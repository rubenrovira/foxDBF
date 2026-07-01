using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;

namespace CrossVault.FoxDbf.Highlike;

/// <summary>
/// The Highlike accelerator engine (assembly <c>CrossVault.FoxDbf.Highlike</c>) implementing the
/// Core opt-in seam <see cref="IQueryAccelerator"/>. Phase A: when statistics are enabled it builds a
/// <see cref="HighlikeCostPlanner"/> from the <c>.stx</c> sidecar and AUGMENTS the Core
/// <see cref="QueryOptimizer"/> through the <see cref="IOptimizerPolicy"/> seam — driving AND-ordering
/// (reported), index-vs-scan, and low-selectivity guard lifting. The dependency direction is strictly
/// <c>Highlike → Core</c>: Core never references this assembly.
/// </summary>
/// <remarks>
/// NON-NEGOTIABLE INVARIANT: the result of <see cref="FindRecords"/> equals the Core
/// <see cref="QueryOptimizer"/>.FindRecords result equals a full scan. Statistics are HINTS ONLY —
/// the policy can only change the plan (speed),
/// never the result set (every decision keeps the candidate a superset the residual confirms).
/// </remarks>
public sealed class HighlikeEngine : IQueryAccelerator, IDisposable
{
    /// <summary>The plan-influencing options (statistics on/off, …). Never affects the result set.</summary>
    public HighlikeOptions Options { get; }

    /// <summary>
    /// PHASE B-1: the engine-owned CROSS-QUERY warm cache of the decoded per-tag index (design §2/§5,
    /// Peak 2). Lifetime is tied to the engine (bounded, disposed with it). Speed-only — never changes
    /// a result.
    /// </summary>
    private readonly HighlikeIndexCache _cache;

    /// <summary>
    /// PERF-FIX: the engine-owned CROSS-QUERY memo of the parsed/built <see cref="StxStatistics"/>
    /// (analogue of <see cref="_cache"/>). Without it every query re-Loads / re-Builds the <c>.stx</c>
    /// — a constant per-query floor the 1M-row benchmark caught. Speed-only — never changes a result.
    /// </summary>
    private readonly HighlikeStatsCache _statsCache;

    /// <summary>Create an engine with default options (identical Core baseline).</summary>
    public HighlikeEngine() : this(null) { }

    /// <summary>Create an engine with the given <paramref name="options"/> (null → defaults).</summary>
    public HighlikeEngine(HighlikeOptions? options)
    {
        Options = options ?? HighlikeOptions.Default;
        _cache = new HighlikeIndexCache(Options);
        _statsCache = new HighlikeStatsCache(Options);
    }

    /// <summary>
    /// PHASE B-1: an immutable snapshot of the warm index-cache counters (hits / misses / evictions /
    /// resident entries / active FileSystemWatchers) — exposed so tests can prove the perf structure
    /// without timing.
    /// </summary>
    public HighlikeCacheStatistics CacheStatistics => _cache.Snapshot();

    /// <summary>
    /// PERF-FIX: an immutable snapshot of the statistics-memo counters (computes / reuses / resident
    /// entries) — exposed so tests can prove the per-query Load/Build floor is gone WITHOUT timing.
    /// </summary>
    public HighlikeStatsCacheStatistics StatsCacheStatistics => _statsCache.Snapshot();

    /// <summary>Disposes the engine: releases every attached FileSystemWatcher and drops the warm caches.</summary>
    public void Dispose()
    {
        _cache.Dispose();
        _statsCache.Dispose();
    }

    /// <inheritdoc />
    public QueryResult FindRecords(DbfTable table, CdxFile? cdx, string filter, EvaluationContext? context = null)
    {
        // Build the cost policy (stats-driven). Missing / stale / corrupt stats → null policy → the
        // Core path runs unchanged. The policy is HINTS ONLY: it may only change the PLAN, never the
        // result (each decision keeps the candidate a superset the Core residual still confirms).
        var policy = TryBuildPolicy(table, cdx);

        // PHASE B-1: attach the warm decoded-index cache as the optimizer's MEMOIZATION source. It is
        // change-token validated at this entry, so a warm tag is byte-for-byte the cold walk — speed only,
        // never a different (or stale) result. Null source (cache off / no path / failure) → the Core walks
        // the tree itself, exactly as before.
        var entrySource = cdx is null ? null : _cache.BeginQuery(table, cdx);
        return QueryOptimizer.FindRecords(table, cdx, filter, context, policy, entrySource);
    }

    /// <inheritdoc />
    public int Count(DbfTable table, CdxFile? cdx, string filter, EvaluationContext? context = null)
    {
        // Mirror FindRecords: drive the Core count-only path with the SAME cost policy and warm
        // decoded-index cache, so the count reuses the exact candidate-build path execution would.
        // HINTS ONLY — the count is identical to FindRecords(...).RecordNumbers.Count either way.
        var policy = TryBuildPolicy(table, cdx);
        var entrySource = cdx is null ? null : _cache.BeginQuery(table, cdx);
        return QueryOptimizer.Count(table, cdx, filter, context, policy, entrySource);
    }

    /// <inheritdoc />
    public QueryPlan Explain(DbfTable table, CdxFile? cdx, string filter, EvaluationContext? context = null)
    {
        // Mirror FindRecords: augment the Core plan with the cost policy when statistics are enabled,
        // so the ShowPlan reflects the very tags execution would drive. No stats → the Core plan.
        var policy = TryBuildPolicy(table, cdx);
        return QueryOptimizer.Explain(table, cdx, filter, context, policy);
    }

    /// <summary>
    /// Produce the Highlike COST-BASED ShowPlan (design §7): the Core <see cref="QueryPlan"/> augmented
    /// with the planner's row estimates, the chosen AND execution order (selective-first), the tags it
    /// would drive (including any low-selectivity guard it lifted) and the leaves it would full-scan.
    /// Plan-only; HINT-driven; never changes the result set. Falls back to the Core plan when statistics
    /// are missing / stale.
    /// </summary>
    public HighlikeQueryPlan ExplainPlan(DbfTable table, CdxFile? cdx, string filter, EvaluationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(table);

        var planner = TryBuildPlanner(table, cdx);
        var policy = planner is null ? null : new HighlikeOptimizerPolicy(planner);

        // The Core plan, computed WITH the policy so its per-leaf verdicts already reflect the
        // guard-lifts / index-vs-scan demotions the planner drives.
        var corePlan = QueryOptimizer.Explain(table, cdx, filter, context, policy);

        var leaves = new List<HighlikeLeafPlan>(corePlan.Conditions.Count);
        foreach (var c in corePlan.Conditions)
        {
            double est = planner?.EstimateRows(c.Condition) ?? table.RecordCount;
            bool usesIndex = c.Optimizable;
            // A guard was LIFTED when a NOT-keyed tag (the Core default would skip) is now driven.
            bool guardLifted = usesIndex && KeyHasNot(c.TagKeyExpression);
            leaves.Add(new HighlikeLeafPlan(c.Condition, est, usesIndex, c.TagName, guardLifted, c.ReasonText));
        }

        // AND-ORDERING (design §7, decision 1): intersect the cheapest (smallest estimate) leaf first
        // so the intermediate candidate sets stay small. A stable sort keeps source order among ties.
        var ordered = leaves
            .Select((l, idx) => (l, idx))
            .OrderBy(t => t.l.EstimatedRows)
            .ThenBy(t => t.idx)
            .Select(t => t.l)
            .ToList();

        var drivingTags = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in ordered)
            if (l.UsesIndex && l.TagName is not null && seen.Add(l.TagName))
                drivingTags.Add(l.TagName);

        double total = planner?.EstimateRows(filter) ?? table.RecordCount;
        return new HighlikeQueryPlan(corePlan, ordered, drivingTags, total);
    }

    // ============================================================ internals

    /// <summary>Build a cost policy from fresh statistics, or null when stats are off / unavailable.</summary>
    private IOptimizerPolicy? TryBuildPolicy(DbfTable table, CdxFile? cdx)
    {
        var planner = TryBuildPlanner(table, cdx);
        return planner is null ? null : new HighlikeOptimizerPolicy(planner);
    }

    /// <summary>
    /// Build a <see cref="HighlikeCostPlanner"/> from the table's <c>.stx</c> sidecar (lazily built /
    /// loaded). Returns null when statistics are disabled or cannot be obtained — the caller then runs
    /// the plain Core path. NEVER throws: stats are HINTS, never a correctness dependency.
    /// </summary>
    private HighlikeCostPlanner? TryBuildPlanner(DbfTable table, CdxFile? cdx)
    {
        if (table is null || !Options.EnableStatistics) return null;
        try
        {
            // PERF-FIX: go through the engine-owned stats MEMO instead of re-Loading / re-Building the
            // .stx on every query. The memo is change-token validated, so a reused entry is exactly the
            // freshly-loaded one (HINTS ONLY — never a correctness dependency).
            StxStatistics? stats = _statsCache.GetOrBuild(table, cdx);
            return new HighlikeCostPlanner(stats, table.RecordCount, Options, cdx);
        }
        catch
        {
            return null; // any failure → no policy → the Core path (still correct)
        }
    }

    /// <summary>True when a tag KEY expression contains a word-boundaried <c>NOT</c> (the Core guard trigger).</summary>
    private static bool KeyHasNot(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        string u = key.ToUpperInvariant();
        int idx = 0;
        while ((idx = u.IndexOf("NOT", idx, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = idx == 0 || !(char.IsLetterOrDigit(u[idx - 1]) || u[idx - 1] == '_');
            int after = idx + 3;
            bool rightOk = after >= u.Length || !(char.IsLetterOrDigit(u[after]) || u[after] == '_');
            if (leftOk && rightOk) return true;
            idx = after;
        }
        return false;
    }
}
