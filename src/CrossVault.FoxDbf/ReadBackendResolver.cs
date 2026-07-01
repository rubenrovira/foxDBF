namespace CrossVault.FoxDbf;

/// <summary>
/// Resolves the OPT-IN memory-mapped read backend for a path-based open (Highlike Phase B-3).
/// The single decision point: <see cref="TryOpenMapped"/> returns a memory-mapped <see cref="Stream"/>
/// ONLY when mapping was requested AND the file lives on LOCAL/fixed storage AND it maps cleanly;
/// otherwise it returns <see langword="null"/> and the caller opens its classic <see cref="FileStream"/>
/// (so the default path and every fall-back keep their EXACT existing semantics).
/// </summary>
internal static class ReadBackendResolver
{
    /// <summary>
    /// Try to open <paramref name="path"/> as a read-only memory-mapped stream honouring
    /// <paramref name="backend"/> / <paramref name="driveKind"/>. Returns <see langword="null"/> when
    /// mapping is OFF (<see cref="DbfReadBackend.FileStream"/>), the storage is NETWORK/UNC, or any
    /// mapping attempt fails — in every such case the caller uses its existing <see cref="FileStream"/>.
    /// Never throws.
    /// </summary>
    public static Stream? TryOpenMapped(string path, DbfReadBackend backend, DbfDriveKind driveKind)
    {
        if (backend == DbfReadBackend.FileStream)
            return null; // opt-in is OFF — classic path.

        if (ResolveDriveKind(path, driveKind) != DbfDriveKind.Fixed)
            return null; // network/UNC: an mmap over SMB is unstable — force the FileStream path.

        return MemoryMappedReadStream.TryCreate(path); // null on any mapping failure → FileStream.
    }

    /// <summary>
    /// Resolve Fixed-vs-Network for <paramref name="path"/> (mirrors the Highlike <c>HighlikeDriveKind</c>
    /// detection: a UNC <c>\\</c> prefix or <see cref="DriveType.Network"/> root → Network). An explicit
    /// non-Auto <paramref name="driveKind"/> wins. On any uncertainty we behave LOCALLY (Fixed) — the
    /// map then either succeeds or falls back on its own; correctness never depends on this guess.
    /// </summary>
    private static DbfDriveKind ResolveDriveKind(string path, DbfDriveKind driveKind)
    {
        if (driveKind != DbfDriveKind.Auto)
            return driveKind;

        try
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
                return DbfDriveKind.Network; // UNC share

            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
                return DbfDriveKind.Fixed;

            return new DriveInfo(root).DriveType == DriveType.Network
                ? DbfDriveKind.Network
                : DbfDriveKind.Fixed;
        }
        catch
        {
            return DbfDriveKind.Fixed; // uncertain → local; the map's own fall-back still guards.
        }
    }
}
