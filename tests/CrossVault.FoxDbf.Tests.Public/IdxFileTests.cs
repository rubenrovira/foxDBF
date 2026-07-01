using System.Buffers.Binary;
using System.Text;
using CrossVault.FoxDbf.Index;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Pin-down tests for <see cref="IdxFile"/> — a standalone single-tag legacy
/// <c>.idx</c> (plan §C7 phase C-1). No real <c>.idx</c> fixture ships with the
/// repo, so these build SYNTHETIC uncompressed indexes in memory to the documented
/// layout and assert ordered key→recno enumeration, interior descent, the
/// big-endian pointer trap, and defensive behaviour on malformed input.
///
/// Layout (512-byte pages):
///   page 0  = header: Root u32 LE @0, FreeList @4, EOF @8, KeyLength u16 LE @12,
///             Options @14, KEY expr ASCII @16.
///   node    = attribute u16 LE @0 (LEAF when attribute &gt; 1), key count u16 LE @2,
///             left sibling u32 LE @4, right sibling u32 LE @8, then key-count
///             entries of [key(KeyLength)] [pointer u32 BIG-endian]. In a leaf the
///             pointer is a DBF record number; in an interior node it is a child
///             node byte offset.
/// </summary>
public sealed class IdxFileTests
{
    private const int Page = 512;
    private const uint NoPtr = 0xFFFFFFFF;

    // ---------------------------------------------------------------------
    //  Single root-leaf: entries enumerate in stored (key) order.
    // ---------------------------------------------------------------------

    [Fact]
    public void Enumerate_SingleRootLeaf_YieldsRecnosInKeyOrder()
    {
        // keyLen 4; three keys A<B<C with out-of-order recnos 30,10,20.
        var file = new SyntheticIdx(keyLength: 4, root: 512, keyExpr: "NAME");
        file.AddNode(offset: 512, attribute: 2 /*leaf*/, left: NoPtr, right: NoPtr,
            entries: new (string, uint)[] { ("AAAA", 30), ("BBBB", 10), ("CCCC", 20) });

        using var idx = IdxFile.Open(new MemoryStream(file.Build()));

        Assert.Equal(4, idx.KeyLength);
        Assert.Equal(512u, idx.Header.Root);
        Assert.Equal("NAME", idx.Header.KeyExpression);

        var entries = idx.EnumerateEntries().ToArray();
        Assert.Equal(new uint[] { 30, 10, 20 }, entries.Select(e => e.RecordNumber).ToArray());
        Assert.Equal("AAAA", Encoding.ASCII.GetString(entries[0].Key));
        Assert.Equal("CCCC", Encoding.ASCII.GetString(entries[2].Key));
    }

    // ---------------------------------------------------------------------
    //  Interior root over two chained leaves: descent + right-sibling walk.
    // ---------------------------------------------------------------------

    [Fact]
    public void Enumerate_InteriorRoot_DescendsLeftThenWalksRightSiblings()
    {
        var file = new SyntheticIdx(keyLength: 4, root: 512, keyExpr: "NAME");
        // interior root @512: child[0]->1024 (separator "BBBB"), child[1]->1536.
        file.AddNode(offset: 512, attribute: 0 /*interior*/, left: NoPtr, right: NoPtr,
            entries: new (string, uint)[] { ("BBBB", 1024), ("DDDD", 1536) });
        // leaf1 @1024 -> right sibling 1536.
        file.AddNode(offset: 1024, attribute: 2, left: NoPtr, right: 1536,
            entries: new (string, uint)[] { ("AAAA", 1), ("BBBB", 2) });
        // leaf2 @1536 -> left sibling 1024.
        file.AddNode(offset: 1536, attribute: 2, left: 1024, right: NoPtr,
            entries: new (string, uint)[] { ("CCCC", 3), ("DDDD", 4) });

        using var idx = IdxFile.Open(new MemoryStream(file.Build()));

        var recnos = idx.EnumerateEntries().Select(e => e.RecordNumber).ToArray();
        Assert.Equal(new uint[] { 1, 2, 3, 4 }, recnos);
    }

    [Fact]
    public void Enumerate_RecordPointer_IsBigEndian_NotLittleEndian()
    {
        // recno 0x00000201 (513) stored big-endian must decode as 513, not the
        // little-endian misreading 0x01020000.
        var file = new SyntheticIdx(keyLength: 2, root: 512, keyExpr: "K");
        file.AddNode(offset: 512, attribute: 3 /*leaf, last page*/, left: NoPtr, right: NoPtr,
            entries: new (string, uint)[] { ("AA", 513) });

        using var idx = IdxFile.Open(new MemoryStream(file.Build()));

        var e = idx.EnumerateEntries().Single();
        Assert.Equal(513u, e.RecordNumber);
        Assert.NotEqual(0x01020000u, e.RecordNumber);
    }

    // ---------------------------------------------------------------------
    //  Empty + malformed: never throw.
    // ---------------------------------------------------------------------

    [Fact]
    public void Enumerate_EmptyRootLeaf_YieldsNothing_DoesNotThrow()
    {
        var file = new SyntheticIdx(keyLength: 4, root: 512, keyExpr: "NAME");
        file.AddNode(offset: 512, attribute: 2, left: NoPtr, right: NoPtr,
            entries: Array.Empty<(string, uint)>());

        using var idx = IdxFile.Open(new MemoryStream(file.Build()));
        Assert.Empty(idx.EnumerateEntries());
    }

    [Fact]
    public void Enumerate_RootPointerPastEof_YieldsNothing_DoesNotThrow()
    {
        // Header only, root points far past EOF.
        var header = new byte[Page];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), 999_999); // root
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), 4);      // key length

        using var idx = IdxFile.Open(new MemoryStream(header));
        Assert.Empty(idx.EnumerateEntries());
    }

    [Fact]
    public void Enumerate_TruncatedFile_DoesNotThrow()
    {
        // 40 bytes: not even a full header page.
        using var idx = IdxFile.Open(new MemoryStream(new byte[40]));
        Assert.Empty(idx.EnumerateEntries());
    }

    [Fact]
    public void Enumerate_CyclicSiblingChain_TerminatesAndDoesNotThrow()
    {
        // Two leaves whose right siblings point at each other: a corrupt cycle.
        // Enumeration must terminate (visited-set guard) rather than loop forever.
        var file = new SyntheticIdx(keyLength: 2, root: 512, keyExpr: "K");
        file.AddNode(offset: 512, attribute: 2, left: NoPtr, right: 1024,
            entries: new (string, uint)[] { ("AA", 1) });
        file.AddNode(offset: 1024, attribute: 2, left: 512, right: 512 /*cycle!*/,
            entries: new (string, uint)[] { ("BB", 2) });

        using var idx = IdxFile.Open(new MemoryStream(file.Build()));

        // Must finish; we don't pin an exact count, only that it returns and the
        // first two records are the readable ones.
        var recnos = idx.EnumerateEntries().Select(e => e.RecordNumber).Take(10).ToArray();
        Assert.Contains(1u, recnos);
        Assert.Contains(2u, recnos);
    }

    // ---------------------------------------------------------------------
    //  Synthetic uncompressed .idx builder.
    // ---------------------------------------------------------------------

    private sealed class SyntheticIdx
    {
        private readonly int _keyLength;
        private readonly uint _root;
        private readonly string _keyExpr;
        private readonly Dictionary<long, byte[]> _nodes = new();

        public SyntheticIdx(int keyLength, uint root, string keyExpr)
        {
            _keyLength = keyLength;
            _root = root;
            _keyExpr = keyExpr;
        }

        public void AddNode(long offset, ushort attribute, uint left, uint right,
            (string key, uint pointer)[] entries)
        {
            var node = new byte[Page];
            BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(0), attribute);
            BinaryPrimitives.WriteUInt16LittleEndian(node.AsSpan(2), (ushort)entries.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(4), left);
            BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(8), right);

            int o = 12;
            foreach (var (key, pointer) in entries)
            {
                var kb = Encoding.ASCII.GetBytes(key);
                Array.Copy(kb, 0, node, o, Math.Min(kb.Length, _keyLength));
                BinaryPrimitives.WriteUInt32BigEndian(node.AsSpan(o + _keyLength, 4), pointer);
                o += _keyLength + 4;
            }
            _nodes[offset] = node;
        }

        public byte[] Build()
        {
            long max = Page;
            foreach (var off in _nodes.Keys)
                max = Math.Max(max, off + Page);

            var buf = new byte[max];

            // Header page 0.
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0), _root);
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), NoPtr);  // free list
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(8), (uint)max); // eof
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(12), (ushort)_keyLength);
            Encoding.ASCII.GetBytes(_keyExpr).CopyTo(buf, 16);

            foreach (var (off, node) in _nodes)
                Array.Copy(node, 0, buf, off, Page);

            return buf;
        }
    }
}
