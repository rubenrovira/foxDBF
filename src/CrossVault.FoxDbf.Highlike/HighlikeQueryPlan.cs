using System.Text;
using CrossVault.FoxDbf.Query;

namespace CrossVault.FoxDbf.Highlike;

/// <summary>
/// The cost-based plan for ONE leaf of a filter, as chosen by the <see cref="HighlikeCostPlanner"/>:
/// the leaf text, its <see cref="EstimatedRows"/>, whether the planner chose to drive it via an index
/// (<see cref="UsesIndex"/>) and the <see cref="TagName"/> it would drive, plus
/// <see cref="GuardLifted"/> — true when this is a tag the Core default mode would have SKIPPED
/// (NOT-keyed / boolean / low-NDV) but the stats proved selective enough to drive.
/// </summary>
/// <remarks>HINT-ONLY: this is a plan report; nothing here changes the result set.</remarks>
public sealed class HighlikeLeafPlan
{
    /// <summary>The leaf condition's source text.</summary>
    public string Condition { get; }

    /// <summary>The planner's row estimate for this leaf (a HINT).</summary>
    public double EstimatedRows { get; }

    /// <summary>True when the planner chose to drive this leaf via a CDX tag (vs. a residual full scan).</summary>
    public bool UsesIndex { get; }

    /// <summary>The CDX tag the planner would drive when <see cref="UsesIndex"/>, else null.</summary>
    public string? TagName { get; }

    /// <summary>
    /// True when the Core default mode would skip this tag (e.g. a NOT-keyed / boolean / low-NDV index)
    /// but the cost planner LIFTED that guard because the stats prove the tag selective enough to drive.
    /// </summary>
    public bool GuardLifted { get; }

    /// <summary>A short human-readable explanation of the chosen plan for this leaf.</summary>
    public string Reason { get; }

    /// <summary>Construct a per-leaf cost plan.</summary>
    public HighlikeLeafPlan(string condition, double estimatedRows, bool usesIndex,
        string? tagName, bool guardLifted, string reason)
    {
        Condition = condition;
        EstimatedRows = estimatedRows;
        UsesIndex = usesIndex;
        TagName = tagName;
        GuardLifted = guardLifted;
        Reason = reason;
    }
}

/// <summary>
/// The Highlike cost-based ShowPlan (design §7) for a filter: the underlying Core
/// <see cref="QueryPlan"/> (<see cref="CorePlan"/>), the leaves in the planner's chosen EXECUTION
/// ORDER (<see cref="OrderedLeaves"/>, cheapest-first for an AND), the tags the planner would actually
/// drive in that order (<see cref="DrivingTags"/>), and the overall row estimate
/// (<see cref="EstimatedRows"/>). It augments — never contradicts — the Core plan: the result set is
/// always identical, so this only reports HOW the answer is computed.
/// </summary>
public sealed class HighlikeQueryPlan
{
    /// <summary>The underlying Core ShowPlan (the SYS(3054)-style verdict the planner augments).</summary>
    public QueryPlan CorePlan { get; }

    /// <summary>The leaf plans in the planner's chosen execution order (selective-first for an AND).</summary>
    public IReadOnlyList<HighlikeLeafPlan> OrderedLeaves { get; }

    /// <summary>The CDX tags the planner would drive, in the chosen order.</summary>
    public IReadOnlyList<string> DrivingTags { get; }

    /// <summary>The planner's overall row estimate for the whole filter (a HINT).</summary>
    public double EstimatedRows { get; }

    /// <summary>Construct a Highlike cost plan.</summary>
    public HighlikeQueryPlan(QueryPlan corePlan, IReadOnlyList<HighlikeLeafPlan> orderedLeaves,
        IReadOnlyList<string> drivingTags, double estimatedRows)
    {
        CorePlan = corePlan;
        OrderedLeaves = orderedLeaves;
        DrivingTags = drivingTags;
        EstimatedRows = estimatedRows;
    }

    /// <summary>A readable cost-plan report: the Core level, the driving tags, and one line per ordered leaf.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append("Highlike cost plan (core level: ").Append(CorePlan.Overall)
          .Append(", est rows: ").Append(EstimatedRows.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))
          .Append(")\n");
        sb.Append("Driving tags (in order): ")
          .Append(DrivingTags.Count == 0 ? "(none)" : string.Join(", ", DrivingTags))
          .Append('\n');
        foreach (var l in OrderedLeaves)
        {
            sb.Append("  ").Append(l.Condition).Append(" -> ");
            sb.Append(l.UsesIndex ? $"index ({l.TagName})" : "scan/residual");
            if (l.GuardLifted) sb.Append(" [guard lifted]");
            sb.Append(" ~").Append(l.EstimatedRows.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
