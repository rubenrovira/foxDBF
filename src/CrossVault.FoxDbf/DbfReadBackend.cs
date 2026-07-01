namespace CrossVault.FoxDbf;

/// <summary>
/// Selects the byte read-source backing a read-only <see cref="DbfTable"/> (and its
/// sidecar <see cref="MemoFile"/> / <see cref="Index.CdxFile"/>) — Highlike Phase B-3,
/// "design Peak 1". An OPT-IN, dependency-free (System.IO.MemoryMappedFiles is in-box)
/// memory-mapped READ path that serves record / page / memo-block reads from an
/// OS-page-cache-backed mapped view instead of a per-read <see cref="System.IO.FileStream"/>
/// syscall + buffer copy.
/// </summary>
/// <remarks>
/// SAFETY (FoxPro runs on SMB shares where mmap is unstable): memory-mapping is for
/// LOCAL / fixed storage ONLY. On a NETWORK / UNC path the backend AUTO-FALLS-BACK to
/// <see cref="FileStream"/> regardless of this setting (see <see cref="DbfOptions.DriveKind"/>).
/// WRITES always use the existing <see cref="Write.DbfWriter"/> <see cref="System.IO.FileStream"/>
/// path; a map is READ-ONLY, must not block a concurrent writer, and must unmap cleanly so
/// the file is fully released on dispose. Any mmap failure NEVER throws — it degrades to
/// <see cref="FileStream"/> (still byte-identical / correct).
/// </remarks>
public enum DbfReadBackend
{
    /// <summary>The classic per-read <see cref="System.IO.FileStream"/> path (the default — opt-in is OFF).</summary>
    FileStream = 0,

    /// <summary>
    /// Memory-map on LOCAL / fixed storage, else fall back to <see cref="FileStream"/>. The
    /// "smart" opt-in: never maps a network share.
    /// </summary>
    Auto = 1,

    /// <summary>
    /// Force the memory-mapped read source. Still AUTO-FALLS-BACK to <see cref="FileStream"/>
    /// on a network / UNC path and on any mapping failure (e.g. an empty / too-small file).
    /// </summary>
    MemoryMapped = 2,
}
