namespace CrossVault.FoxDbf;

/// <summary>
/// How the storage backing a table is treated when resolving the memory-mapped read
/// backend (Highlike Phase B-3). Mirrors the Highlike <c>HighlikeDriveKind</c> detection
/// (UNC prefix <c>\\</c> or <see cref="System.IO.DriveType.Network"/>) but lives in Core so
/// the read-source seam can decide locally without a dependency on the Highlike assembly.
/// </summary>
/// <remarks>
/// Memory-mapping engages only on <see cref="Fixed"/>. <see cref="Network"/> always uses
/// <see cref="System.IO.FileStream"/> — an mmap over SMB is unstable. <see cref="Auto"/>
/// detects Fixed vs Network from the table path.
/// </remarks>
public enum DbfDriveKind
{
    /// <summary>Detect Fixed vs Network from the table path (the default).</summary>
    Auto = 0,

    /// <summary>Local / fixed disk: memory-mapping is permitted.</summary>
    Fixed = 1,

    /// <summary>Network / UNC share: force the <see cref="System.IO.FileStream"/> path (no map).</summary>
    Network = 2,
}
