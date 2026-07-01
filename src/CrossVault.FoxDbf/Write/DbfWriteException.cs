namespace CrossVault.FoxDbf.Write;

/// <summary>
/// Thrown by the record-write layer (plan §D1) when a write cannot proceed safely:
/// principally the 2 GB file-size guard (a DBF addresses records with 32-bit offsets,
/// so <c>HeaderLength + RecordCount * RecordLength</c> must stay below
/// <see cref="int.MaxValue"/>), but also any other refusal-to-corrupt condition.
/// </summary>
/// <remarks>
/// Distinct from the read-side <see cref="DbfCorruptHeaderException"/> so callers can
/// catch a write refusal specifically and leave the file untouched (§D-Leitplanken:
/// "auf einen Mid-Write-Fehler keinen korrupten Count hinterlassen").
/// </remarks>
public sealed class DbfWriteException : Exception
{
    public DbfWriteException() { }
    public DbfWriteException(string message) : base(message) { }
    public DbfWriteException(string message, Exception inner) : base(message, inner) { }
}
