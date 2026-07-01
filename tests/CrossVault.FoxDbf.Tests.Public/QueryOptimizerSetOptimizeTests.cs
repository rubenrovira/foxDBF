using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// §3.4 — <c>SET OPTIMIZE OFF</c>. <see cref="EvaluationContext.Optimize"/> = false makes the
/// query consult NO index and run as a plain full scan; the result must be identical to the
/// optimized run, only slower. ShowPlan reports the whole query as <see cref="OptimizationLevel.None"/>
/// with <see cref="PlanReason.OptimizationDisabled"/>. Each test builds its own TEMP table + CDX.
/// </summary>
public sealed class QueryOptimizerSetOptimizeTests
{
    private static (string dir, string dbf, string cdx) BuildTable(int rows)
    {
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_query_setopt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dbf = Path.Combine(dir, "o.dbf");
        using (var w = DbfWriter.Create(dbf, new[]
        {
            new DbfColumnDef("ID", 'I', 4),
            new DbfColumnDef("AMOUNT", 'N', 10, 2),
        }))
        {
            for (int i = 1; i <= rows; i++)
                w.AppendRecord(new object?[] { i, i * 10 });   // AMOUNT 10..rows*10
            w.CreateTag(new CdxTagDefinition("AMTTAG", "AMOUNT"));
        }
        return (dir, dbf, Path.ChangeExtension(dbf, ".cdx"));
    }

    private static int[] Sorted(IEnumerable<int> xs) => xs.OrderBy(x => x).ToArray();
    private static void TryDelete(string dir) { try { Directory.Delete(dir, true); } catch { } }

    [Fact]
    public void OptimizeOff_FullScans_ButReturnsTheSameSetAsOptimizeOn()
    {
        var (dir, dbf, cdx) = BuildTable(200);
        try
        {
            using var table = DbfTable.Open(dbf);
            using var ix = CdxFile.Open(cdx, table);
            const string filter = "AMOUNT > 1900"; // recno 191..200 → 10 matches

            var on = QueryOptimizer.FindRecords(table, ix, filter, new EvaluationContext { Optimize = true });
            var off = QueryOptimizer.FindRecords(table, ix, filter, new EvaluationContext { Optimize = false });

            // Same answer either way.
            Assert.Equal(Sorted(on.RecordNumbers), Sorted(off.RecordNumbers));
            Assert.Equal(10, off.RecordNumbers.Count);

            // ON uses the index and scans few; OFF consults no index and scans every (non-deleted) row.
            Assert.True(on.Optimized, "SET OPTIMIZE ON should drive the AMOUNT tag");
            Assert.False(off.Optimized, "SET OPTIMIZE OFF must not use any index");
            Assert.Equal(200, off.RecordsScanned);
            Assert.True(on.RecordsScanned < off.RecordsScanned,
                $"optimized scan ({on.RecordsScanned}) must be smaller than the full scan ({off.RecordsScanned})");
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void OptimizeOff_Explain_ReportsNone_WithOptimizationDisabledReason()
    {
        var (dir, dbf, cdx) = BuildTable(50);
        try
        {
            using var table = DbfTable.Open(dbf);
            using var ix = CdxFile.Open(cdx, table);
            const string filter = "AMOUNT > 100";

            var off = QueryOptimizer.Explain(table, ix, filter, new EvaluationContext { Optimize = false });
            Assert.Equal(OptimizationLevel.None, off.Overall);
            Assert.NotEmpty(off.Conditions);
            Assert.All(off.Conditions, c =>
            {
                Assert.False(c.Optimizable);
                Assert.Equal(PlanReason.OptimizationDisabled, c.Reason);
            });
            Assert.Empty(off.UsedTags);
            Assert.Contains("disabled", off.ToString(), StringComparison.OrdinalIgnoreCase);

            // Sanity: with optimization ON the same query is Full and uses the tag.
            var on = QueryOptimizer.Explain(table, ix, filter, new EvaluationContext { Optimize = true });
            Assert.Equal(OptimizationLevel.Full, on.Overall);
            Assert.Contains("AMTTAG", on.UsedTags);
        }
        finally { TryDelete(dir); }
    }
}
