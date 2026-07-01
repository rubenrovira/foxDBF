namespace CrossVault.FoxDbf;

/// <summary>
/// Describes one <c>OBJECTTYPE=="Table"</c> entry of a Visual FoxPro <c>.dbc</c>
/// container (plan §A8), passed to a user-supplied
/// <see cref="DbfDatabaseOptions.TableResolver"/> when the primary <c>PROPERTY</c>
/// path could not be resolved. Carries the logical <see cref="ObjectName"/>, the
/// directory of the owning <c>.dbc</c> (for relative-path resolution), the path
/// scanned out of the <c>PROPERTY</c> memo (if any), and the table's long field
/// names from the DBC Field tree.
/// </summary>
public sealed record DbcTableInfo
{
    /// <summary>The logical (long) table name as stored in the DBC <c>OBJECTNAME</c> field.</summary>
    public required string ObjectName { get; init; }

    /// <summary>Absolute directory of the owning <c>.dbc</c>; relative member paths resolve against it (case-insensitively).</summary>
    public string DatabaseDirectory { get; init; } = "";

    /// <summary>
    /// The relative <c>.dbf</c> path extracted from the table's <c>PROPERTY</c> memo
    /// (first printable run ending in <c>.dbf</c>, scanned as RAW bytes, §A8), or
    /// <see langword="null"/> when the <c>PROPERTY</c> memo yielded nothing.
    /// </summary>
    public string? PropertyPath { get; init; }

    /// <summary>The member table's long field names from the DBC Field tree, in physical column order (§A8).</summary>
    public IReadOnlyList<string> LongFieldNames { get; init; } = Array.Empty<string>();
}
