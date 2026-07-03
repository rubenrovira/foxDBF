using System.Buffers.Binary;
using CrossVault.FoxDbf.Index;

namespace CrossVault.FoxDbf.Write;

/// <summary>
/// TRUE INCREMENTAL edit of a live compound <c>.cdx</c> B-tree (project-review finding 5.1): insert or
/// delete a single <c>(key, recno)</c> entry in one tag's tree by descending to the target leaf,
/// decoding it (<see cref="CompactLeaf"/>), editing the entry list, and re-encoding via the SAME
/// primitives the bulk <see cref="CdxIndexBuilder"/> uses — so a maintained leaf reads back byte-identical
/// to a fresh build. Leaf overflow SPLITS (a new page is allocated at file end, sibling links + the parent
/// separator are patched, and the split propagates up; a root split grows the tree height and re-points the
/// tag header). Delete allows SPARSE leaves and unlinks a leaf that empties (no merge — VFP tolerates it;
/// REINDEX compacts). Freed / oversized pages are simply leaked at the file end (VFP's cdx grows too).
/// </summary>
/// <remarks>
/// EQUIVALENCE (not byte-identity with a VFP incremental update — page layout is history-dependent): after
/// an edit (a) our own <see cref="CdxTag"/> reader enumerates exactly the expected key→recno set, in order;
/// (b) SEEK finds the new key / no longer finds a removed one; (c) a real VFP9 opens the maintained file and
/// SEEKs correctly (the branch separators = subtree-MAX are maintained, which VFP's descent relies on).
/// The reader/editor never trusts a malformed page beyond the defensive guards inherited from
/// <see cref="IndexFile"/>. Character vs numeric pad (0x20 / 0x00) is supplied per operation by the caller.
/// </remarks>
internal sealed class CdxTreeEditor : IDisposable
{
    private const int PageSize = IndexFile.PageSize;   // 512
    private const uint NoPtr = CdxIndexBuilder.NoPointer;

    private readonly FileStream _rw;
    private readonly IndexFile _reader;   // page reads over the SAME stream (leaveOpen)
    private long _end;                     // next free byte offset (== file length); new pages land here

    public CdxTreeEditor(FileStream rw)
    {
        _rw = rw;
        _reader = IndexFile.Open(rw, leaveOpen: true);
        _end = rw.Length;
    }

    /// <summary>One tag of the compound index: its directory name, its header byte offset (where the
    /// root pointer lives), and the parsed header (root / key length / expressions / flags).</summary>
    public readonly record struct TagHandle(string Name, long HeaderOffset, CdxHeader Header);

    /// <summary>Enumerate every tag in the compound index (name + header offset + parsed header), so the
    /// caller can build a per-tag key computer and drive <see cref="Insert"/> / <see cref="Delete"/>.</summary>
    public List<TagHandle> ReadTags()
    {
        var result = new List<TagHandle>();
        var fileHeader = _reader.ReadCdxHeader(0);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (headerOffset, nameBytes) in
                 IndexTraversal.EnumerateCompact(_reader, fileHeader.Root, fileHeader.KeyLength, isCharacter: true))
        {
            string name = TrimName(nameBytes);
            if (name.Length == 0 || !seen.Add(name))
                continue;
            var hdr = _reader.ReadCdxHeader(headerOffset);
            result.Add(new TagHandle(name, headerOffset, hdr));
        }
        return result;
    }

    // ---- public single-entry operations ----------------------------------------

    /// <summary>Insert <paramref name="key"/> → <paramref name="recno"/> into the tag rooted at the header
    /// at <paramref name="headerOffset"/>, splitting leaves/branches and growing the tree as needed.</summary>
    public void Insert(long headerOffset, byte[] key, uint recno, int keyLen, byte pad, long recnoBasis)
    {
        uint root = ReadTagRoot(headerOffset);
        var res = InsertNode(root, key, recno, keyLen, pad, recnoBasis);
        if (res.Extra is { Count: > 0 })
        {
            // Root split: build a fresh root over [(old-root max, old root)] + the new right siblings.
            var children = new List<CdxIndexBuilder.BranchChild>(1 + res.Extra.Count)
            {
                new(res.Max.Key, res.Max.Recno, root),
            };
            children.AddRange(res.Extra);
            long newRoot = BuildRoot(children, keyLen);
            WriteTagRoot(headerOffset, (uint)newRoot);
        }
    }

    /// <summary>Delete the entry <paramref name="key"/> → <paramref name="recno"/> from the tag rooted at
    /// the header at <paramref name="headerOffset"/> (a no-op when the exact entry is absent).</summary>
    public void Delete(long headerOffset, byte[] key, uint recno, int keyLen, byte pad, long recnoBasis)
    {
        uint root = ReadTagRoot(headerOffset);
        var res = DeleteNode(root, key, recno, keyLen, pad, recnoBasis);
        if (res.Emptied)
        {
            // The whole tag emptied → install a fresh empty leaf as the root.
            var img = CdxIndexBuilder.PackLeafImages(Array.Empty<(byte[], uint)>(), keyLen, pad, recnoBasis)[0];
            PatchSiblings(img.Page, NoPtr, NoPtr);
            PatchRoot(img.Page, isRoot: true);
            long off = _end; _end += PageSize;
            WritePage(off, img.Page);
            WriteTagRoot(headerOffset, (uint)off);
        }
    }

    /// <summary>True when the tag already stores <paramref name="key"/> (UNIQUE-tag pre-insert check —
    /// VFP keeps only the first record per distinct key in a UNIQUE tag). Descends to the leaf where the
    /// key would sort and scans forward while keys stay equal.</summary>
    public bool ContainsKey(long headerOffset, byte[] key, int keyLen, byte pad)
    {
        uint root = ReadTagRoot(headerOffset);
        long leaf = DescendToLeaf(root, key, keyLen);
        if (leaf < 0)
            return false;
        var visited = new HashSet<long>();
        long cur = leaf;
        while (cur >= 0 && visited.Add(cur))
        {
            foreach (var e in ReadLeafList(cur, keyLen, pad))
            {
                int c = CompareBytes(e.Key, key);
                if (c == 0) return true;
                if (c > 0) return false;   // passed where the key would sort
            }
            var hdr = _reader.ReadNodeHeader(cur);
            cur = hdr?.RightSibling is uint r ? r : -1;
        }
        return false;
    }

    public void Flush() => _rw.Flush();
    public void Dispose() => _reader.Dispose();   // leaveOpen: does not close _rw (owned by the caller)

    // ---- recursive insert ------------------------------------------------------

    private readonly record struct NodeMax(byte[] Key, uint Recno);

    private sealed class InsertResult
    {
        public NodeMax Max;
        public List<CdxIndexBuilder.BranchChild>? Extra;   // new RIGHT siblings created by a split
    }

    private InsertResult InsertNode(long nodeOff, byte[] key, uint recno, int keyLen, byte pad, long recnoBasis)
    {
        var hdr = _reader.ReadNodeHeader(nodeOff)
            ?? throw new InvalidDataException($"CDX node at {nodeOff} is unreadable.");
        uint? left = hdr.LeftSibling, right = hdr.RightSibling;
        bool isRoot = hdr.IsRoot;

        if (hdr.IsLeaf)
        {
            var entries = ReadLeafList(nodeOff, keyLen, pad);
            int pos = entries.Count;
            for (int i = 0; i < entries.Count; i++)
                if (CompareEntry(entries[i].Key, entries[i].Recno, key, recno) > 0) { pos = i; break; }
            entries.Insert(pos, (key, recno));

            var leafImages = CdxIndexBuilder.PackLeafImages(entries, keyLen, pad, recnoBasis);
            var images = new List<(byte[] Page, byte[] MaxKey, uint MaxRecno)>(leafImages.Count);
            foreach (var li in leafImages)
                images.Add((li.Page, li.MaxKey, li.MaxRecno));
            return WriteImagesInPlace(nodeOff, images, left, right, isRoot);
        }

        var kids = ReadBranchList(nodeOff, keyLen);
        int ci = ChooseChild(kids, key, recno);
        var childRes = InsertNode(kids[ci].Child, key, recno, keyLen, pad, recnoBasis);
        kids[ci] = new CdxIndexBuilder.BranchChild(childRes.Max.Key, childRes.Max.Recno, kids[ci].Child);
        if (childRes.Extra is { Count: > 0 })
            kids.InsertRange(ci + 1, childRes.Extra);

        var branchImages = SplitBranch(kids, keyLen);
        return WriteImagesInPlace(nodeOff, branchImages, left, right, isRoot);
    }

    /// <summary>Assign page offsets to <paramref name="images"/> (image 0 reuses <paramref name="nodeOff"/>,
    /// the rest are fresh pages at file end), patch sibling links + the root bit, write them, splice them into
    /// the sibling chain (patch the original right neighbour's LEFT pointer), and report the new max + any new
    /// right siblings for the parent.</summary>
    private InsertResult WriteImagesInPlace(long nodeOff,
        List<(byte[] Page, byte[] MaxKey, uint MaxRecno)> images, uint? origLeft, uint? origRight, bool isRoot)
    {
        int n = images.Count;
        var pages = new long[n];
        pages[0] = nodeOff;
        for (int i = 1; i < n; i++) { pages[i] = _end; _end += PageSize; }

        for (int i = 0; i < n; i++)
        {
            uint lp = (uint)(i == 0 ? (origLeft ?? NoPtr) : pages[i - 1]);
            uint rp = (uint)(i == n - 1 ? (origRight ?? NoPtr) : pages[i + 1]);
            PatchSiblings(images[i].Page, lp, rp);
            PatchRoot(images[i].Page, isRoot && n == 1);
            WritePage(pages[i], images[i].Page);
        }

        if (n > 1 && origRight is uint orr)
        {
            var rpg = ReadPage(orr);
            PatchLeft(rpg, (uint)pages[n - 1]);
            WritePage(orr, rpg);
        }

        var res = new InsertResult { Max = new NodeMax(images[0].MaxKey, images[0].MaxRecno) };
        if (n > 1)
        {
            res.Extra = new List<CdxIndexBuilder.BranchChild>(n - 1);
            for (int i = 1; i < n; i++)
                res.Extra.Add(new CdxIndexBuilder.BranchChild(images[i].MaxKey, images[i].MaxRecno, (uint)pages[i]));
        }
        return res;
    }

    private static List<(byte[] Page, byte[] MaxKey, uint MaxRecno)> SplitBranch(
        List<CdxIndexBuilder.BranchChild> kids, int keyLen)
    {
        int cap = CdxIndexBuilder.BranchCapacity(keyLen);
        var images = new List<(byte[] Page, byte[] MaxKey, uint MaxRecno)>();
        for (int i = 0; i < kids.Count; i += cap)
        {
            int take = Math.Min(cap, kids.Count - i);
            var grp = kids.GetRange(i, take);
            var page = CdxIndexBuilder.EncodeBranchPage(grp, keyLen, attr: 0, left: NoPtr, right: NoPtr);
            images.Add((page, grp[^1].SepKey, grp[^1].SepRecno));
        }
        return images;
    }

    private long BuildRoot(List<CdxIndexBuilder.BranchChild> children, int keyLen)
    {
        int cap = CdxIndexBuilder.BranchCapacity(keyLen);
        var level = children;
        while (level.Count > cap)
            level = WriteBranchLevel(level, keyLen, cap);

        long off = _end; _end += PageSize;
        var page = CdxIndexBuilder.EncodeBranchPage(level, keyLen, CdxIndexBuilder.AttrRootBit, NoPtr, NoPtr);
        WritePage(off, page);
        return off;
    }

    private List<CdxIndexBuilder.BranchChild> WriteBranchLevel(
        List<CdxIndexBuilder.BranchChild> level, int keyLen, int cap)
    {
        var groups = new List<List<CdxIndexBuilder.BranchChild>>();
        for (int i = 0; i < level.Count; i += cap)
            groups.Add(level.GetRange(i, Math.Min(cap, level.Count - i)));

        var offs = new long[groups.Count];
        for (int i = 0; i < groups.Count; i++) { offs[i] = _end; _end += PageSize; }

        var result = new List<CdxIndexBuilder.BranchChild>(groups.Count);
        for (int i = 0; i < groups.Count; i++)
        {
            uint lp = (uint)(i > 0 ? offs[i - 1] : NoPtr);
            uint rp = (uint)(i < groups.Count - 1 ? offs[i + 1] : NoPtr);
            var page = CdxIndexBuilder.EncodeBranchPage(groups[i], keyLen, attr: 0, left: lp, right: rp);
            WritePage(offs[i], page);
            result.Add(new CdxIndexBuilder.BranchChild(groups[i][^1].SepKey, groups[i][^1].SepRecno, (uint)offs[i]));
        }
        return result;
    }

    // ---- recursive delete ------------------------------------------------------

    private sealed class DeleteResult
    {
        public bool Emptied;
        public NodeMax Max;
    }

    private DeleteResult DeleteNode(long nodeOff, byte[] key, uint recno, int keyLen, byte pad, long recnoBasis)
    {
        var hdr = _reader.ReadNodeHeader(nodeOff)
            ?? throw new InvalidDataException($"CDX node at {nodeOff} is unreadable.");
        uint? left = hdr.LeftSibling, right = hdr.RightSibling;
        bool isRoot = hdr.IsRoot;

        if (hdr.IsLeaf)
        {
            var entries = ReadLeafList(nodeOff, keyLen, pad);
            int idx = -1;
            for (int i = 0; i < entries.Count; i++)
                if (entries[i].Recno == recno && CompareBytes(entries[i].Key, key) == 0) { idx = i; break; }
            if (idx < 0)
                return new DeleteResult { Emptied = false, Max = LeafMax(entries) };   // absent: leave as-is

            entries.RemoveAt(idx);
            if (entries.Count == 0 && !isRoot)
            {
                Unlink(left, right);
                return new DeleteResult { Emptied = true };
            }

            // A removal FREES space, so with a correctly-sized recnoBasis the leaf re-packs into exactly ONE
            // image. But an OVERSIZED recnoBasis (a record count — or, historically, a file length — that crossed
            // an 8-bit band since this leaf was last packed) widens BytesPerEntry and can push a near-full leaf
            // over a page: PackLeafImages then returns MULTIPLE images. Taking image[0] and dropping the rest
            // would SILENTLY LOSE entries — e.g. sibling tags in the compound-index directory tree
            // (project-review 5.6 MUST-FIX). The pack precedes every page write, so THROW here, before any
            // WritePage, leaving the on-disk tree untouched by this node: the tag-DDL fast paths catch it and
            // fall back to a consistent whole-file rebuild; the 5.1 row-maintenance path catches it and durably
            // invalidates + REINDEXes. Callers pass a DATA-derived (non-oversized) basis, so this is a
            // belt-and-braces guard rather than a normal code path.
            var imgs = CdxIndexBuilder.PackLeafImages(entries, keyLen, pad, recnoBasis);
            if (imgs.Count > 1)
                throw new InvalidOperationException(
                    $"CDX leaf delete re-packed into {imgs.Count} pages (recno geometry widened past the packed " +
                    "leaf); refusing to drop entries — the caller must fall back to a full rebuild.");
            var img = imgs[0];
            PatchSiblings(img.Page, left ?? NoPtr, right ?? NoPtr);
            PatchRoot(img.Page, isRoot);
            WritePage(nodeOff, img.Page);
            return new DeleteResult { Emptied = false, Max = new NodeMax(img.MaxKey, img.MaxRecno) };
        }

        var kids = ReadBranchList(nodeOff, keyLen);
        int ci = ChooseChild(kids, key, recno);
        var childRes = DeleteNode(kids[ci].Child, key, recno, keyLen, pad, recnoBasis);
        if (childRes.Emptied)
        {
            kids.RemoveAt(ci);
            if (kids.Count == 0)
            {
                if (isRoot)
                    return new DeleteResult { Emptied = true };
                Unlink(left, right);
                return new DeleteResult { Emptied = true };
            }
        }
        else
        {
            kids[ci] = new CdxIndexBuilder.BranchChild(childRes.Max.Key, childRes.Max.Recno, kids[ci].Child);
        }

        var page = CdxIndexBuilder.EncodeBranchPage(
            kids, keyLen, isRoot ? CdxIndexBuilder.AttrRootBit : (ushort)0, left ?? NoPtr, right ?? NoPtr);
        WritePage(nodeOff, page);
        return new DeleteResult { Emptied = false, Max = new NodeMax(kids[^1].SepKey, kids[^1].SepRecno) };
    }

    // ---- descent / comparison --------------------------------------------------

    /// <summary>Descend from <paramref name="root"/> to the leaf that would hold <paramref name="key"/>
    /// (first child whose separator MAX-key ≥ the key). Returns the leaf offset, or -1 on a malformed tree.</summary>
    private long DescendToLeaf(long root, byte[] key, int keyLen)
    {
        long cur = root;
        var seen = new HashSet<long>();
        while (seen.Add(cur))
        {
            var hdr = _reader.ReadNodeHeader(cur);
            if (hdr is null) return -1;
            if (hdr.Value.IsLeaf) return cur;
            var kids = ReadBranchList(cur, keyLen);
            if (kids.Count == 0) return -1;
            int j = 0;
            while (j < kids.Count && CompareBytes(kids[j].SepKey, key) < 0) j++;
            if (j >= kids.Count) j = kids.Count - 1;
            cur = kids[j].Child;
        }
        return -1;
    }

    /// <summary>Choose the child that (by subtree MAX separator) covers <c>(key, recno)</c>: the first
    /// child whose (sepKey, sepRecno) ≥ (key, recno); the right-most when the entry sorts after all.</summary>
    private static int ChooseChild(List<CdxIndexBuilder.BranchChild> kids, byte[] key, uint recno)
    {
        for (int i = 0; i < kids.Count; i++)
            if (CompareEntry(key, recno, kids[i].SepKey, kids[i].SepRecno) <= 0)
                return i;
        return kids.Count - 1;
    }

    private static int CompareEntry(byte[] k1, uint r1, byte[] k2, uint r2)
    {
        int c = CompareBytes(k1, k2);
        return c != 0 ? c : r1.CompareTo(r2);
    }

    /// <summary>Unsigned, shorter-sorts-first byte comparison — the CDX key order.</summary>
    private static int CompareBytes(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int d = a[i] - b[i];
            if (d != 0) return d < 0 ? -1 : 1;
        }
        return a.Length.CompareTo(b.Length);
    }

    private static NodeMax LeafMax(List<(byte[] Key, uint Recno)> entries)
        => entries.Count == 0 ? new NodeMax(Array.Empty<byte>(), 0) : new NodeMax(entries[^1].Key, entries[^1].Recno);

    // ---- node decoding ---------------------------------------------------------

    private List<(byte[] Key, uint Recno)> ReadLeafList(long off, int keyLen, byte pad)
    {
        bool isChar = pad == 0x20;
        var raw = _reader.ReadLeafEntries(off, keyLen, isChar);
        var list = new List<(byte[], uint)>(raw.Count);
        foreach (var e in raw)
            list.Add((e.Key, e.RecordNumber));
        return list;
    }

    private List<CdxIndexBuilder.BranchChild> ReadBranchList(long off, int keyLen)
    {
        var raw = _reader.ReadBranchEntries(off, keyLen);
        var list = new List<CdxIndexBuilder.BranchChild>(raw.Count);
        foreach (var e in raw)
            list.Add(new CdxIndexBuilder.BranchChild(e.Key, e.RecordNumber, e.ChildPointer));
        return list;
    }

    // ---- sibling-chain / page plumbing -----------------------------------------

    private void Unlink(uint? left, uint? right)
    {
        if (left is uint L) { var p = ReadPage(L); PatchRight(p, right ?? NoPtr); WritePage(L, p); }
        if (right is uint R) { var p = ReadPage(R); PatchLeft(p, left ?? NoPtr); WritePage(R, p); }
    }

    private static void PatchSiblings(byte[] page, uint left, uint right)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), left);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8), right);
    }

    private static void PatchLeft(byte[] page, uint left)
        => BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), left);

    private static void PatchRight(byte[] page, uint right)
        => BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8), right);

    private static void PatchRoot(byte[] page, bool isRoot)
    {
        ushort attr = BinaryPrimitives.ReadUInt16LittleEndian(page.AsSpan(0));
        attr = (ushort)(isRoot ? attr | CdxIndexBuilder.AttrRootBit : attr & ~CdxIndexBuilder.AttrRootBit);
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(0), attr);
    }

    private byte[] ReadPage(long off)
        => _reader.ReadPage(off) ?? throw new InvalidDataException($"CDX page at {off} is unreadable.");

    private void WritePage(long off, byte[] page)
    {
        _rw.Seek(off, SeekOrigin.Begin);
        _rw.Write(page, 0, PageSize);
        if (off + PageSize > _end)
            _end = off + PageSize;
    }

    private uint ReadTagRoot(long headerOffset)
    {
        var page = ReadPage(headerOffset);
        return BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(0));
    }

    private void WriteTagRoot(long headerOffset, uint root)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, root);
        _rw.Seek(headerOffset, SeekOrigin.Begin);
        _rw.Write(b);
    }

    private static string TrimName(byte[] raw)
    {
        int len = raw.Length;
        while (len > 0 && (raw[len - 1] == 0x20 || raw[len - 1] == 0x00))
            len--;
        return len == 0 ? string.Empty : System.Text.Encoding.ASCII.GetString(raw, 0, len);
    }
}
