namespace CrossVault.FoxDbf.Query;

/// <summary>
/// The outcome of a Rushmore-style optimized query (plan §D8): the matching
/// 1-based <see cref="RecordNumbers"/> (always the EXACT same set a full table scan
/// would return), plus an optimization report — <see cref="Optimized"/> (an index
/// narrowed the candidate set), <see cref="FullyOptimized"/> (the candidate set was
/// the exact answer, no residual filtering was logically required) and
/// <see cref="RecordsScanned"/> (how many candidate records the residual filter was
/// actually evaluated on — the evidence that the optimizer avoided a full scan).
/// </summary>
public sealed class QueryResult
{
    /// <summary>The matching record numbers (1-based DBF recnos), ascending.</summary>
    public IReadOnlyList<int> RecordNumbers { get; }

    /// <summary>True when an index was used to narrow the scanned candidate set.</summary>
    public bool Optimized { get; }

    /// <summary>
    /// True when the candidate set produced by the index combination was already the
    /// exact answer (a fully index-resolvable filter), i.e. no non-optimizable residual
    /// term remained.
    /// </summary>
    public bool FullyOptimized { get; }

    /// <summary>
    /// The number of candidate records the full compiled filter was actually evaluated
    /// on (the residual scan size). For an unoptimized query this equals the non-deleted
    /// record count; for a selective indexed query it is FAR smaller than the table size.
    /// </summary>
    public int RecordsScanned { get; }

    /// <summary>Construct a query result.</summary>
    public QueryResult(IReadOnlyList<int> recordNumbers, bool optimized, bool fullyOptimized, int recordsScanned)
    {
        RecordNumbers = recordNumbers;
        Optimized = optimized;
        FullyOptimized = fullyOptimized;
        RecordsScanned = recordsScanned;
    }

    /// <summary>
    /// Bridge to records: map each matched recno through
    /// <see cref="DbfTable.GetRecord(int)"/> (1-based recno → 0-based index), skipping
    /// any record that has since become deleted / out of range. Never throws.
    /// </summary>
    public IEnumerable<DbfRecord> GetRecords(DbfTable table)
    {
        if (table is null) yield break;
        foreach (int recno in RecordNumbers)
        {
            int index = recno - 1; // 1-based recno → 0-based physical index
            if (index < 0 || index >= table.RecordCount) continue;

            DbfRecord? record;
            try { record = table.GetRecord(index); }
            catch { continue; }

            if (record is not null)
                yield return record.Value;
        }
    }
}
