using System.Text;

namespace CrossVault.FoxDbf.Query;

/// <summary>
/// The overall Rushmore optimization level of a <see cref="QueryPlan"/> (the VFP
/// SYS(3054) headline): <see cref="None"/> (no leaf could use an index — a full scan),
/// <see cref="Partial"/> (some leaves resolved to a tag but a non-optimizable residual
/// term remains), <see cref="Full"/> (EVERY leaf resolved to a tag — the candidate set
/// is driven entirely by index seeks, though a residual may still TRIM a collation/EXACT
/// superset down to the exact answer).
/// </summary>
public enum OptimizationLevel
{
    /// <summary>Nothing was optimizable — the filter is a full table scan.</summary>
    None,

    /// <summary>Some leaves resolved to a tag; at least one leaf is a non-optimizable residual.</summary>
    Partial,

    /// <summary>Every leaf resolved to a tag — the scan is driven entirely by index seeks.</summary>
    Full,
}

/// <summary>
/// Why a single leaf condition is — or is NOT — index-optimizable. <see cref="Optimized"/>
/// means a tag drove the leaf; every other value is a REASON the leaf fell to the residual
/// full scan, mirroring exactly the guards the optimizer applies in
/// <see cref="QueryOptimizer.FindRecords"/> (so the plan can never disagree with execution).
/// </summary>
public enum PlanReason
{
    /// <summary>The leaf IS resolved via a CDX tag (see <see cref="ConditionPlan.TagName"/>).</summary>
    Optimized,

    /// <summary>No CDX tag's KEY expression matches the leaf's indexable side.</summary>
    NoMatchingTag,

    /// <summary>A key-matching tag exists but carries a FOR filter (a strict subset of rows) — unsafe.</summary>
    ForFiltered,

    /// <summary>A key-matching tag exists but is UNIQUE (one recno per key) — unsafe for a positive leaf.</summary>
    Unique,

    /// <summary>A key-matching tag exists but its key type / collation cannot be resolved into a superset seek.</summary>
    UnsupportedKeyType,

    /// <summary>
    /// A key-matching tag exists but its KEY expression contains a <c>NOT</c> operator — almost
    /// always a boolean / low-selectivity index. The default mode skips it because, without
    /// statistics, driving it risks scanning ~half the table; this is a COST choice, not a
    /// correctness ban (superset+residual would handle it). The Highlike cost-planner lifts this
    /// once NDV proves the tag selective. Reported by the shared tag-selection guard (<c>GuardReason</c>).
    /// </summary>
    KeyExpressionHasNot,

    /// <summary>The leaf is not a simple <c>(key) (op) (constant)</c> comparison / BETWEEN / INLIST shape.</summary>
    NotSimpleComparison,

    /// <summary>The leaf is otherwise handled only by the residual confirmation (e.g. collation mismatch, <c>&lt;&gt;</c> on a character key).</summary>
    Residual,

    /// <summary>
    /// A key-matching tag exists and is usable, but the COST policy (the Highlike planner, design §7
    /// decision 2) estimates the leaf matches more than the index-vs-scan threshold of the table, so a
    /// full scan over the residual is preferred over driving that index. A PLAN choice only — the
    /// residual still confirms every candidate, so the result set is unchanged. Only ever reported when
    /// an <see cref="IOptimizerPolicy"/> is attached; the default Core mode never produces it.
    /// </summary>
    CostPrefersScan,

    /// <summary>The optimizer was disabled (<c>SET OPTIMIZE OFF</c> / <c>EvaluationContext.Optimize = false</c>) — no index is consulted; the whole query is a full scan.</summary>
    OptimizationDisabled,
}

/// <summary>
/// The plan for ONE leaf condition of a filter: the original <see cref="Condition"/> text,
/// whether it is <see cref="Optimizable"/>, the <see cref="TagName"/> +
/// <see cref="TagKeyExpression"/> of the CDX tag that drives it when optimizable, and the
/// <see cref="Reason"/> (with a human-readable <see cref="ReasonText"/>) it fell to the
/// residual when not.
/// </summary>
public sealed class ConditionPlan
{
    /// <summary>The leaf condition's source text (as it appeared in the filter).</summary>
    public string Condition { get; }

    /// <summary>True when a CDX tag drives this leaf (an index seek narrows the candidates).</summary>
    public bool Optimizable { get; }

    /// <summary>The name of the CDX tag used, when <see cref="Optimizable"/>; else null.</summary>
    public string? TagName { get; }

    /// <summary>The KEY expression of the CDX tag used (e.g. <c>UPPER(NAME)</c>), when optimizable; else null.</summary>
    public string? TagKeyExpression { get; }

    /// <summary><see cref="PlanReason.Optimized"/> when optimizable, else the reason it is not.</summary>
    public PlanReason Reason { get; }

    /// <summary>A short human-readable explanation of <see cref="Reason"/>.</summary>
    public string ReasonText { get; }

    /// <summary>Construct a per-condition plan.</summary>
    public ConditionPlan(string condition, bool optimizable, string? tagName, string? tagKeyExpression,
        PlanReason reason, string reasonText)
    {
        Condition = condition;
        Optimizable = optimizable;
        TagName = tagName;
        TagKeyExpression = tagKeyExpression;
        Reason = reason;
        ReasonText = reasonText;
    }
}

/// <summary>
/// A structured EXPLAIN / ShowPlan for a Rushmore-optimized query (the VFP SYS(3054)
/// equivalent): the <see cref="Overall"/> level, the per-leaf <see cref="Conditions"/>,
/// the distinct <see cref="UsedTags"/> the optimizer would drive, and a readable
/// <see cref="ToString"/>. Produced by <see cref="QueryOptimizer.Explain"/> WITHOUT running
/// the residual scan — it is a plan, not an execution.
/// </summary>
public sealed class QueryPlan
{
    /// <summary>The overall optimization level (Full / Partial / None).</summary>
    public OptimizationLevel Overall { get; }

    /// <summary>The plan for each leaf condition, in source order.</summary>
    public IReadOnlyList<ConditionPlan> Conditions { get; }

    /// <summary>The distinct names of the CDX tags the optimizer would use, in first-seen order.</summary>
    public IReadOnlyList<string> UsedTags { get; }

    /// <summary>Construct a query plan.</summary>
    public QueryPlan(OptimizationLevel overall, IReadOnlyList<ConditionPlan> conditions, IReadOnlyList<string> usedTags)
    {
        Overall = overall;
        Conditions = conditions;
        UsedTags = usedTags;
    }

    /// <summary>
    /// A readable SYS(3054)-style report: the overall level, the tags used, and one line per
    /// leaf condition (optimized-via-tag, or the reason it falls to the residual).
    /// </summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append("Rushmore optimization level: ").Append(Overall).Append('\n');
        sb.Append("Using tags: ")
          .Append(UsedTags.Count == 0 ? "(none)" : string.Join(", ", UsedTags))
          .Append('\n');
        foreach (var c in Conditions)
        {
            sb.Append("  ").Append(c.Condition).Append(" -> ");
            if (c.Optimizable)
                sb.Append("optimized (tag ").Append(c.TagName)
                  .Append(" on ").Append(c.TagKeyExpression).Append(')');
            else
                sb.Append("residual (").Append(c.ReasonText).Append(')');
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
