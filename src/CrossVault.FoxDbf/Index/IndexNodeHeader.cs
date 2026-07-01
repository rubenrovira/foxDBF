using System.Buffers.Binary;

namespace CrossVault.FoxDbf.Index;

/// <summary>
/// The 12-byte B-tree node header that prefixes every 512-byte CDX/IDX page
/// (plan §C3). Layout: attribute u16 LE @0, key count u16 LE @2,
/// left sibling u32 LE @4, right sibling u32 LE @8. A sibling value of
/// <c>0xFFFFFFFF</c> means "no sibling" and is surfaced as <c>null</c>.
/// </summary>
public readonly struct IndexNodeHeader
{
    /// <summary>The on-disk "no pointer" sentinel for sibling/free-list/root fields.</summary>
    public const uint NoPointer = 0xFFFFFFFF;

    /// <summary>Raw node-attribute word (@0, LE). Bit 0 = root, bit 1 = leaf (else branch).</summary>
    public ushort Attribute { get; }

    /// <summary>Number of keys stored in this node (@2, LE).</summary>
    public ushort KeyCount { get; }

    /// <summary>Byte offset of the left sibling leaf, or null when <c>0xFFFFFFFF</c>.</summary>
    public uint? LeftSibling { get; }

    /// <summary>Byte offset of the right sibling leaf, or null when <c>0xFFFFFFFF</c>.</summary>
    public uint? RightSibling { get; }

    /// <summary>True when the root bit (0x01) is set.</summary>
    public bool IsRoot => (Attribute & 0x01) != 0;

    /// <summary>True when the leaf bit (0x02) is set.</summary>
    public bool IsLeaf => (Attribute & 0x02) != 0;

    /// <summary>True for an interior (branch) node, i.e. the leaf bit is clear.</summary>
    public bool IsBranch => !IsLeaf;

    public IndexNodeHeader(ushort attribute, ushort keyCount, uint? leftSibling, uint? rightSibling)
    {
        Attribute = attribute;
        KeyCount = keyCount;
        LeftSibling = leftSibling;
        RightSibling = rightSibling;
    }

    /// <summary>
    /// Parses the 12-byte node header from the start of a page span.
    /// </summary>
    public static IndexNodeHeader Parse(ReadOnlySpan<byte> page)
    {
        ushort attribute = BinaryPrimitives.ReadUInt16LittleEndian(page);
        ushort keyCount = BinaryPrimitives.ReadUInt16LittleEndian(page[2..]);
        uint left = BinaryPrimitives.ReadUInt32LittleEndian(page[4..]);
        uint right = BinaryPrimitives.ReadUInt32LittleEndian(page[8..]);

        return new IndexNodeHeader(
            attribute,
            keyCount,
            left == NoPointer ? null : left,
            right == NoPointer ? null : right);
    }
}
