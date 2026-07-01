using CrossVault.FoxDbf.Index;

namespace CrossVault.FoxDbf.Query;

/// <summary>
/// The Core opt-in seam through which an external accelerator (the Highlike sub-project,
/// assembly <c>CrossVault.FoxDbf.Highlike</c>) AUGMENTS the basic Rushmore optimizer's
/// tag-selection decisions with COST information it does not itself have. The Core
/// <see cref="QueryOptimizer"/> stays dependency-free and behaviourally unchanged when no
/// policy is supplied (the default): the policy is consulted only at the two points where the
/// default mode makes a CONSERVATIVE, cost-blind choice (skip a NOT-keyed / low-selectivity
/// tag; always drive a key-matching tag). The dependency direction stays strictly
/// <c>Highlike → Core</c> — Core never references the accelerator.
/// </summary>
/// <remarks>
/// NON-NEGOTIABLE INVARIANT: a policy may only change the PLAN (how the answer is computed),
/// NEVER the result set. Both hooks can only ever WIDEN or keep the candidate set a SUPERSET of
/// the true matches:
/// <list type="bullet">
///   <item><see cref="LiftGuard"/> true → the optimizer drives a tag it would otherwise skip,
///   producing a SUPERSET candidate bitmap from the real index (the residual still confirms).</item>
///   <item><see cref="PreferFullScan"/> true → the optimizer treats an otherwise-optimizable leaf
///   as a residual (the all-records universe), which is the widest possible superset.</item>
/// </list>
/// Stale, missing, or deliberately WRONG statistics behind a policy can therefore only pick a
/// worse PLAN; the answer is always the Core optimizer's answer (and a full scan's).
/// </remarks>
public interface IOptimizerPolicy
{
    /// <summary>
    /// LIFT the default low-selectivity guard for <paramref name="tag"/> (design §11): the default
    /// mode skips a NOT-keyed / boolean tag because, without statistics, driving it risks scanning
    /// ~half the table. Return true when the cost statistics prove the tag selective enough for
    /// <paramref name="conditionText"/> that driving it pays — the optimizer then applies its
    /// remaining (correctness) guards and, if they pass, drives the tag. PLAN-only.
    /// </summary>
    bool LiftGuard(CdxTag tag, string conditionText);

    /// <summary>
    /// The INDEX-vs-SCAN decision (design §7, decision 2): return true when the cost statistics
    /// estimate <paramref name="conditionText"/> matches MORE than the configured threshold of the
    /// table, so a full scan over the residual is preferred over driving <paramref name="tag"/>.
    /// The optimizer then treats the leaf as a residual. PLAN-only — never changes the result.
    /// </summary>
    bool PreferFullScan(CdxTag tag, string conditionText);
}
