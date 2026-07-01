namespace CrossVault.FoxDbf.Write;

/// <summary>
/// Thrown by <see cref="DbfWriter.Create(string, System.Collections.Generic.IEnumerable{DbfColumnDef}, DbfCreateOptions)"/>
/// and the §D5 structure-altering surface when a requested SCHEMA is invalid (plan §D4/§D5):
/// an empty or duplicate column name, an out-of-range field length (must be 1–255), or more
/// than 255 user columns. Distinct from the per-record <see cref="DbfWriteException"/> (a
/// data-write refusal) — a schema is rejected BEFORE any byte is written so no partial/corrupt
/// file is produced (§D-Leitplanken).
/// </summary>
public sealed class DbfSchemaException : Exception
{
    public DbfSchemaException() { }
    public DbfSchemaException(string message) : base(message) { }
    public DbfSchemaException(string message, Exception inner) : base(message, inner) { }
}
