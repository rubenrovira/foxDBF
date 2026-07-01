using System.Buffers.Binary;
using System.Text;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.Write;

/// <summary>
/// §D7 — BULK build / rebuild (REINDEX) of a compact compound <c>.cdx</c>: the
/// inverse of the Teil C reader. Computes every key via the expression engine,
/// sorts the (key, recno) pairs by unsigned key bytes (recno tiebreak), bulk-loads
/// a balanced B-tree into 512-byte pages (compact leaves + branch nodes), wires the
/// tag headers + tag directory, and writes the file.
/// </summary>
/// <remarks>
/// The on-disk format is the exact inverse of <see cref="CompactLeaf"/>.Decode /
/// <see cref="CdxHeader.Parse"/> / <see cref="IndexFile.ReadBranchEntries"/>, and the
/// leaf bit-width algorithm mirrors CodeBase <c>r4reinde.c</c> (validated byte-for-byte
/// against VFP9 fixtures such as <c>vfp_test/ligtest.CDX</c>). Page layout (per VFP9):
/// page0 = file header, page1 = its expression pool, then for each tag a header page +
/// pool page + its B-tree pages, and finally the tag directory.
/// </remarks>
internal static class CdxIndexBuilder
{
    private const int Page = IndexFile.PageSize;       // 512
    private const int StdHeader = 12;                  // node std header (attr/keys/left/right)
    private const int NodeInfo = 12;                   // compact-leaf info block (@12..23)
    private const int LeafDataStart = StdHeader + NodeInfo; // 24
    private const uint NoPtr = 0xFFFFFFFF;

    // Node attribute bits (CodeBase d4data.h: 0=index/branch, 1=root, 2=leaf, 4=data-tag leaf).
    private const ushort AttrRoot = 0x01;
    private const ushort AttrLeaf = 0x02;
    private const ushort AttrData = 0x04;

    /// <summary>A single source record to index: its 1-based record number and decoded row.</summary>
    public readonly record struct BuildRow(int RecNo, DbfRecord Record);

    /// <summary>
    /// Build a fresh compound <c>.cdx</c> at <paramref name="cdxPath"/> covering every
    /// definition in <paramref name="tags"/> over <paramref name="rows"/> (the live,
    /// non-deleted-aware record set of <paramref name="schema"/>). Overwrites any existing
    /// file at the path.
    /// </summary>
    public static void Build(string cdxPath, DbfTable schema, IReadOnlyList<BuildRow> rows, IReadOnlyList<CdxTagDefinition> tags)
    {
        var sink = new PageSink();
        sink.Reserve();           // page 0: file header (filled last)
        sink.Reserve();           // page 1: file-header expression pool (2 NUL bytes; already zero)

        // Plan every per-tag tree up front (keys, geometry, header metadata, page footprint).
        var plans = new List<TagPlan>(tags.Count);
        foreach (var def in tags)
            plans.Add(PlanTag(schema, rows, def));

        // VFP places the tag-DIRECTORY root EARLY — on page 2 (offset 1024), BEFORE the per-tag
        // trees — so the real VFP9 runtime can open a .dbc that uses this .dcx (error 1552 otherwise).
        // The directory leaf stores each tag NAME (10-byte key) → that tag's HEADER byte offset as its
        // "recno". Those offsets depend on where the tags land, which depends on how many pages the
        // directory itself needs — a small fixed point (1 iteration in every realistic case: the
        // directory fits in a single page for up to ~37 tags). We solve it before reserving anything.
        long basisDir = (long)Math.Max(1, tags.Count) * 1024; // VFP: nTags*1024 sizes the recno field
        List<Entry> dirEntries = new(tags.Count);
        LeafGeometry dirGeom = default;
        int dirPages = 1;
        while (true)
        {
            dirEntries = new List<Entry>(tags.Count);
            int page = 2 + dirPages;                       // first page after [hdr][pool][directory…]
            foreach (var p in plans)
            {
                long headerOffset = (long)page * Page;
                dirEntries.Add(new Entry(MakeNameKey(p.Name), (uint)headerOffset));
                page += 2 + p.TreePageCount;               // header + pool + B-tree pages
            }
            dirEntries.Sort(EntryComparer.Instance);
            dirGeom = LeafGeometry.For(keyLen: 10, recnoBasis: basisDir, entries: dirEntries);
            int need = CountTreePages(dirEntries, keyLen: 10, pad: 0x20, dirGeom);
            if (need == dirPages) break;
            dirPages = need;
        }

        // Reserve the directory page(s) at page 2 BEFORE the tags, then lay every tag down after them.
        int dirStart = sink.Reserve();                     // page 2
        for (int i = 1; i < dirPages; i++) sink.Reserve();

        foreach (var p in plans)
        {
            int headerIndex = sink.Reserve();              // tag header page
            sink.Reserve();                                // its expression-pool page
            long headerOffset = (long)headerIndex * Page;
            uint treeRoot = BuildTree(sink, () => sink.Reserve(), p.Entries, p.KeyLen, p.Pad, isDataTag: true, p.Geom);

            WriteHeaderCommon(sink.At(headerIndex), root: treeRoot, keyLen: p.KeyLen, options: p.Options,
                signature: p.Signature, sortOrder: p.SortOrder, descending: p.Descending,
                keyExpr: p.KeyExpr, forExpr: p.ForExpr);
            WriteExpressionPool(sink, headerOffset, p.KeyExpr, p.ForExpr);
        }

        // Fill the reserved directory page(s) with the directory tree (leaves first, then branches —
        // the same allocation order CountTreePages counted, so the pre-reserved range matches exactly).
        var dirRange = Enumerable.Range(dirStart, dirPages).GetEnumerator();
        uint dirRoot = BuildTree(sink, () => { dirRange.MoveNext(); return dirRange.Current; },
            dirEntries, keyLen: 10, pad: 0x20, isDataTag: false, dirGeom);

        // File header (page 0) + its pool (page 1: keyExprLen=1, forExprLen=1 → 2 NUL bytes).
        var fileHeader = sink.At(0);
        WriteHeaderCommon(fileHeader, root: dirRoot, keyLen: 10,
            options: (byte)0xE0,           // structural | compound | compact
            signature: 1, sortOrder: null, descending: false,
            keyExpr: string.Empty, forExpr: string.Empty);

        File.WriteAllBytes(cdxPath, sink.ToArray());
    }

    // ---- per-tag build ---------------------------------------------------------

    /// <summary>The fully resolved plan for one tag: its sorted entries, leaf geometry, page
    /// footprint, and every value needed to emit its header — computed BEFORE any page is placed.</summary>
    private sealed class TagPlan
    {
        public required string Name;
        public required List<Entry> Entries;
        public LeafGeometry Geom;
        public int KeyLen;
        public byte Pad;
        public int TreePageCount;
        public byte Options;
        public byte Signature;
        public string? SortOrder;
        public bool Descending;
        public string KeyExpr = string.Empty;
        public string ForExpr = string.Empty;
    }

    private static TagPlan PlanTag(DbfTable schema, IReadOnlyList<BuildRow> rows, CdxTagDefinition def)
    {
        var collation = VfpCollations.ByName(def.Collation);
        var ctx = new EvaluationContext { Collation = collation };

        var (keyType, baseLen, isChar) = ResolveKey(schema, def.KeyExpression);
        int keyLen = CollatedKeyLength(baseLen, isChar, collation);
        byte pad = isChar ? (byte)0x20 : (byte)0x00;

        var keyFn = VfpExpression.Parse(def.KeyExpression).Compile(ctx);
        Func<IRowContext, VfpValue>? forFn = !string.IsNullOrWhiteSpace(def.ForExpression)
            ? VfpExpression.Parse(def.ForExpression!).Compile(ctx)
            : null;

        int recCount = rows.Count;
        var entries = new List<Entry>(rows.Count);
        foreach (var row in rows)
        {
            if (row.Record.IsDeleted)
                continue;
            var rc = new RowContext(row.Record, row.RecNo, recCount);

            if (forFn is not null)
            {
                var f = forFn(rc);
                if (!(f.Type == VfpType.Logical && f.AsLogical))
                    continue;
            }

            var value = keyFn(rc);
            byte[] keyBytes = EncodeKey(value, keyType, keyLen, collation, pad);
            entries.Add(new Entry(keyBytes, (uint)row.RecNo));
        }

        entries.Sort(EntryComparer.Instance);

        if (def.Unique)
            entries = DropDuplicateKeys(entries);

        long recnoBasis = Math.Max(1, recCount);
        var geom = LeafGeometry.For(keyLen, recnoBasis, entries);

        byte options = (byte)0x60; // compound | compact
        if (def.Unique) options |= 0x01;
        bool hasFor = !string.IsNullOrWhiteSpace(def.ForExpression);
        if (hasFor) options |= 0x08;

        // Tag signature (byte 15): 0x01 for an IDENTITY/MACHINE collation, 0x02 only for a real
        // named (GENERAL, …) weight collation. Verified against vfp_test/dbctest/ref.DCX (both DBC
        // tags MACHINE ⇒ 0x01) and vfp_test/ligtest.CDX (GENERAL ⇒ 0x02). Deriving it from the
        // collation (rather than the old hardcoded 2) makes the built .DCX byte-match VFP9 and fixes
        // every caller (the DBC builder, DbfWriter.CreateTag, Reindex) at once.
        byte tagSig = IsIdentityCollation(collation) ? (byte)1 : (byte)2;

        return new TagPlan
        {
            Name = def.Name,
            Entries = entries,
            Geom = geom,
            KeyLen = keyLen,
            Pad = pad,
            TreePageCount = CountTreePages(entries, keyLen, pad, geom),
            Options = options,
            Signature = tagSig,
            SortOrder = collation.Name,
            Descending = def.Descending,
            KeyExpr = def.KeyExpression ?? string.Empty,
            ForExpr = hasFor ? def.ForExpression! : string.Empty,
        };
    }

    // ---- B-tree bulk loader ----------------------------------------------------

    /// <summary>
    /// Count the pages a <see cref="BuildTree"/> for <paramref name="entries"/> would allocate (leaves
    /// first, then each branch level), WITHOUT writing anything — used to size the tag directory's
    /// pre-reserved page range up front. Mirrors the allocation order of <see cref="BuildTree"/> exactly.
    /// </summary>
    private static int CountTreePages(List<Entry> entries, int keyLen, byte pad, LeafGeometry geom)
    {
        var leaves = PackLeaves(entries, keyLen, pad, geom);
        int total = leaves.Count;
        if (leaves.Count <= 1) return total; // a single leaf is also the root (no branch level)

        int entrySize = keyLen + 8;
        int maxPer = Math.Max(1, (Page - StdHeader) / entrySize);
        int level = leaves.Count;
        while (level > 1)
        {
            int nodes = (level + maxPer - 1) / maxPer;
            total += nodes;
            level = nodes;
        }
        return total;
    }

    /// <summary>Build the compact B-tree for <paramref name="entries"/> and return the root page offset.
    /// Pages are taken from <paramref name="alloc"/> (either fresh reservations or a pre-reserved range).</summary>
    private static uint BuildTree(PageSink sink, Func<int> alloc, List<Entry> entries, int keyLen, byte pad, bool isDataTag, LeafGeometry geom)
    {
        var leaves = PackLeaves(entries, keyLen, pad, geom);

        // Assign page offsets to every leaf first (so siblings can reference each other).
        foreach (var leaf in leaves)
            leaf.Index = alloc();

        // The 0x04 "data" leaf-attribute bit is NOT a per-tag property — it is a function of the leaf
        // GEOMETRY: VFP9 sets it exactly when the record-number field is narrower than a full byte
        // (RecnoBits < 8). Verified across vfp_test/dbctest/ref.DCX (OBJECTNAME RecnoBits=8 ⇒ 0x03,
        // OBJECTTYPE RecnoBits=6 ⇒ 0x07), vfp_test/ligtest.CDX (RecnoBits=6 ⇒ 0x07) and every TasTrade
        // .cdx (RecnoBits≥8 ⇒ 0x03/0x02). The tag directory (RecnoBits=16) therefore correctly clears
        // it. The old isDataTag heuristic mis-set it on wide-recno data tags; this geometry rule is exact.
        ushort leafExtra = (ushort)(geom.RecnoBits < 8 ? AttrData : 0);

        // Single leaf → it is also the root (root | leaf).
        if (leaves.Count == 1)
        {
            var only = leaves[0];
            WriteLeaf(sink.At(only.Index), only, keyLen, geom, (ushort)(AttrRoot | AttrLeaf | leafExtra),
                left: NoPtr, right: NoPtr);
            return (uint)(only.Index * Page);
        }

        for (int i = 0; i < leaves.Count; i++)
        {
            var leaf = leaves[i];
            uint left = i > 0 ? (uint)(leaves[i - 1].Index * Page) : NoPtr;
            uint right = i < leaves.Count - 1 ? (uint)(leaves[i + 1].Index * Page) : NoPtr;
            WriteLeaf(sink.At(leaf.Index), leaf, keyLen, geom, (ushort)(AttrLeaf | leafExtra), left, right);
        }

        // Build branch levels until a single root node remains.
        var level = leaves
            .Select(l => new ChildRef((uint)(l.Index * Page), l.LastKey, l.LastRecno))
            .ToList();

        while (level.Count > 1)
            level = BuildBranchLevel(sink, alloc, level, keyLen);

        return level[0].Offset;
    }

    private static List<ChildRef> BuildBranchLevel(PageSink sink, Func<int> alloc, List<ChildRef> children, int keyLen)
    {
        int entrySize = keyLen + 8;
        int maxPer = (Page - StdHeader) / entrySize;
        if (maxPer < 1) maxPer = 1;

        var nodes = new List<(int index, List<ChildRef> kids)>();
        for (int i = 0; i < children.Count; i += maxPer)
        {
            var kids = children.GetRange(i, Math.Min(maxPer, children.Count - i));
            nodes.Add((alloc(), kids));
        }

        bool isRoot = nodes.Count == 1;
        var result = new List<ChildRef>(nodes.Count);
        for (int n = 0; n < nodes.Count; n++)
        {
            var (index, kids) = nodes[n];
            uint left = n > 0 ? (uint)(nodes[n - 1].index * Page) : NoPtr;
            uint right = n < nodes.Count - 1 ? (uint)(nodes[n + 1].index * Page) : NoPtr;
            WriteBranch(sink.At(index), kids, keyLen, (ushort)(isRoot ? AttrRoot : 0), left, right);

            var last = kids[^1];
            result.Add(new ChildRef((uint)(index * Page), last.SeparatorKey, last.SeparatorRecno));
        }
        return result;
    }

    private static void WriteBranch(byte[] page, List<ChildRef> kids, int keyLen, ushort attr, uint left, uint right)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(0), attr);
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(2), (ushort)kids.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), left);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8), right);

        int off = StdHeader;
        foreach (var k in kids)
        {
            int copy = Math.Min(keyLen, k.SeparatorKey.Length);
            k.SeparatorKey.AsSpan(0, copy).CopyTo(page.AsSpan(off, keyLen));
            BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(off + keyLen, 4), k.SeparatorRecno);
            BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(off + keyLen + 4, 4), k.Offset);
            off += keyLen + 8;
        }
    }

    // ---- leaf packing (mirror of CompactLeaf.Decode / r4reinde.c) ---------------

    private static List<LeafNode> PackLeaves(List<Entry> entries, int keyLen, byte pad, LeafGeometry geom)
    {
        var leaves = new List<LeafNode>();
        int freeBudget = Page - StdHeader - NodeInfo; // 488

        if (entries.Count == 0)
        {
            leaves.Add(new LeafNode { Items = new List<LeafItem>(), FreeSpace = (ushort)freeBudget });
            return leaves;
        }

        var cur = new List<LeafItem>();
        int free = freeBudget;
        byte[] lastKey = new byte[keyLen];
        Array.Fill(lastKey, pad);
        int lastTrail = keyLen;
        byte[]? lastOriginal = null;
        uint lastRecno = 0;

        foreach (var e in entries)
        {
            byte[] key = e.Key;
            int dup;
            if (cur.Count == 0)
            {
                dup = 0;
                lastTrail = keyLen;
            }
            else
            {
                dup = CommonPrefix(key, lastKey, keyLen);
                if (dup > keyLen - lastTrail) dup = keyLen - lastTrail;
            }

            int trail = dup == keyLen ? 0 : TrailingPad(key, keyLen, pad);
            lastTrail = trail;
            if (dup > keyLen - trail) dup = keyLen - trail;
            int len = keyLen - dup - trail;

            if (cur.Count > 0 && free < geom.BytesPerEntry + len)
            {
                // Current leaf is full: flush it and start a fresh one (dup resets to 0).
                leaves.Add(new LeafNode { Items = cur, FreeSpace = (ushort)free, LastKey = lastOriginal!, LastRecno = lastRecno });
                cur = new List<LeafItem>();
                free = freeBudget;
                dup = 0;
                trail = keyLen == 0 ? 0 : TrailingPad(key, keyLen, pad);
                len = keyLen - trail;
                lastTrail = trail;
            }

            var fresh = new byte[len];
            Array.Copy(key, dup, fresh, 0, len);
            cur.Add(new LeafItem(e.Recno, dup, trail, fresh));
            free -= len + geom.BytesPerEntry;

            lastKey = key;
            lastOriginal = key;
            lastRecno = e.Recno;
        }

        leaves.Add(new LeafNode { Items = cur, FreeSpace = (ushort)free, LastKey = lastOriginal!, LastRecno = lastRecno });
        return leaves;
    }

    private static void WriteLeaf(byte[] page, LeafNode leaf, int keyLen, LeafGeometry geom, ushort attr, uint left, uint right)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(0), attr);
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(2), (ushort)leaf.Items.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), left);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8), right);

        // Leaf info block (@12..23).
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(12), leaf.FreeSpace);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), geom.RecnoMask);
        page[18] = geom.DupMask;
        page[19] = geom.TrailMask;
        page[20] = geom.RecnoBits;
        page[21] = geom.DupBits;
        page[22] = geom.TrailBits;
        page[23] = geom.BytesPerEntry;

        int kby = geom.BytesPerEntry;
        int infoPos = LeafDataStart;   // entry-info array grows forward from 24
        int tail = Page;               // distinct key bytes grow backward from the page end
        foreach (var item in leaf.Items)
        {
            ulong v = (ulong)item.Recno & geom.RecnoMaskFull
                      | ((ulong)(uint)item.Dup << geom.RecnoBits)
                      | ((ulong)(uint)item.Trail << (geom.RecnoBits + geom.DupBits));
            for (int b = 0; b < kby; b++)
                page[infoPos + b] = (byte)(v >> (8 * b));
            infoPos += kby;

            tail -= item.Fresh.Length;
            Array.Copy(item.Fresh, 0, page, tail, item.Fresh.Length);
        }
    }

    // ---- key encoding ----------------------------------------------------------

    /// <summary>
    /// The stored key length for a tag. For a MACHINE (identity) collation the CHARACTER key is the
    /// raw value bytes (the field / expression width). For a NON-identity collation (GENERAL) over a
    /// CHARACTER key VFP reserves the EXPANDED width — 2× the base length — because each source
    /// character can yield up to two head weight bytes (the ligatures Œ→OE, Æ→AE, Þ→TH, ß→SS) plus a
    /// diacritic-tail run; truncating to the field width would discard tail/diacritic weight and
    /// mis-order accented or wide data (and be byte-incompatible with VFP9). Verified against the
    /// VFP9-built fixture <c>vfp_test/ligtest.CDX</c> (C(10) GENERAL ⇒ KeyLength 20) and
    /// <c>data/USR.CDX</c> (UPPER over C(80) ⇒ 160). Capped at 254 so <c>Bits(keyLen) ≤ 8</c> — the
    /// compact-leaf dup/trail masks are single bytes (2·N stays ≤ 254 for N ≤ 127).
    /// </summary>
    private static int CollatedKeyLength(int baseLen, bool isChar, IVfpCollation collation)
    {
        if (!isChar || IsIdentityCollation(collation))
            return Math.Max(1, baseLen);
        int expanded = baseLen * 2;
        if (expanded > 254) expanded = 254; // keep Bits(keyLen) ≤ 8 (single-byte dup/trail masks)
        return Math.Max(1, expanded);
    }

    /// <summary>An identity collation (MACHINE) maps a character value to its raw code-page bytes,
    /// so the stored key needs no expansion; any other (GENERAL) is a variable-length weight key.</summary>
    private static bool IsIdentityCollation(IVfpCollation collation)
        => string.Equals(collation.Name, "MACHINE", StringComparison.OrdinalIgnoreCase);

    private static byte[] EncodeKey(VfpValue value, IndexKeyType type, int keyLen, IVfpCollation collation, byte pad)
    {
        if (type == IndexKeyType.Character)
        {
            // Space-pad (0x20) the NATURAL collated key up to the (already collation-expanded) keyLen.
            // For GENERAL the natural key is the head+tail weight run, which for accented / ligature
            // data is LONGER than the field width — keyLen is 2× the base width precisely so it is not
            // truncated here. The Math.Min is only a hard safety bound for a pathological over-long key.
            var natural = value.IsNull ? Array.Empty<byte>() : collation.GetCollatedKey(value.AsString.AsSpan());
            var key = new byte[keyLen];
            for (int i = 0; i < keyLen; i++) key[i] = pad;
            int copy = Math.Min(natural.Length, keyLen);
            natural.AsSpan(0, copy).CopyTo(key);
            return key;
        }

        object? clr = type switch
        {
            IndexKeyType.Integer => value.IsNull ? null : (object)value.AsInteger,
            IndexKeyType.Numeric => value.IsNull ? null : (object)value.AsDouble,
            IndexKeyType.Date => value.IsNull ? null : (object)value.AsDate,
            IndexKeyType.DateTime => value.IsNull ? null : (object)value.AsDateTime,
            _ => null,
        };

        byte[]? encoded = clr is null ? null : IndexKey.Encode(clr, type);
        if (encoded is null)
            return new byte[keyLen]; // null / unencodable → all-zero key (sorts first)

        var buf = new byte[keyLen];
        Array.Copy(encoded, buf, Math.Min(encoded.Length, keyLen));
        return buf;
    }

    /// <summary>Resolve the on-disk key type, byte length and char-ness for a tag KEY expression.</summary>
    private static (IndexKeyType Type, int KeyLen, bool IsChar) ResolveKey(DbfTable schema, string keyExpr)
    {
        string field = (keyExpr ?? string.Empty).Trim();
        foreach (var col in schema.Columns)
        {
            if (string.Equals(col.Name, field, StringComparison.OrdinalIgnoreCase))
                return FromColumn(col.Type, col.Length);
        }

        // Composite expression: infer the static type from the schema.
        try
        {
            var info = VfpExpression.Parse(keyExpr ?? string.Empty).InferType(new TableSchema(schema));
            return info.Type switch
            {
                VfpType.Character => (IndexKeyType.Character, Math.Max(1, info.Length), true),
                VfpType.Integer => (IndexKeyType.Integer, 4, false),
                VfpType.Numeric or VfpType.Currency => (IndexKeyType.Numeric, 8, false),
                VfpType.Date => (IndexKeyType.Date, 8, false),
                VfpType.DateTime => (IndexKeyType.DateTime, 8, false),
                _ => (IndexKeyType.Character, Math.Max(1, info.Length == 0 ? 10 : info.Length), true),
            };
        }
        catch
        {
            return (IndexKeyType.Character, 10, true);
        }
    }

    private static (IndexKeyType, int, bool) FromColumn(char dbfType, int length) => dbfType switch
    {
        'C' or 'V' => (IndexKeyType.Character, Math.Max(1, length), true),
        'I' => (IndexKeyType.Integer, 4, false),
        'N' or 'F' or 'Y' or 'B' => (IndexKeyType.Numeric, 8, false),
        'D' => (IndexKeyType.Date, 8, false),
        'T' => (IndexKeyType.DateTime, 8, false),
        _ => (IndexKeyType.Character, Math.Max(1, length), true),
    };

    // ---- header / pool writers -------------------------------------------------

    private static void WriteHeaderCommon(byte[] page, uint root, int keyLen, byte options, byte signature,
        string? sortOrder, bool descending, string keyExpr, string forExpr)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(0), root);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), 0u);    // free list (unused)
        BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(8), 0u);       // version counter
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(12), (ushort)keyLen);
        page[14] = options;
        page[15] = signature;

        // MACHINE collation is encoded as an EMPTY sort-order field (all zeros): VFP looks up
        // any NON-empty name as a named collating sequence and raises error 1915 ("Collating
        // sequence 'MACHINE' is not found") on the literal "MACHINE" — verified against the real
        // VFP9 runtime. Only a real named collation (GENERAL, ...) writes its name at @494.
        if (!string.IsNullOrEmpty(sortOrder) && !string.Equals(sortOrder, "MACHINE", StringComparison.OrdinalIgnoreCase))
        {
            byte[] s = Encoding.ASCII.GetBytes(sortOrder);
            int n = Math.Min(s.Length, 8);
            s.AsSpan(0, n).CopyTo(page.AsSpan(494, 8));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(502), (ushort)(descending ? 1 : 0));
        ushort forLen = (ushort)((forExpr?.Length ?? 0) + 1);  // includes terminating NUL
        ushort keyLenExpr = (ushort)((keyExpr?.Length ?? 0) + 1);
        // @504 mirrors the KEY-expression length (VFP writes it alongside the @510 copy the reader uses).
        // Confirmed in vfp_test/dbctest/ref.DCX and vfp_test/ligtest.CDX: @504 == @510 == keyExprLen on
        // every header page (file header and each tag). The reader ignores it; byte-identity needs it.
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(504), keyLenExpr);
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(506), forLen);
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(510), keyLenExpr);
    }

    /// <summary>Write the KEY then FOR expression strings into the pool at <paramref name="headerOffset"/> + 512.</summary>
    private static void WriteExpressionPool(PageSink sink, long headerOffset, string keyExpr, string forExpr)
    {
        int poolIndex = (int)((headerOffset / Page) + 1);
        var pool = sink.At(poolIndex);
        int p = 0;
        foreach (byte b in Encoding.ASCII.GetBytes(keyExpr)) pool[p++] = b;
        pool[p++] = 0;
        foreach (byte b in Encoding.ASCII.GetBytes(forExpr)) pool[p++] = b;
        pool[p++] = 0;
    }

    // ---- small helpers ---------------------------------------------------------

    private static byte[] MakeNameKey(string name)
    {
        var key = new byte[10];
        for (int i = 0; i < 10; i++) key[i] = 0x20;
        byte[] bytes = Encoding.ASCII.GetBytes(name.Trim());
        int n = Math.Min(bytes.Length, 10);
        bytes.AsSpan(0, n).CopyTo(key);
        return key;
    }

    private static List<Entry> DropDuplicateKeys(List<Entry> sorted)
    {
        var result = new List<Entry>(sorted.Count);
        byte[]? prev = null;
        foreach (var e in sorted)
        {
            if (prev is not null && prev.AsSpan().SequenceEqual(e.Key))
                continue; // duplicate key → keep only the first (lowest recno).
            result.Add(e);
            prev = e.Key;
        }
        return result;
    }

    private static int CommonPrefix(byte[] a, byte[] b, int len)
    {
        int n = Math.Min(len, Math.Min(a.Length, b.Length));
        int i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    private static int TrailingPad(byte[] key, int len, byte pad)
    {
        int n = Math.Min(len, key.Length);
        int t = 0;
        while (t < n && key[n - 1 - t] == pad) t++;
        return t;
    }

    internal static int Bits(long value)
    {
        int c = 0;
        ulong v = (ulong)Math.Max(0, value);
        while (v != 0) { c++; v >>= 1; }
        return c;
    }

    // ---- supporting types ------------------------------------------------------

    private sealed class PageSink
    {
        private readonly List<byte[]> _pages = new();
        public int Reserve() { _pages.Add(new byte[Page]); return _pages.Count - 1; }
        public byte[] At(int index) => _pages[index];
        public byte[] ToArray()
        {
            var buf = new byte[_pages.Count * Page];
            for (int i = 0; i < _pages.Count; i++)
                Array.Copy(_pages[i], 0, buf, i * Page, Page);
            return buf;
        }
    }

    private readonly record struct Entry(byte[] Key, uint Recno);

    private sealed class EntryComparer : IComparer<Entry>
    {
        public static readonly EntryComparer Instance = new();
        public int Compare(Entry x, Entry y)
        {
            int n = Math.Min(x.Key.Length, y.Key.Length);
            for (int i = 0; i < n; i++)
            {
                int d = x.Key[i] - y.Key[i];
                if (d != 0) return d;
            }
            if (x.Key.Length != y.Key.Length) return x.Key.Length - y.Key.Length;
            return x.Recno.CompareTo(y.Recno);
        }
    }

    private readonly record struct LeafItem(uint Recno, int Dup, int Trail, byte[] Fresh);

    private sealed class LeafNode
    {
        public List<LeafItem> Items = new();
        public ushort FreeSpace;
        public byte[] LastKey = Array.Empty<byte>();
        public uint LastRecno;
        public int Index;
    }

    private readonly record struct ChildRef(uint Offset, byte[] SeparatorKey, uint SeparatorRecno);

    /// <summary>The compact-leaf bit-width geometry (mirrors CodeBase <c>r4reinde.c</c>).</summary>
    private readonly struct LeafGeometry
    {
        public readonly byte RecnoBits, DupBits, TrailBits, BytesPerEntry;
        public readonly byte DupMask, TrailMask;
        public readonly uint RecnoMask;
        public readonly ulong RecnoMaskFull;

        private LeafGeometry(byte recnoBits, byte dupBits, byte trailBits, byte kby)
        {
            RecnoBits = recnoBits; DupBits = dupBits; TrailBits = trailBits; BytesPerEntry = kby;
            DupMask = (byte)((1 << dupBits) - 1);
            TrailMask = (byte)((1 << trailBits) - 1);
            RecnoMask = recnoBits >= 32 ? 0xFFFFFFFFu : (1u << recnoBits) - 1;
            RecnoMaskFull = RecnoMask;
        }

        public static LeafGeometry For(int keyLen, long recnoBasis, List<Entry> entries)
        {
            int dupTrailBits = Math.Max(1, Bits(keyLen));

            long maxRecno = recnoBasis;
            foreach (var e in entries)
                if (e.Recno > maxRecno) maxRecno = e.Recno;

            // Cap the record-number width at 32 bits BEFORE the byte-boundary padding, then pad. The
            // padding may push recBits past 32 — that is fine: RecnoMask already saturates to
            // 0xFFFFFFFF for recnoBits ≥ 32 and the decoder shifts dup/trail by the on-disk RecnoBits,
            // so RecnoBits + DupBits + TrailBits stays exactly equal to BytesPerEntry·8 (the invariant
            // VFP/CodeBase validate). Capping AFTER padding instead would desync the bit widths from
            // BytesPerEntry near ~2^31 records.
            int recBits = Math.Max(1, Math.Min(32, Bits(maxRecno)));
            while ((recBits + dupTrailBits + dupTrailBits) % 8 != 0)
                recBits++;
            int kby = (recBits + dupTrailBits + dupTrailBits) / 8;

            return new LeafGeometry((byte)recBits, (byte)dupTrailBits, (byte)dupTrailBits, (byte)kby);
        }
    }

    /// <summary>Adapts a <see cref="DbfRecord"/> to the expression engine row contract.</summary>
    private sealed class RowContext : IRowContext
    {
        private readonly DbfRecord _record;
        public RowContext(DbfRecord record, int recNo, int recCount) { _record = record; RecNo = recNo; RecCount = recCount; }
        public int RecNo { get; }
        public int RecCount { get; }
        public bool Deleted => _record.IsDeleted;

        public object? GetField(string name)
        {
            // VFP evaluates an index KEY/FOR expression over the FULL declared field width — a
            // CHARACTER field keeps its trailing space padding (and any leading spaces). The record
            // reader (DbfRecord[name]) TRIMS character fields (§A5/ruby-dbf oracle), which is correct
            // for display but WRONG inside a composite key: e.g. str(parentid)+objecttype+lower(name)
            // must embed objecttype as "Field     " (C(10)), not the trimmed "Field". Re-decode the
            // raw bytes at full width for 'C' fields; a NULL-flagged field still reads as NULL.
            var value = _record[name];
            if (value is string)
            {
                foreach (var col in _record.Table.Columns)
                {
                    if (string.Equals(col.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        if (col.Type == 'C')
                        {
                            var raw = _record.GetRawField(col);
                            var enc = col.IsBinary ? Encoding.Latin1 : _record.Table.Encoding;
                            return enc.GetString(raw);
                        }
                        break;
                    }
                }
            }
            return value;
        }
    }

    /// <summary>Adapts a <see cref="DbfTable"/> to the expression engine static-schema contract.</summary>
    private sealed class TableSchema : ISchema
    {
        private readonly DbfTable _table;
        public TableSchema(DbfTable table) => _table = table;
        public bool TryGetColumn(string name, out char type, out int length, out int decimals)
        {
            foreach (var c in _table.Columns)
            {
                if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    type = c.Type; length = c.Length; decimals = c.Decimal;
                    return true;
                }
            }
            type = '\0'; length = 0; decimals = 0;
            return false;
        }
    }
}
