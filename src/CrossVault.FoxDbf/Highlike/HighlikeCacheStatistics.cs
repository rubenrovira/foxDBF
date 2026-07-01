namespace CrossVault.FoxDbf.Highlike;

/// <summary>
/// An immutable SNAPSHOT of the Highlike warm index-cache counters (design §5, Peak 2). Exposed so
/// tests can prove the perf structure WITHOUT timing: a tag decoded once is a <see cref="Misses"/>;
/// every later reuse is a <see cref="Hits"/>; a detected change forces an <see cref="Evictions"/> +
/// re-decode (the next access is a miss again).
/// </summary>
public sealed class HighlikeCacheStatistics
{
    /// <summary>Cache lookups that found a fresh, token-validated decoded tag (served from RAM).</summary>
    public long Hits { get; }

    /// <summary>Cache lookups that had to DECODE the tag (first touch, or after an eviction).</summary>
    public long Misses { get; }

    /// <summary>Entries evicted because the cheap change-token (or the FSW) detected a change.</summary>
    public long Evictions { get; }

    /// <summary>The number of decoded tag entries currently resident in the cache.</summary>
    public int EntryCount { get; }

    /// <summary>
    /// The number of live <see cref="System.IO.FileSystemWatcher"/> instances the engine currently owns
    /// (one per watched table on a Fixed drive). Always 0 in forced <see cref="HighlikeDriveKind.Network"/>
    /// mode and 0 after the engine is disposed.
    /// </summary>
    public int ActiveWatchers { get; }

    public HighlikeCacheStatistics(long hits, long misses, long evictions, int entryCount, int activeWatchers)
    {
        Hits = hits;
        Misses = misses;
        Evictions = evictions;
        EntryCount = entryCount;
        ActiveWatchers = activeWatchers;
    }

    /// <summary>The all-zero snapshot (a fresh or disposed cache).</summary>
    public static HighlikeCacheStatistics Empty { get; } = new(0, 0, 0, 0, 0);
}
