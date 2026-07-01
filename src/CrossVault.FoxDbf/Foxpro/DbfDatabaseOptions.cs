namespace CrossVault.FoxDbf;

/// <summary>
/// Open-time options for a <see cref="DbfDatabase"/> (Visual FoxPro <c>.dbc</c>
/// container, plan §A8). Controls the member-table file-resolution fallback chain
/// and the per-member <see cref="DbfOptions"/> used when each member table is
/// opened.
/// </summary>
/// <remarks>
/// Resolution order (§A8): the physical <c>.dbf</c> path from the table's
/// <c>PROPERTY</c> memo (primary) → <c>OBJECTNAME + ".dbf"</c> in the DBC directory →
/// <see cref="TableResolver"/> hook → optional <c>U_XCASE</c> custom field (only when
/// <see cref="UseXCaseFallback"/> is set; xCase-specific, not a VFP standard).
/// </remarks>
public sealed record DbfDatabaseOptions
{
    /// <summary>
    /// Optional user-supplied member-file resolver (§A8 fallback step). Consulted only
    /// after BOTH the primary <c>PROPERTY</c> path AND the <c>OBJECTNAME + ".dbf"</c>
    /// fallback fail to resolve to an existing file; returns an absolute/relative
    /// <c>.dbf</c> path or <see langword="null"/> to fall through to the remaining
    /// (<c>U_XCASE</c>) fallback.
    /// </summary>
    public Func<DbcTableInfo, string?>? TableResolver { get; init; }

    /// <summary>
    /// Opt-in (default <see langword="false"/>): consult the xCase-specific
    /// <c>U_XCASE</c> custom DBC field as a last-resort filename source (§A8). Off by
    /// default because <c>U_XCASE</c> is a tooling artifact, not a VFP standard.
    /// </summary>
    public bool UseXCaseFallback { get; init; }

    /// <summary>
    /// The <see cref="DbfOptions"/> applied when opening each member table. Defaults
    /// to a fresh <see cref="DbfOptions"/> (system <c>_NullFlags</c> column hidden,
    /// NULL flags applied) — long field names from the DBC are layered on top (§A8).
    /// </summary>
    public DbfOptions TableOptions { get; init; } = new();
}
