namespace CrossVault.FoxDbf.Highlike;

/// <summary>
/// An immutable SNAPSHOT of the Highlike STATISTICS memo counters — the analogue of
/// <see cref="HighlikeCacheStatistics"/> for the engine-owned <c>.stx</c> statistics cache. Exposed so
/// tests can prove the perf structure WITHOUT timing: the expensive Load/Build/parse of a table's
/// statistics should happen ONCE (a <see cref="Computes"/>) and every later query should REUSE the
/// memoised <see cref="StxStatistics"/> (a <see cref="Reuses"/>) rather than re-reading / re-parsing /
/// re-walking on every query. After a table write the change-token moves and the next query forces one
/// fresh <see cref="Computes"/>.
/// </summary>
/// <remarks>
/// The numbers here only describe PLAN-building work. Statistics are HINTS ONLY: a cached, stale,
/// missing, or wrong stat may only change the plan, never the result set.
/// </remarks>
public sealed class HighlikeStatsCacheStatistics
{
    /// <summary>
    /// The number of EXPENSIVE statistics acquisitions performed — each one a full
    /// <see cref="HighlikeStatistics.Load"/> (File.ReadAllText + JSON parse) or
    /// <see cref="HighlikeStatistics.Build"/> (a whole-index re-walk). The bug this counter exposes:
    /// today it increments once PER query; once memoised it must increment once per (table, change-token).
    /// </summary>
    public long Computes { get; }

    /// <summary>The number of queries that REUSED an already-memoised <see cref="StxStatistics"/> (no Load/Build).</summary>
    public long Reuses { get; }

    /// <summary>The number of distinct table statistics currently resident in the memo.</summary>
    public int EntryCount { get; }

    public HighlikeStatsCacheStatistics(long computes, long reuses, int entryCount)
    {
        Computes = computes;
        Reuses = reuses;
        EntryCount = entryCount;
    }

    /// <summary>The all-zero snapshot (a fresh or disposed stats memo).</summary>
    public static HighlikeStatsCacheStatistics Empty { get; } = new(0, 0, 0);
}
