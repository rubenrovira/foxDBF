using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Query;

namespace CrossVault.FoxDbf.Highlike;

/// <summary>
/// The Highlike implementation of the Core cost seam <see cref="IOptimizerPolicy"/>: it forwards the
/// optimizer's two cost-blind decision points to the <see cref="HighlikeCostPlanner"/>. Driving the
/// decisions through the SAME Core machinery (superset bitmap + residual confirmation) is what keeps
/// the NON-NEGOTIABLE INVARIANT intact — the policy may only change the PLAN, never the result.
/// </summary>
internal sealed class HighlikeOptimizerPolicy : IOptimizerPolicy
{
    private readonly HighlikeCostPlanner _planner;

    public HighlikeOptimizerPolicy(HighlikeCostPlanner planner) => _planner = planner;

    /// <inheritdoc />
    public bool LiftGuard(CdxTag tag, string conditionText)
    {
        // A Core-skipped NOT-keyed / boolean tag may be driven once the stats prove the leaf
        // selective. On ANY estimation failure → false (keep the conservative Core skip).
        try { return _planner.ShouldLiftGuard(conditionText); }
        catch { return false; }
    }

    /// <inheritdoc />
    public bool PreferFullScan(CdxTag tag, string conditionText)
    {
        // Prefer a full scan when the leaf is estimated non-selective (> threshold). On ANY
        // estimation failure → false (keep driving the index, i.e. the default Core behaviour).
        try { return _planner.PrefersFullScan(conditionText); }
        catch { return false; }
    }
}
