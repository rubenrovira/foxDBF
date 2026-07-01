using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;

namespace CrossVault.FoxDbf.Query;

/// <summary>
/// The Core opt-in seam for an external query ACCELERATOR (the Highlike sub-project,
/// assembly <c>CrossVault.FoxDbf.Highlike</c>). It mirrors the two static
/// <see cref="QueryOptimizer"/> entry points so an accelerator can wrap / replace the
/// execution path WITHOUT the Core ever taking a dependency on it — the dependency
/// direction stays strictly <c>Highlike → Core</c>.
/// </summary>
/// <remarks>
/// NON-NEGOTIABLE INVARIANT: an accelerator implementation MUST return the SAME result set
/// (the same record numbers) the Core <see cref="QueryOptimizer.FindRecords"/> — and a full
/// table scan — would return. An accelerator may only change the PLAN (how the answer is
/// computed: ordering / index-vs-scan / which tag), never the answer. When no accelerator is
/// attached, the Core path runs exactly as before (the seam is invisible).
/// </remarks>
public interface IQueryAccelerator
{
    /// <summary>
    /// Find every record matching <paramref name="filter"/> over <paramref name="table"/>,
    /// using <paramref name="cdx"/>'s tags where possible. Signature mirrors
    /// <see cref="QueryOptimizer"/>.FindRecords;
    /// the returned set MUST equal what that Core method would return.
    /// </summary>
    QueryResult FindRecords(DbfTable table, CdxFile? cdx, string filter, EvaluationContext? context = null);

    /// <summary>
    /// Produce a structured <see cref="QueryPlan"/> (SYS(3054) equivalent) for
    /// <paramref name="filter"/>. Signature mirrors
    /// <see cref="QueryOptimizer"/>.Explain.
    /// Plan-only and never throws.
    /// </summary>
    QueryPlan Explain(DbfTable table, CdxFile? cdx, string filter, EvaluationContext? context = null);

    /// <summary>
    /// Count every record matching <paramref name="filter"/> — the SAME number
    /// <see cref="FindRecords"/> would return as <c>RecordNumbers.Count</c>, computed more cheaply.
    /// The DEFAULT implementation simply delegates to <see cref="FindRecords"/> (always correct); an
    /// accelerator may override it with a cheaper count-only path that reuses the candidate-build path
    /// without materializing a recno list. INVARIANT: the returned count MUST equal
    /// <c>FindRecords(...).RecordNumbers.Count</c>.
    /// </summary>
    int Count(DbfTable table, CdxFile? cdx, string filter, EvaluationContext? context = null)
        => FindRecords(table, cdx, filter, context).RecordNumbers.Count;
}
