using CrossVault.FoxDbf;

namespace CrossVault.FoxDbf.Highlike;

/// <summary>
/// Opt-in extension surface for attaching the <see cref="HighlikeEngine"/> accelerator to a
/// <see cref="DbfTable"/>. This is the user-facing entry point for the sub-project: calling
/// <see cref="UseHighlike(DbfTable, HighlikeOptions?)"/> routes the table's
/// <see cref="DbfTable.Query"/> / <see cref="DbfTable.ExplainQuery(string, Expressions.EvaluationContext?, Query.IQueryAccelerator?)"/>
/// through Highlike; not calling it leaves the Core optimizer path unchanged.
/// </summary>
public static class HighlikeTableExtensions
{
    /// <summary>
    /// Attach a <see cref="HighlikeEngine"/> (with the given <paramref name="options"/>, or
    /// defaults) to <paramref name="table"/> as its opt-in query accelerator, and return the table
    /// for fluent chaining. The result set is unaffected — Highlike only changes the plan (speed).
    /// </summary>
    public static DbfTable UseHighlike(this DbfTable table, HighlikeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        return table.UseAccelerator(new HighlikeEngine(options));
    }
}
