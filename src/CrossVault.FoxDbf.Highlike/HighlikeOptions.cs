namespace CrossVault.FoxDbf.Highlike;

/// <summary>
/// Opt-in configuration for the <see cref="HighlikeEngine"/> accelerator (Phase A).
/// All options influence only the PLAN (how a query is computed), never the result set —
/// the engine's non-negotiable invariant is that its answer equals the Core optimizer's.
/// </summary>
public sealed class HighlikeOptions
{
    /// <summary>
    /// When set, the engine may consult / build the <c>.stx</c> statistics sidecar to drive its
    /// cost-based planner. OFF (the default for Phase A scaffolding) → the engine behaves exactly
    /// like the Core optimizer (an identical baseline, no statistics). Stale or missing statistics
    /// only ever change the plan (speed), never the result.
    /// </summary>
    public bool EnableStatistics { get; init; }

    /// <summary>
    /// The INDEX-vs-SCAN crossover (design §7, decision 2): when the cost-based planner estimates a
    /// leaf (or query) matches MORE than this fraction of the table, a full scan is preferred over
    /// driving that index. VFP9's classic binary-index crossover was ~0.03, BUT that assumed the old
    /// model where an indexed query still READ every record (the index only saved the filter eval).
    /// With LATE MATERIALIZATION (the optimizer reads ONLY the candidate records via random access),
    /// an index that narrows to a fraction f costs ~f of the reads+evals, so it keeps paying off far
    /// past 3% — the crossover moves up to roughly where random-access of f% beats a full sequential
    /// scan (~f &lt; 0.5 with a ~2x random-read factor; higher still with the memory-mapped backend).
    /// Default 0.5; tunable, ideally measured per table. PLAN-only: changing it can never change the
    /// result set, only HOW it is computed.
    /// </summary>
    public double IndexVsScanThreshold { get; init; } = 0.5;

    /// <summary>
    /// The selectivity fraction assumed for a NON-optimizable leaf (no matching tag / no usable stat)
    /// when the planner estimates rows (design §7, "Residual"): a fixed default (≈ 0.33). It only ever
    /// feeds the cost estimate (ordering / index-vs-scan); the residual still confirms every candidate,
    /// so the result set is unaffected.
    /// </summary>
    public double DefaultLeafFraction { get; init; } = 0.33;

    /// <summary>
    /// PHASE B-1 (design §2/§5, Peak 2): when set, the engine keeps a CROSS-QUERY in-memory cache of the
    /// DECODED per-tag index (the sorted key→recno data the optimizer consumes), keyed by table path + tag,
    /// so repeated queries "tree-walk once, then serve from RAM" instead of re-walking the CDX B-tree every
    /// time. SPEED-ONLY: every cache entry is change-token validated at query entry, so a warm result is
    /// always EXACTLY the cold Core result — the cache may never change a result nor serve a stale recno.
    /// On by default.
    /// </summary>
    public bool EnableIndexCache { get; init; } = true;

    /// <summary>
    /// PHASE B-1 invalidation strategy (design §5): how the warm index cache treats the backing storage,
    /// which decides whether a proactive <see cref="System.IO.FileSystemWatcher"/> is attached. Default
    /// <see cref="HighlikeDriveKind.Auto"/> auto-detects Fixed (FSW + token poll) vs Network (token poll
    /// only — an FSW is unreliable over SMB). Overridable for testing / known-topology deployments.
    /// </summary>
    public HighlikeDriveKind DriveKind { get; init; } = HighlikeDriveKind.Auto;

    /// <summary>
    /// PHASE B-1 BOUND (design §5, "bounded — no unbounded growth"): the maximum number of distinct table
    /// (+ index) slots kept warm at once. A long-lived engine querying many distinct tables would otherwise
    /// accumulate slots — and on a Fixed drive their FileSystemWatchers — without limit. When a new slot
    /// would exceed this cap the LEAST-RECENTLY-USED slot is dropped and its watcher disposed. Default 256.
    /// Speed-only: evicting a warm slot only forces the next query on it to re-decode (still correct).
    /// </summary>
    public int MaxCachedTables { get; init; } = 256;

    /// <summary>
    /// PHASE B-1 BOUND (design §5): the maximum number of concurrent <see cref="System.IO.FileSystemWatcher"/>
    /// handles the engine will own. Beyond this cap a new Fixed-drive slot attaches NO watcher and relies on
    /// the change-token poll alone (still correct, just not proactive) — guarding against OS watcher-handle
    /// exhaustion silently degrading eviction. Default 64.
    /// </summary>
    public int MaxWatchers { get; init; } = 64;

    /// <summary>
    /// PHASE C (design §9): the number of EQUI-DEPTH HISTOGRAM buckets harvested per tag during the
    /// single sorted walk that already produces NDV / Min / Max — no extra scan. A larger value tightens
    /// range / BETWEEN estimates at a small per-tag cost in the <c>.stx</c>. <c>0</c> (or negative)
    /// disables histogram capture, so the planner falls back to linear min/max interpolation. Default 16.
    /// PLAN-only: a stale / missing / wrong histogram may only change the plan, never the result.
    /// </summary>
    public int HistogramBuckets { get; init; } = 16;

    /// <summary>
    /// PHASE C (design §9): how many MOST-COMMON-VALUES (top-K value+count) to keep per tag from the same
    /// single walk (a run of equal keys is a frequency). Lets the planner estimate a HOT (skewed)
    /// equality value by its true count instead of the uniform <c>reccount / ndv</c>. <c>0</c> (or
    /// negative) disables MCV capture. Default 16. PLAN-only.
    /// </summary>
    public int McvCount { get; init; } = 16;

    /// <summary>The default options: statistics off (identical Core baseline); warm index cache on.</summary>
    public static HighlikeOptions Default { get; } = new();
}
