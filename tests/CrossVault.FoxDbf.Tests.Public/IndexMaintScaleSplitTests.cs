using System;
using System.Collections.Generic;
using System.IO;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Project-review finding 5.1 — group (3): SCALE. Enough one-by-one maintained appends to force MANY leaf
/// splits AND several BRANCH levels (root growth), then the incrementally-maintained tag must enumerate
/// EXACTLY what a fresh REINDEX (the proven bulk builder) produces — semantic equality with the bulk path is
/// the equivalence criterion (not byte-identity). The BATCH append path (<see cref="DbfWriter.AppendRecords"/>)
/// is covered too (review item 4: it must not leave an indexed table silently stale). And a table with NO
/// structural cdx must see ZERO behaviour change on writes (a green guard).
///
/// A few THOUSAND keys are used (not a few hundred) so the tree grows past a single branch level — branch
/// splits and root growth are exercised against our own reader in the committed suite, not just leaf splits.
///
/// SAFETY: throwaway temp dir only; no committed fixture is touched.
/// </summary>
public sealed class IndexMaintScaleSplitTests
{
    // A few thousand distinct keys: enough that the compact B-tree grows past ONE branch level (several
    // leaves → multiple branch nodes → a branch-of-branches root), so root/branch splits run in the editor.
    private const int AppendCount = 3000;

    [Fact]
    public void ManyIncrementalAppends_ForceLeafAndBranchSplits_EnumerationEqualsFreshReindex()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        var cols = new[] { new DbfColumnDef("KEY", 'C', 8) };

        var seed = new List<object?[]>();
        for (int i = 0; i < 5; i++)
            seed.Add(new object?[] { $"S{i:D5}" });
        string dbf = IndexMaintTestSupport.CreateTable(dir.Path, "t.dbf", cols, seed,
            new CdxTagDefinition("KTAG", "KEY"));

        // Append AppendCount distinct keys one-by-one in a scrambled order (gcd(137, AppendCount)=1 ⇒ a
        // bijection), so the maintained tree must split leaves and grow SEVERAL branch levels (root split).
        using (var w = DbfWriter.Open(dbf))
            for (int i = 0; i < AppendCount; i++)
            {
                int k = (i * 137 + 11) % AppendCount;
                w.AppendRecord($"A{k:D5}");
            }

        var maintainedRec = IndexMaintTestSupport.Recnos(dbf, "KTAG");
        var maintainedKeys = IndexMaintTestSupport.StrKeys(dbf, "KTAG");

        // A fresh full rebuild of the same live data — the semantic oracle.
        using (var w = DbfWriter.Open(dbf))
            w.Reindex();
        var freshRec = IndexMaintTestSupport.Recnos(dbf, "KTAG");
        var freshKeys = IndexMaintTestSupport.StrKeys(dbf, "KTAG");

        Assert.Equal(AppendCount + 5, freshRec.Length);   // sanity: the rebuild sees all rows
        Assert.Equal(freshKeys, maintainedKeys);          // same keys, same order
        Assert.Equal(freshRec, maintainedRec);            // same record numbers, same order

        // Cross-check a spread of SEEKs across the split boundaries land on real records.
        Assert.NotNull(IndexMaintTestSupport.Seek(dbf, "KTAG", (object)"A00000"));         // low key
        Assert.NotNull(IndexMaintTestSupport.Seek(dbf, "KTAG", (object)$"A{AppendCount / 2:D5}"));
        Assert.NotNull(IndexMaintTestSupport.Seek(dbf, "KTAG", (object)$"A{AppendCount - 1:D5}")); // high key
        Assert.Null(IndexMaintTestSupport.Seek(dbf, "KTAG", (object)"A99999"));            // never inserted
    }

    [Fact]
    public void AppendRecords_BatchIntoIndexedTable_EnumerationEqualsFreshReindex()
    {
        // Review item 4: the BATCH append API must maintain the structural cdx too (its in-repo callers
        // happen to target fresh tables, but the public API must be safe against an already-indexed one).
        using var dir = new IndexMaintTestSupport.TempDir();
        var cols = new[] { new DbfColumnDef("KEY", 'C', 8) };

        var seed = new List<object?[]>();
        for (int i = 0; i < 5; i++)
            seed.Add(new object?[] { $"S{i:D5}" });
        string dbf = IndexMaintTestSupport.CreateTable(dir.Path, "t.dbf", cols, seed,
            new CdxTagDefinition("KTAG", "KEY"));

        var batch = new List<object?[]>(AppendCount);
        for (int i = 0; i < AppendCount; i++)
        {
            int k = (i * 137 + 11) % AppendCount;
            batch.Add(new object?[] { $"A{k:D5}" });
        }
        using (var w = DbfWriter.Open(dbf))
            w.AppendRecords(batch);   // ONE batched maintenance pass — must equal a fresh rebuild

        var maintainedRec = IndexMaintTestSupport.Recnos(dbf, "KTAG");
        var maintainedKeys = IndexMaintTestSupport.StrKeys(dbf, "KTAG");

        using (var w = DbfWriter.Open(dbf))
            w.Reindex();
        var freshRec = IndexMaintTestSupport.Recnos(dbf, "KTAG");
        var freshKeys = IndexMaintTestSupport.StrKeys(dbf, "KTAG");

        Assert.Equal(AppendCount + 5, freshRec.Length);
        Assert.Equal(freshKeys, maintainedKeys);
        Assert.Equal(freshRec, maintainedRec);
        Assert.NotNull(IndexMaintTestSupport.Seek(dbf, "KTAG", (object)$"A{AppendCount / 2:D5}"));
    }

    [Fact]
    public void TableWithoutStructuralCdx_SeesNoIndexBehaviourChange()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        var cols = new[] { new DbfColumnDef("NAME", 'C', 10) };
        string dbf = IndexMaintTestSupport.CreateTable(dir.Path, "t.dbf", cols,
            new[] { new object?[] { "ALPHA" }, new object?[] { "BRAVO" } });   // NO tag → no cdx

        using (var w = DbfWriter.Open(dbf))
        {
            w.AppendRecord("CHARLIE");
            w.UpdateRecord(0, new object?[] { "ZULU" });
            Assert.False(w.ReindexNeeded);   // nothing to maintain, nothing invalidated
        }

        Assert.False(IndexMaintTestSupport.CdxExists(dbf));   // no index was ever conjured up
        using var t = DbfTable.Open(dbf);
        Assert.Equal(3, t.RecordCount);
    }
}
