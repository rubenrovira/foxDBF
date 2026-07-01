using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Highlike Phase B-4 — BMI2 <c>PEXT</c>-accelerated compact-CDX-leaf UNPACK.
///
/// The compact leaf packs <c>recno / dup-count / trail-count</c> at FIXED bit widths
/// (RecnoBits / DupBits / TrailBits, BytesPerEntry) taken from the leaf info block.
/// A BMI2 <c>PEXT</c> fast path extracts those fields, guarded by
/// <see cref="CompactLeaf.IsBmi2Available"/>, with the existing scalar shift/mask loop
/// as the fallback for ARM / older x86 / unsupported CPUs.
///
/// THE GATE IS CORRECTNESS, NOT SPEED: the BMI2-decoded output (recno, dup, trail, and
/// the reconstructed key bytes) MUST be BYTE-IDENTICAL to the scalar decoder for every
/// tag and every entry. <see cref="CompactLeaf.Decode(System.ReadOnlySpan{byte},int,bool,CompactLeaf.DecodePath)"/>
/// exposes BOTH kernels so these tests can run the scalar AND the BMI2 path and assert
/// equality even on a BMI2-capable machine (which also covers the fallback contract).
///
/// SAFETY: the freshly-built index is written to a throwaway TEMP dir only; no committed
/// fixture under data/ or vfp_test/ is ever written.
/// </summary>
public sealed class CompactLeafBmi2Tests
{
    // ====================================================================
    //  Helpers
    // ====================================================================

    /// <summary>
    /// True when the BMI2 fast path can actually run here. Tests that exercise the BMI2
    /// kernel early-return as a no-op on a non-BMI2 CPU (the fallback is covered separately),
    /// but RUN — and must hold byte-identity — on any BMI2-capable machine.
    /// </summary>
    private static bool Bmi2Available => CompactLeaf.IsBmi2Available;

    /// <summary>
    /// Decodes <paramref name="page"/> with the forced SCALAR kernel and the forced BMI2
    /// kernel and asserts both produce identical entries (recno + reconstructed key bytes).
    /// </summary>
    private static void AssertScalarEqualsBmi2(byte[] page, int keyLength, bool isCharacter)
    {
        var scalar = CompactLeaf.Decode(page, keyLength, isCharacter, CompactLeaf.DecodePath.Scalar);
        var bmi2 = CompactLeaf.Decode(page, keyLength, isCharacter, CompactLeaf.DecodePath.Bmi2);

        Assert.Equal(scalar.Count, bmi2.Count);
        for (int i = 0; i < scalar.Count; i++)
        {
            Assert.Equal(scalar[i].RecordNumber, bmi2[i].RecordNumber);
            Assert.Equal(scalar[i].Key, bmi2[i].Key);
        }
    }

    /// <summary>
    /// Returns every LEAF page of a tag in index order: descend to the left-most leaf,
    /// then walk the right-sibling chain (mirrors <c>IndexTraversal</c>, defensively).
    /// </summary>
    private static List<byte[]> LeafPages(IndexFile index, uint root, int keyLength)
    {
        var pages = new List<byte[]>();
        if (keyLength <= 0)
            return pages;

        long cur = root;
        var descended = new HashSet<long>();
        while (true)
        {
            if (!descended.Add(cur))
                return pages;
            var header = index.ReadNodeHeader(cur);
            if (header is null)
                return pages;
            if (header.Value.IsLeaf)
                break;
            var branch = index.ReadBranchEntries(cur, keyLength);
            if (branch.Count == 0)
                return pages;
            cur = branch[0].ChildPointer;
        }

        var visited = new HashSet<long>();
        while (true)
        {
            if (!visited.Add(cur))
                break;
            var page = index.ReadPage(cur);
            if (page is not null)
                pages.Add(page);
            var header = index.ReadNodeHeader(cur);
            if (header is null)
                break;
            var right = header.Value.RightSibling;
            if (right is null)
                break;
            cur = right.Value;
        }

        return pages;
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>
    /// Builds a synthetic 512-byte compact-leaf page with explicit bit widths and
    /// per-entry (recno, dup, trail, freshBytes) tuples. Mirrors the real on-disk layout:
    /// the entry-info array grows FORWARD from offset 24, the fresh key bytes are laid out
    /// at the TAIL and consumed BACKWARD. Masks are derived from the bit widths.
    /// </summary>
    private static byte[] BuildLeaf(
        byte cRN, byte cDC, byte cTC, byte kBy, int keyLength,
        params (uint recno, int dup, int trail, byte[] fresh)[] entries)
    {
        uint recnoMask = cRN >= 32 ? 0xFFFFFFFFu : (uint)((1u << cRN) - 1);
        byte dupMask = (byte)((1 << cDC) - 1);
        byte trailMask = (byte)((1 << cTC) - 1);

        var page = new byte[IndexFile.PageSize];

        // Node header: leaf bit (0x02) set, key count @2, no siblings.
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(0), 0x0002);
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(2), (ushort)entries.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4), IndexNodeHeader.NoPointer);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(8), IndexNodeHeader.NoPointer);

        // Leaf info block @12.
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(12), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), recnoMask);
        page[18] = dupMask;
        page[19] = trailMask;
        page[20] = cRN;
        page[21] = cDC;
        page[22] = cTC;
        page[23] = kBy;

        int entryOffset = LeafInfo.EntryArrayOffset;
        int tail = IndexFile.PageSize;
        Span<byte> packed = stackalloc byte[8];

        foreach (var (recno, dup, trail, fresh) in entries)
        {
            ulong v = ((ulong)recno & recnoMask)
                      | ((ulong)((uint)dup & dupMask) << cRN)
                      | ((ulong)((uint)trail & trailMask) << (cRN + cDC));

            BinaryPrimitives.WriteUInt64LittleEndian(packed, v);
            packed.Slice(0, kBy).CopyTo(page.AsSpan(entryOffset, kBy));
            entryOffset += kBy;

            tail -= fresh.Length;
            fresh.CopyTo(page.AsSpan(tail, fresh.Length));
        }

        return page;
    }

    private static string FreshTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_bmi2leaf_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    // ====================================================================
    //  (1) BYTE-IDENTICAL scalar-vs-BMI2 on REAL CDX fixtures
    // ====================================================================

    [Fact]
    public void Bmi2_Matches_Scalar_OnIdxtestCdx_EveryTagEveryLeaf()
    {
        if (!Bmi2Available) return;

        using var cdx = CdxFile.Open(Fixtures.VfpTest("idxtest.CDX"));
        Assert.NotEmpty(cdx.TagNames);

        int leavesChecked = 0;
        foreach (var name in cdx.TagNames)
        {
            var tag = cdx.Tag(name)!;
            foreach (var page in LeafPages(cdx.Index, tag.RootPageOffset, tag.KeyLength))
            {
                // The kernel only extracts recno/dup/trail; assert identity under BOTH
                // pad modes so the front-coding reconstruction is covered either way.
                AssertScalarEqualsBmi2(page, tag.KeyLength, isCharacter: true);
                AssertScalarEqualsBmi2(page, tag.KeyLength, isCharacter: false);
                leavesChecked++;
            }
        }

        Assert.True(leavesChecked > 0, "expected at least one decodable leaf in idxtest.CDX");
    }

    [Fact]
    public void Bmi2_Matches_Scalar_OnTastradeCustomerCdx_EveryTagEveryLeaf()
    {
        if (!Bmi2Available) return;

        using var cdx = CdxFile.Open(Fixtures.Tastrade("customer.cdx"));
        Assert.NotEmpty(cdx.TagNames);

        int leavesChecked = 0;
        foreach (var name in cdx.TagNames)
        {
            var tag = cdx.Tag(name)!;
            foreach (var page in LeafPages(cdx.Index, tag.RootPageOffset, tag.KeyLength))
            {
                AssertScalarEqualsBmi2(page, tag.KeyLength, isCharacter: true);
                AssertScalarEqualsBmi2(page, tag.KeyLength, isCharacter: false);
                leavesChecked++;
            }
        }

        Assert.True(leavesChecked > 0, "expected at least one decodable leaf in customer.cdx");
    }

    [Fact]
    public void Bmi2_Matches_Scalar_OnFreshlyBuiltMultiLeafIndex_AcrossSiblingChain()
    {
        if (!Bmi2Available) return;

        string dir = FreshTempDir();
        try
        {
            // Build a temp table whose single character tag CANNOT fit in one 512-byte
            // leaf, forcing a branch level + a multi-leaf sibling chain (REINDEX path).
            const int n = 600;
            string dbf = Path.Combine(dir, "t.dbf");
            var cols = new[] { new DbfColumnDef("KEY", 'C', 8) };
            using (var w = DbfWriter.Create(dbf, cols))
            {
                for (int r = 1; r <= n; r++)
                    w.AppendRecord(new object?[] { $"K{(n + 1 - r):D5}" });
                w.CreateTag(new CdxTagDefinition("KTAG", "KEY"));
            }
            string cdxPath = Path.ChangeExtension(dbf, ".cdx");

            using var table = DbfTable.Open(dbf);
            using var cdx = CdxFile.Open(cdxPath, table);
            var tag = cdx.Tag("KTAG")!;

            var leaves = LeafPages(cdx.Index, tag.RootPageOffset, tag.KeyLength);
            Assert.True(leaves.Count > 1, "a 600-row tag must span more than one leaf page");

            foreach (var page in leaves)
                AssertScalarEqualsBmi2(page, tag.KeyLength, tag.IsCharacterKey);
        }
        finally { Cleanup(dir); }
    }

    // ====================================================================
    //  (3) EDGE: bit widths at boundaries, byte-boundary spans, page edge,
    //           0/1 entries, dup=0 first key
    // ====================================================================

    [Fact]
    public void Bmi2_Matches_Scalar_RecnoBits32_FullWidthRecord_HighBitSet()
    {
        if (!Bmi2Available) return;

        // recnoBits == 32 (the maximum; RecnoMask is a u32). dup/trail occupy bits
        // 32..35 and 36..39, so BOTH straddle byte 4 of the 5-byte LE record.
        var page = BuildLeaf(
            cRN: 32, cDC: 4, cTC: 4, kBy: 5, keyLength: 10,
            (recno: 0xFEDCBA98u, dup: 0, trail: 2, fresh: new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }),
            (recno: 0x80000001u, dup: 4, trail: 1, fresh: new byte[] { 9, 10, 11, 12, 13 }));

        AssertScalarEqualsBmi2(page, keyLength: 10, isCharacter: false);
        AssertScalarEqualsBmi2(page, keyLength: 10, isCharacter: true);
    }

    [Fact]
    public void Bmi2_Matches_Scalar_FieldsSpanningByteBoundaries_RealWorldWidths()
    {
        if (!Bmi2Available) return;

        // The canonical cRN=14,cDC=5,cTC=5,kBy=3 widths: every field straddles a byte
        // boundary inside the 3-byte LE record.
        var page = BuildLeaf(
            cRN: 14, cDC: 5, cTC: 5, kBy: 3, keyLength: 20,
            (recno: 0x2FFFu, dup: 0, trail: 0, fresh: Ascii("oyl7Ti**a**k7wi9PESc")),
            (recno: 0x1234u, dup: 8, trail: 3, fresh: Ascii("bYcxiSN")),     // 20-8-3 = 9 fresh
            (recno: 0x0001u, dup: 19, trail: 0, fresh: new byte[] { 0x5A })); // 20-19 = 1 fresh

        AssertScalarEqualsBmi2(page, keyLength: 20, isCharacter: true);
        AssertScalarEqualsBmi2(page, keyLength: 20, isCharacter: false);
    }

    [Fact]
    public void Bmi2_Matches_Scalar_WideDupTrailBytes_kBy5()
    {
        if (!Bmi2Available) return;

        // cRN=24,cDC=8,cTC=8,kBy=5: dup/trail are full-byte fields (mask 0xFF) sitting
        // at bits 24..31 and 32..39 — i.e. each is exactly one whole byte of the record.
        var page = BuildLeaf(
            cRN: 24, cDC: 8, cTC: 8, kBy: 5, keyLength: 16,
            (recno: 0x00ABCDEFu, dup: 0, trail: 4, fresh: Ascii("ABCDEFGHIJKL")),
            (recno: 0x00112233u, dup: 6, trail: 2, fresh: Ascii("MNOPQRST")));

        AssertScalarEqualsBmi2(page, keyLength: 16, isCharacter: false);
    }

    [Fact]
    public void Bmi2_Matches_Scalar_SingleByteEntry_kBy1()
    {
        if (!Bmi2Available) return;

        // kBy == 1: the whole record is a single byte (cRN=4,cDC=2,cTC=2).
        var page = BuildLeaf(
            cRN: 4, cDC: 2, cTC: 2, kBy: 1, keyLength: 4,
            (recno: 0x0Au, dup: 0, trail: 1, fresh: new byte[] { 0xAA, 0xBB, 0xCC }));

        AssertScalarEqualsBmi2(page, keyLength: 4, isCharacter: false);
        AssertScalarEqualsBmi2(page, keyLength: 4, isCharacter: true);
    }

    [Fact]
    public void Bmi2_Matches_Scalar_EntryAtPageEdge_LastRecordTouchesPageEnd()
    {
        if (!Bmi2Available) return;

        // Fill the entry-info array right up to the 512-byte page end: (512-24)/3 = 162
        // records of kBy=3, keyLength=1, fresh=0 (dup=0, trail=1) so the LAST record's
        // bytes [510,512) sit exactly at the page edge.
        const int count = 162;
        var entries = new (uint, int, int, byte[])[count];
        for (int i = 0; i < count; i++)
            entries[i] = ((uint)(i + 1), 0, 1, Array.Empty<byte>());

        var page = BuildLeaf(cRN: 14, cDC: 5, cTC: 5, kBy: 3, keyLength: 1, entries);

        AssertScalarEqualsBmi2(page, keyLength: 1, isCharacter: true);
        AssertScalarEqualsBmi2(page, keyLength: 1, isCharacter: false);
    }

    [Fact]
    public void Bmi2_Matches_Scalar_TagWithZeroEntries_BothEmpty()
    {
        if (!Bmi2Available) return;

        // KeyCount == 0: a well-formed but empty leaf. Both kernels yield nothing.
        var page = BuildLeaf(cRN: 14, cDC: 5, cTC: 5, kBy: 3, keyLength: 10);

        var scalar = CompactLeaf.Decode(page, 10, true, CompactLeaf.DecodePath.Scalar);
        var bmi2 = CompactLeaf.Decode(page, 10, true, CompactLeaf.DecodePath.Bmi2);
        Assert.Empty(scalar);
        Assert.Empty(bmi2);
    }

    [Fact]
    public void Bmi2_Matches_Scalar_TagWithSingleEntry_Dup0FirstKey()
    {
        if (!Bmi2Available) return;

        // Exactly ONE entry: the dup=0 first key is stored verbatim (no predecessor).
        var page = BuildLeaf(
            cRN: 14, cDC: 5, cTC: 5, kBy: 3, keyLength: 6,
            (recno: 42u, dup: 0, trail: 1, fresh: Ascii("ABCDE")));

        AssertScalarEqualsBmi2(page, keyLength: 6, isCharacter: true);
    }

    [Fact]
    public void Bmi2_Matches_Scalar_FirstEntryDupIgnored_EvenWhenEncoded()
    {
        if (!Bmi2Available) return;

        // The first entry has a NON-zero encoded dup; the decoder forces dup=0 for entry 0.
        // Both kernels must agree on that special-case (and the BMI2 kernel must still
        // surface the SAME raw dup for entry 1's reuse).
        var page = BuildLeaf(
            cRN: 14, cDC: 5, cTC: 5, kBy: 3, keyLength: 6,
            (recno: 1u, dup: 3, trail: 0, fresh: Ascii("ABCDEF")),  // entry0: full key (dup forced 0)
            (recno: 2u, dup: 4, trail: 1, fresh: new byte[] { 0x5A })); // entry1: reuse "ABCD" + Z + pad

        AssertScalarEqualsBmi2(page, keyLength: 6, isCharacter: true);
        AssertScalarEqualsBmi2(page, keyLength: 6, isCharacter: false);
    }

    // ====================================================================
    //  (4) RUNTIME GUARD: forcing the SCALAR path (simulating "no BMI2")
    //      decodes correctly and never crashes.
    // ====================================================================

    [Fact]
    public void ForcedScalarPath_DecodesKnownSyntheticLeaf_Correctly()
    {
        // Simulates a CPU without BMI2: the forced scalar path must still produce the
        // exact front-coded keys. Two entries: "ABCDE " then reuse "ABC" + "Z" + 2 pads.
        var page = BuildLeaf(
            cRN: 14, cDC: 5, cTC: 5, kBy: 3, keyLength: 6,
            (recno: 10u, dup: 0, trail: 1, fresh: Ascii("ABCDE")),
            (recno: 20u, dup: 3, trail: 2, fresh: Ascii("Z")));

        var entries = CompactLeaf.Decode(page, 6, isCharacter: true, CompactLeaf.DecodePath.Scalar);

        Assert.Equal(2, entries.Count);
        Assert.Equal(10u, entries[0].RecordNumber);
        Assert.Equal(Ascii("ABCDE "), entries[0].Key);
        Assert.Equal(20u, entries[1].RecordNumber);
        Assert.Equal(Ascii("ABCZ  "), entries[1].Key);
    }

    [Fact]
    public void ForcedScalarPath_OnRealFixture_MatchesPublicDecode_NeverThrows()
    {
        // The public Decode is the trusted (Auto) path; forcing Scalar must reproduce it
        // exactly on a real index, and must never throw on any leaf.
        using var cdx = CdxFile.Open(Fixtures.VfpTest("idxtest.CDX"));

        int leavesChecked = 0;
        foreach (var name in cdx.TagNames)
        {
            var tag = cdx.Tag(name)!;
            foreach (var page in LeafPages(cdx.Index, tag.RootPageOffset, tag.KeyLength))
            {
                var forced = CompactLeaf.Decode(page, tag.KeyLength, false, CompactLeaf.DecodePath.Scalar);
                var publik = CompactLeaf.Decode(page, tag.KeyLength, false);

                Assert.Equal(publik.Count, forced.Count);
                for (int i = 0; i < publik.Count; i++)
                {
                    Assert.Equal(publik[i].RecordNumber, forced[i].RecordNumber);
                    Assert.Equal(publik[i].Key, forced[i].Key);
                }
                leavesChecked++;
            }
        }

        Assert.True(leavesChecked > 0);
    }

    [Fact]
    public void ForcedBmi2Path_OnUnusualPages_NeverThrows()
    {
        // The forced BMI2 path must be crash-proof regardless of CPU support: on a
        // non-BMI2 CPU it transparently uses the scalar fallback; on a BMI2 CPU it runs
        // the kernel. Either way, decoding must never throw — not even on an empty leaf,
        // a single-entry leaf, or a maxed-out (recnoBits=32) record.
        var empty = BuildLeaf(cRN: 14, cDC: 5, cTC: 5, kBy: 3, keyLength: 10);
        var single = BuildLeaf(cRN: 14, cDC: 5, cTC: 5, kBy: 3, keyLength: 6,
            (recno: 1u, dup: 0, trail: 0, fresh: Ascii("ABCDEF")));
        var wide = BuildLeaf(cRN: 32, cDC: 4, cTC: 4, kBy: 5, keyLength: 8,
            (recno: 0xFFFFFFFFu, dup: 0, trail: 0, fresh: new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));

        var ex = Record.Exception(() =>
        {
            CompactLeaf.Decode(empty, 10, true, CompactLeaf.DecodePath.Bmi2);
            CompactLeaf.Decode(single, 6, true, CompactLeaf.DecodePath.Bmi2);
            CompactLeaf.Decode(wide, 8, false, CompactLeaf.DecodePath.Bmi2);
        });

        Assert.Null(ex);
    }
}
