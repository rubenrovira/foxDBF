using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Highlike Phase 0 / part 1 — the Rushmore <see cref="QueryPlan"/> EXPLAIN / ShowPlan (the
/// VFP SYS(3054) equivalent). These tests PIN the plan's accuracy against what the optimizer
/// ACTUALLY does:
/// <list type="bullet">
///   <item>per-leaf: optimizable + the TAG (name + key) used, or the REASON it falls to the residual;</item>
///   <item>overall: Full (all leaves index-resolved), Partial (some optimized + a residual),
///   None (nothing optimized);</item>
///   <item>single-source-of-truth: a leaf the plan marks optimized-via-tag ⇒ <see cref="QueryOptimizer.FindRecords"/>
///   actually reports <see cref="QueryResult.Optimized"/> true for that filter, and a None plan ⇒ Optimized false;</item>
///   <item>plan-ONLY: <see cref="QueryOptimizer.Explain"/> must NOT run the residual scan — it stays cheap on a large table;</item>
///   <item>FOR-filtered and UNIQUE tags are reported NOT usable (never silently optimized);</item>
///   <item>a function key <c>UPPER(NAME)</c> matches <c>UPPER(NAME)=…</c> but a bare <c>NAME=…</c> is residual.</item>
/// </list>
/// SAFETY: every table + CDX is built in a TEMP dir via the writer and deleted on dispose; no
/// committed fixture is touched.
/// </summary>
public sealed class QueryExplainPlanTests : IClassFixture<QueryExplainPlanTests.PlanTable>
{
    private readonly PlanTable _fx;
    public QueryExplainPlanTests(PlanTable fx) => _fx = fx;

    // ============================================================ helpers

    /// <summary>Whitespace-insensitive, case-insensitive normalization for matching leaf text.</summary>
    private static string Norm(string s)
        => new string(s.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToUpperInvariant();

    /// <summary>The single leaf whose condition text contains <paramref name="needle"/> (normalized).</summary>
    private static ConditionPlan Leaf(QueryPlan plan, string needle)
        => plan.Conditions.Single(c => Norm(c.Condition).Contains(Norm(needle)));

    private QueryPlan Explain(string filter, EvaluationContext? ctx = null)
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        return QueryOptimizer.Explain(table, cdx, filter, ctx);
    }

    private QueryResult Find(string filter, EvaluationContext? ctx = null)
    {
        using var table = DbfTable.Open(_fx.Dbf);
        using var cdx = CdxFile.Open(_fx.Cdx, table);
        return QueryOptimizer.FindRecords(table, cdx, filter, ctx);
    }

    // ============================================================ (a) both tags → Full

    [Fact]
    public void BothLeavesIndexed_OverallFull_EachLeafReportsItsTag()
    {
        var plan = Explain("AMOUNT > 100 AND UPPER(NAME) = 'ALICE'");

        Assert.Equal(OptimizationLevel.Full, plan.Overall);
        Assert.Equal(2, plan.Conditions.Count);

        var amount = Leaf(plan, "AMOUNT>100");
        Assert.True(amount.Optimizable);
        Assert.Equal(PlanReason.Optimized, amount.Reason);
        Assert.Equal("AMTTAG", amount.TagName);
        Assert.Equal("AMOUNT", Norm(amount.TagKeyExpression ?? ""));

        var name = Leaf(plan, "UPPER(NAME)='ALICE'");
        Assert.True(name.Optimizable);
        Assert.Equal(PlanReason.Optimized, name.Reason);
        Assert.Equal("UNAME", name.TagName);
        Assert.Equal("UPPER(NAME)", Norm(name.TagKeyExpression ?? ""));

        // Every optimizable leaf's tag appears in the used-tag set.
        Assert.Contains("AMTTAG", plan.UsedTags);
        Assert.Contains("UNAME", plan.UsedTags);
    }

    // ============================================================ (b) one tag-less → Partial

    [Fact]
    public void OneIndexedOneTagless_OverallPartial_TaglessLeafHasNoTagReason()
    {
        var plan = Explain("AMOUNT > 100 AND CITY = 'X'");

        Assert.Equal(OptimizationLevel.Partial, plan.Overall);

        var amount = Leaf(plan, "AMOUNT>100");
        Assert.True(amount.Optimizable);
        Assert.Equal("AMTTAG", amount.TagName);

        var city = Leaf(plan, "CITY='X'");
        Assert.False(city.Optimizable);
        Assert.Equal(PlanReason.NoMatchingTag, city.Reason);
        Assert.Null(city.TagName);

        Assert.Contains("AMTTAG", plan.UsedTags);
        Assert.DoesNotContain("CITY", plan.UsedTags);
    }

    // ============================================================ (c) nothing indexed → None

    [Fact]
    public void TaglessSingleLeaf_OverallNone_WithNoTagReason()
    {
        var plan = Explain("NOTINDEXED = 5");

        Assert.Equal(OptimizationLevel.None, plan.Overall);
        var leaf = Leaf(plan, "NOTINDEXED=5");
        Assert.False(leaf.Optimizable);
        Assert.Equal(PlanReason.NoMatchingTag, leaf.Reason);
        Assert.Empty(plan.UsedTags);
    }

    // ============================================================ (d) FOR / UNIQUE never trusted

    [Fact]
    public void ForFilteredOnlyTag_IsReportedNotUsable_WithForReason()
    {
        // SCORE has ONLY a FOR-filtered tag (SCORE>500) → unsafe → residual, with the FOR reason.
        var plan = Explain("SCORE = 600");

        Assert.Equal(OptimizationLevel.None, plan.Overall);
        var leaf = Leaf(plan, "SCORE=600");
        Assert.False(leaf.Optimizable);
        Assert.Equal(PlanReason.ForFiltered, leaf.Reason);
    }

    [Fact]
    public void UniqueOnlyTag_IsReportedNotUsable_WithUniqueReason()
    {
        // UID has ONLY a UNIQUE tag → under-represents duplicate keys → residual, with the UNIQUE reason.
        var plan = Explain("UID = 5");

        Assert.Equal(OptimizationLevel.None, plan.Overall);
        var leaf = Leaf(plan, "UID=5");
        Assert.False(leaf.Optimizable);
        Assert.Equal(PlanReason.Unique, leaf.Reason);
    }

    // ============================================================ (e) function key vs bare field

    [Fact]
    public void FunctionKeyTag_MatchesUpperName_ButBareNameLeafIsResidual()
    {
        // The function tag's key is UPPER(NAME); a bare NAME=… leaf has no matching tag.
        var upperPlan = Explain("UPPER(NAME) = 'ALICE'");
        var upperLeaf = Leaf(upperPlan, "UPPER(NAME)='ALICE'");
        Assert.True(upperLeaf.Optimizable);
        Assert.Equal("UNAME", upperLeaf.TagName);
        Assert.Equal(OptimizationLevel.Full, upperPlan.Overall);

        var barePlan = Explain("NAME = 'ALICE'");
        var bareLeaf = Leaf(barePlan, "NAME='ALICE'");
        Assert.False(bareLeaf.Optimizable);
        Assert.Equal(PlanReason.NoMatchingTag, bareLeaf.Reason);
        Assert.Equal(OptimizationLevel.None, barePlan.Overall);
    }

    // ============================================================ a DATE tag is index-resolvable

    [Fact]
    public void DateTag_ResolvesDateLeaf()
    {
        var plan = Explain("HIRED >= {^2020-01-01}");
        var leaf = Leaf(plan, "HIRED");
        Assert.True(leaf.Optimizable);
        Assert.Equal("HIREDT", leaf.TagName);
        Assert.Equal("HIRED", Norm(leaf.TagKeyExpression ?? ""));
        Assert.Equal(OptimizationLevel.Full, plan.Overall);
    }

    // ============================================================ (f) ToString readability

    [Fact]
    public void ToString_ContainsOverallLevelAndUsedTagNames()
    {
        var plan = Explain("AMOUNT > 100 AND UPPER(NAME) = 'ALICE'");
        string s = plan.ToString();

        Assert.Contains("Full", s);
        Assert.Contains("AMTTAG", s);
        Assert.Contains("UNAME", s);
    }

    // ============================================================ (g) single source of truth

    [Theory]
    // Full / Partial filters: the indexed leaf(s) ⇒ FindRecords actually uses an index.
    [InlineData("AMOUNT > 100 AND UPPER(NAME) = 'ALICE'", true)]
    [InlineData("AMOUNT > 100 AND CITY = 'X'", true)]
    // None filters: nothing optimizable ⇒ FindRecords does NOT use an index.
    [InlineData("NOTINDEXED = 5", false)]
    [InlineData("SCORE = 600", false)] // FOR-only tag is skipped by both plan and execution
    [InlineData("UID = 5", false)]     // UNIQUE-only tag is skipped by both plan and execution
    [InlineData("NAME = 'ALICE'", false)]
    public void PlanOptimizability_AgreesWith_FindRecordsOptimized(string filter, bool expectOptimized)
    {
        var plan = Explain(filter);
        var find = Find(filter);

        // The plan's "anything optimizable?" verdict must equal execution's Optimized flag.
        bool planOptimizes = plan.Overall != OptimizationLevel.None;
        Assert.Equal(expectOptimized, planOptimizes);
        Assert.Equal(expectOptimized, find.Optimized);

        // And every leaf the plan marks optimized names a real tag.
        foreach (var c in plan.Conditions.Where(c => c.Optimizable))
        {
            Assert.False(string.IsNullOrEmpty(c.TagName));
            Assert.Contains(c.TagName!, plan.UsedTags);
        }
    }

    // ============================================================ DbfTable.ExplainQuery bridge

    [Fact]
    public void DbfTableExplainQueryBridge_AutoLocatesCdx_AndReportsTheSamePlan()
    {
        using var table = DbfTable.Open(_fx.Dbf);
        var plan = table.ExplainQuery("AMOUNT > 100 AND UPPER(NAME) = 'ALICE'");

        Assert.Equal(OptimizationLevel.Full, plan.Overall);
        Assert.Contains("AMTTAG", plan.UsedTags);
        Assert.Contains("UNAME", plan.UsedTags);
    }

    // ============================================================ edges

    [Fact]
    public void EmptyFilter_IsNone_NoConditions_NeverThrows()
    {
        var plan = Explain("");
        Assert.Equal(OptimizationLevel.None, plan.Overall);
        Assert.Empty(plan.Conditions);
        Assert.Empty(plan.UsedTags);
    }

    [Fact]
    public void FullyNonOptimizable_IsNone()
    {
        var plan = Explain("CITY = 'X' OR NOTINDEXED = 5");
        Assert.Equal(OptimizationLevel.None, plan.Overall);
        Assert.DoesNotContain(plan.Conditions, c => c.Optimizable);
    }

    // ============================================================ collapsed OR demotes its leaf

    [Fact]
    public void OptimizableOrTagless_CollapsesToNone_OptimizableLeafIsDemoted_AgreesWithFindRecords()
    {
        // OR with a tag-less operand spreads the all-set universe over the union, so the AMOUNT>100
        // seek is swallowed: FindRecords runs a pure residual scan and seeks NO tag. The plan must
        // NOT claim the AMTTAG seek the optimizer never performs.
        var plan = Explain("AMOUNT > 100 OR CITY = 'X'");

        Assert.Equal(OptimizationLevel.None, plan.Overall);
        Assert.Empty(plan.UsedTags);
        Assert.DoesNotContain(plan.Conditions, c => c.Optimizable);

        var amount = Leaf(plan, "AMOUNT>100");
        Assert.False(amount.Optimizable);
        Assert.Null(amount.TagName);
        Assert.Equal(PlanReason.Residual, amount.Reason);

        // ToString must NOT self-contradict (no 'Using tags: AMTTAG' under a None header).
        string s = plan.ToString();
        Assert.Contains("None", s);
        Assert.DoesNotContain("AMTTAG", s);

        // Single source of truth: execution does not optimize this filter either.
        Assert.False(Find("AMOUNT > 100 OR CITY = 'X'").Optimized);
    }

    [Fact]
    public void OrInsideAnd_OnlySurvivingTagIsUsed_OrBranchSeekIsDemoted()
    {
        // (AMOUNT>100 OR CITY='X') collapses (the OR is a full scan), but the AND with an indexed
        // HIRED leaf keeps the query Partial — driven SOLELY by HIREDT. AMOUNT must NOT appear.
        var plan = Explain("(AMOUNT > 100 OR CITY = 'X') AND HIRED >= {^2020-01-01}");

        Assert.Equal(OptimizationLevel.Partial, plan.Overall);
        Assert.Equal(new[] { "HIREDT" }, plan.UsedTags.ToArray());

        var amount = Leaf(plan, "AMOUNT>100");
        Assert.False(amount.Optimizable);
        Assert.Null(amount.TagName);

        var hired = Leaf(plan, "HIRED");
        Assert.True(hired.Optimizable);
        Assert.Equal("HIREDT", hired.TagName);

        Assert.True(Find("(AMOUNT > 100 OR CITY = 'X') AND HIRED >= {^2020-01-01}").Optimized);
    }

    // ============================================================ NOT of an inexact (char) leaf

    [Fact]
    public void NotOfCharacterLeaf_CollapsesToNone_OptimizableLeafIsDemoted_AgreesWithFindRecords()
    {
        // A Character leaf is only a SUPERSET (collation/EXACT trimmed by the residual), so a
        // wrapping NOT cannot safely flip it: FindRecords seeks no tag. The plan must agree —
        // UNAME must NOT be reported optimized.
        var plan = Explain("NOT UPPER(NAME) = 'ALICE'");

        Assert.Equal(OptimizationLevel.None, plan.Overall);
        Assert.Empty(plan.UsedTags);
        Assert.DoesNotContain(plan.Conditions, c => c.Optimizable);

        var name = Leaf(plan, "UPPER(NAME)='ALICE'");
        Assert.False(name.Optimizable);
        Assert.Null(name.TagName);
        Assert.Equal(PlanReason.Residual, name.Reason);

        string s = plan.ToString();
        Assert.Contains("None", s);
        Assert.DoesNotContain("UNAME", s);

        Assert.False(Find("NOT UPPER(NAME) = 'ALICE'").Optimized);
    }

    [Fact]
    public void Contradiction_StillReportsBothLeavesOnTheTag_OverallFull()
    {
        // The plan is STRUCTURAL: it does not evaluate satisfiability. Both leaves use AMTTAG.
        var plan = Explain("AMOUNT > 500 AND AMOUNT < 100");
        Assert.Equal(OptimizationLevel.Full, plan.Overall);
        Assert.Equal(2, plan.Conditions.Count);
        Assert.All(plan.Conditions, c => Assert.Equal("AMTTAG", c.TagName));
    }

    [Fact]
    public void BadFilter_NeverThrows()
    {
        // A malformed filter must not throw — the diagnostic degrades gracefully.
        var ex = Record.Exception(() => Explain("AMOUNT >>> ("));
        Assert.Null(ex);
    }

    // ============================================================ PLAN-ONLY (no residual scan)

    [Fact]
    public void Explain_IsPlanOnly_DoesNotScaleWithRowCount()
    {
        // A large table whose data region a residual scan WOULD have to walk. Explain must stay
        // cheap (it classifies leaves + matches tags only), while a full-scan FindRecords on the
        // SAME filter walks every live row. Plan-only ⇒ Explain is dramatically faster.
        const int rows = 100_000;
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_explain_big_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string dbf = Path.Combine(dir, "big.dbf");
            var cols = new[]
            {
                new DbfColumnDef("AMOUNT", 'N', 10, 2),
                new DbfColumnDef("CITY", 'C', 10),
            };
            using (var w = DbfWriter.Create(dbf, cols))
            {
                for (int i = 1; i <= rows; i++)
                    w.AppendRecord(new object?[] { i % 1000, "C" + (i % 7) });
                w.CreateTag(new CdxTagDefinition("AMTTAG", "AMOUNT"));
            }
            string cdxPath = Path.ChangeExtension(dbf, ".cdx");

            using var table = DbfTable.Open(dbf);
            using var cdx = CdxFile.Open(cdxPath, table);

            // An UNINDEXED filter so FindRecords is forced into a full residual scan of all rows.
            const string filter = "CITY = 'nope'";

            // Warm up (JIT) so the comparison reflects steady-state work, not first-call cost.
            _ = QueryOptimizer.Explain(table, cdx, filter);
            _ = QueryOptimizer.FindRecords(table, cdx, "AMOUNT > 999999");

            var swExplain = Stopwatch.StartNew();
            var plan = QueryOptimizer.Explain(table, cdx, filter);
            swExplain.Stop();

            var swFind = Stopwatch.StartNew();
            var find = QueryOptimizer.FindRecords(table, cdx, filter);
            swFind.Stop();

            // Evidence the baseline actually did O(n) work: it scanned every live record.
            Assert.Equal(rows, find.RecordsScanned);

            // Plan-only: Explain never touched the rows, so it is far faster than the full scan.
            Assert.True(
                swExplain.Elapsed < swFind.Elapsed,
                $"Explain ({swExplain.ElapsedMilliseconds} ms) must be cheaper than a full-scan " +
                $"FindRecords ({swFind.ElapsedMilliseconds} ms) — it must NOT run the residual scan.");

            // And it still produced a correct plan for the unindexed filter.
            Assert.Equal(OptimizationLevel.None, plan.Overall);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // ============================================================ shared temp table

    /// <summary>
    /// ONE temp table built via the writer with: a NUMERIC tag AMTTAG(AMOUNT), a CHARACTER
    /// FUNCTION tag UNAME(UPPER(NAME)), a DATE tag HIREDT(HIRED), a FOR-filtered tag
    /// SCOREPOS(SCORE FOR SCORE&gt;500), a UNIQUE tag UIDUNQ(UID), and tag-less fields CITY +
    /// NOTINDEXED. NAME deliberately has NO bare tag (only the UPPER(NAME) function tag), so a
    /// bare NAME=… leaf is residual. Deleted on dispose.
    /// </summary>
    public sealed class PlanTable : IDisposable
    {
        public string Dir { get; }
        public string Dbf { get; }
        public string Cdx { get; }
        public int RowCount => 2000;

        private static readonly string[] Names = { "Alice", "Bob", "Carol", "Dave", "alice", "BOB" };

        public PlanTable()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_explain_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Dbf = Path.Combine(Dir, "p.dbf");

            var cols = new[]
            {
                new DbfColumnDef("AMOUNT", 'N', 10, 2),
                new DbfColumnDef("NAME", 'C', 20),
                new DbfColumnDef("HIRED", 'D', 8),
                new DbfColumnDef("SCORE", 'N', 10, 2),
                new DbfColumnDef("UID", 'N', 10, 0),
                new DbfColumnDef("CITY", 'C', 15),
                new DbfColumnDef("NOTINDEXED", 'N', 10, 0),
            };

            using (var w = DbfWriter.Create(Dbf, cols))
            {
                for (int i = 1; i <= RowCount; i++)
                {
                    int amount = i % 1000;                       // 0..999, many duplicates
                    string name = Names[i % Names.Length];
                    var hired = new DateOnly(2018 + (i % 6), 1 + (i % 12), 1 + (i % 27));
                    int score = (i % 2 == 0) ? 600 + (i % 100) : i % 400; // some >500, some <=500 (sparse FOR)
                    int uid = i;                                  // distinct
                    string city = "CITY" + (i % 5);
                    int notindexed = i % 50;
                    w.AppendRecord(new object?[] { amount, name, hired, score, uid, city, notindexed });
                }

                w.CreateTag(new CdxTagDefinition("AMTTAG", "AMOUNT"));
                w.CreateTag(new CdxTagDefinition("UNAME", "UPPER(NAME)"));
                w.CreateTag(new CdxTagDefinition("HIREDT", "HIRED"));
                w.CreateTag(new CdxTagDefinition("SCOREPOS", "SCORE", forExpression: "SCORE>500"));
                w.CreateTag(new CdxTagDefinition("UIDUNQ", "UID", unique: true));
            }

            Cdx = Path.ChangeExtension(Dbf, ".cdx");
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
