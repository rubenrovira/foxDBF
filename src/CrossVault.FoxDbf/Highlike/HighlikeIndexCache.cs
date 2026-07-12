using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Index;

namespace CrossVault.FoxDbf.Highlike;

/// <summary>
/// PHASE B-1 (design §2/§5, Peak 2). The CROSS-QUERY in-memory cache of the DECODED per-tag index
/// — the sorted key→recno entries the Core <see cref="Query.QueryOptimizer"/> consumes — owned by the
/// <see cref="HighlikeEngine"/> and keyed by (table path + index path + tag identity). "Tree-walk once,
/// then serve from RAM": today every query re-walks the CDX B-tree (<see cref="CdxTag.EnumerateEntries"/>);
/// this memoizes the decoded entries so repeated queries reuse them.
/// </summary>
/// <remarks>
/// CORRECTNESS:
/// <list type="bullet">
///   <item>ONE source of truth — the cache stores EXACTLY what <see cref="CdxTag.EnumerateEntries"/>
///   yields and feeds it back through the Core optimizer's existing candidate-build path (the
///   <c>entrySource</c> seam). It never duplicates the seek / candidate logic, so a WARM result is
///   byte-for-byte the COLD result.</item>
///   <item>Every entry is validated at query entry against a cheap CHANGE-TOKEN that covers BOTH the
///   <c>.dbf</c> (reccount + last-update stamp + file length + last-write time) AND the <c>.cdx</c>
///   (its path + length + last-write time); on mismatch the table's entries are EVICTED and re-decoded —
///   so a stale recno is never served. The index source is part of the identity: a DIFFERENT <c>.cdx</c>
///   gets its own slot, and a rewritten <c>.cdx</c> (reindex / drop+recreate of a tag) is detected via the
///   cdx length / last-write EVEN WHEN the <c>.dbf</c> is byte-identical.</item>
///   <item>IMPORTANT (the residual on the DBF format): an IN-PLACE update (<see cref="Write.DbfWriter"/>
///   <c>UpdateRecord</c>) does NOT change reccount or file length, and the 3-byte header last-update stamp
///   is only DAY-granularity — so a same-day in-place update changes NONE of reccount/length/stamp;
///   detection of it through the <c>.dbf</c> alone rests entirely on the last-write TIME. Because the
///   last-write time has coarse granularity on some filesystems (FAT/exFAT 2-second buckets, some SMB
///   shares), the <c>.dbf</c> timestamp is not a reliable change signal by itself. We therefore ALSO fold
///   the <c>.cdx</c> (length + last-write) into the token: any reindex that follows our own write rewrites
///   the cdx and bumps its last-write, closing the same-bucket window for our own writes. The DOCUMENTED
///   residual (design decision (a)) is a FOREIGN same-bucket in-place update that rewrites neither
///   reccount/length/stamp nor the cdx — unclosable on the DBF format.</item>
///   <item>On a Fixed drive a <see cref="FileSystemWatcher"/> additionally evicts PROACTIVELY (disposed
///   with the engine); on a Network/UNC path (FSW unreliable over SMB) the token poll is the sole guard.</item>
///   <item>BOUNDED (design §5): slots are capped at <see cref="HighlikeOptions.MaxCachedTables"/> (LRU
///   eviction beyond it) and live FileSystemWatchers at <see cref="HighlikeOptions.MaxWatchers"/> (poll-only
///   beyond it) — so a long-lived engine querying many distinct tables cannot grow without bound or exhaust
///   OS watcher handles.</item>
///   <item>Thread-safe (concurrent queries), disposable; exposes hit / miss / eviction / entry / watcher
///   counters via <see cref="Snapshot"/>.</item>
/// </list>
/// </remarks>
internal sealed class HighlikeIndexCache : IDisposable
{
    private readonly HighlikeOptions _options;

    // Guards the table map only (held briefly). Per-table work happens under the slot's own gate, and no
    // path ever takes a slot gate then this one — so the fixed order (map → slot) cannot deadlock.
    private readonly object _tablesGate = new();
    private readonly Dictionary<string, TableSlot> _tables = new(StringComparer.OrdinalIgnoreCase);

    private long _hits;
    private long _misses;
    private long _evictions;
    private long _accessSeq;     // monotonic LRU clock (bumped under _tablesGate on every slot touch)
    private int _activeWatchers;
    private bool _disposed;

    public HighlikeIndexCache(HighlikeOptions options)
        => _options = options ?? HighlikeOptions.Default;

    /// <summary>
    /// Begin a query against <paramref name="table"/> through <paramref name="cdx"/>: validate the table's
    /// warm entries against the cheap change-token (which now covers the <c>.cdx</c> as well as the <c>.dbf</c>,
    /// evicting + scheduling a re-decode on mismatch) and return a per-tag MEMOIZATION source the Core
    /// optimizer consults instead of re-walking the B-tree. The slot is keyed by (dbf path + cdx path) so a
    /// DIFFERENT index over the same table never serves the wrong cdx's decoded recnos. Returns
    /// <see langword="null"/> (→ the Core walks the tree itself, unchanged) when the cache is disabled /
    /// disposed or the table has no path. Never throws.
    /// </summary>
    public Func<CdxTag, IEnumerable<IndexEntry>>? BeginQuery(DbfTable? table, CdxFile? cdx)
    {
        if (_disposed || !_options.EnableIndexCache) return null;
        if (table?.SourcePath is not { Length: > 0 } dbfPath) return null;

        TableSlot slot;
        try
        {
            string dbfFull = Normalize(dbfPath);
            string? cdxPath = cdx?.SourcePath;
            string cdxFull = cdxPath is { Length: > 0 } ? Normalize(cdxPath) : "\0stream";
            long cdxLiveLength = SafeLength(cdx);
            string key = dbfFull + "\0" + cdxFull;

            lock (_tablesGate)
            {
                if (_disposed) return null;
                if (!_tables.TryGetValue(key, out slot!))
                {
                    slot = new TableSlot(dbfFull, cdxPath, cdxLiveLength);
                    _tables[key] = slot;
                    AttachWatcherIfFixed(slot);
                    EvictLeastRecentlyUsedIfOverCap(key);
                }
                slot.LastAccess = ++_accessSeq; // LRU touch
            }

            // The correctness core: token poll at EVERY query entry (Fixed and Network alike). The token
            // now folds the cdx in, so a cdx-only rewrite is caught even when the dbf is byte-identical.
            slot.Validate(ChangeToken.Compute(slot.DbfPath, slot.CdxPath, slot.CdxLiveLength), ref _evictions);
        }
        catch
        {
            return null; // any failure → no cache → the Core path (still correct)
        }

        return tag => GetEntries(slot, tag);
    }

    /// <summary>
    /// The decoded entries for <paramref name="tag"/>: served from RAM on a HIT, otherwise decoded ONCE
    /// (a MISS) via <see cref="CdxTag.EnumerateEntries"/> and cached. A name HIT is only honoured when the
    /// cached tag's KEY / FOR / collation identity still matches the requested tag — a tag dropped and
    /// recreated under the SAME name with a DIFFERENT expression is treated as a change (evict + re-decode),
    /// so the cache can never serve old-key recnos for a renamed-meaning tag. The decode runs under the slot
    /// gate so two concurrent first-touches of the same tag decode it once, not twice.
    /// </summary>
    private IEnumerable<IndexEntry> GetEntries(TableSlot slot, CdxTag tag)
    {
        lock (slot.Gate)
        {
            if (slot.Tags.TryGetValue(tag.Name, out var cached))
            {
                if (cached.MatchesIdentity(tag))
                {
                    Interlocked.Increment(ref _hits);
                    return cached.Entries;
                }

                // Same name, DIFFERENT key/for/collation: the tag's meaning changed under us. Drop the
                // stale entry and re-decode — never serve recnos decoded for the old expression.
                Interlocked.Increment(ref _evictions);
                slot.Tags.Remove(tag.Name);
            }

            // Materialise EXACTLY what the live walk yields (one source of truth). If the defensive
            // walk somehow throws, it propagates to the Core, whose try/catch falls back to a correct
            // full scan — so we never cache a partial/empty list that could under-set the result.
            IReadOnlyList<IndexEntry> list = tag.EnumerateEntries().ToArray();
            slot.Tags[tag.Name] = new CachedTag(tag, list);
            Interlocked.Increment(ref _misses);
            return list;
        }
    }

    /// <summary>An immutable snapshot of the live counters (hits / misses / evictions / resident entries / watchers).</summary>
    public HighlikeCacheStatistics Snapshot()
    {
        int entryCount = 0;
        lock (_tablesGate)
            foreach (var s in _tables.Values)
                entryCount += s.EntryCount;

        return new HighlikeCacheStatistics(
            Interlocked.Read(ref _hits),
            Interlocked.Read(ref _misses),
            Interlocked.Read(ref _evictions),
            entryCount,
            Volatile.Read(ref _activeWatchers));
    }

    /// <summary>Releases every attached FileSystemWatcher and drops all cached entries. Idempotent; never throws.</summary>
    public void Dispose()
    {
        lock (_tablesGate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var s in _tables.Values)
                ReleaseSlot(s);
            _tables.Clear();
        }
    }

    // ============================================================ bounding (slots + watchers)

    /// <summary>
    /// Keep the slot count at or below <see cref="HighlikeOptions.MaxCachedTables"/>: when the just-inserted
    /// slot pushed us over, drop the LEAST-RECENTLY-USED OTHER slot (disposing its watcher and entries).
    /// Called under <see cref="_tablesGate"/>. Speed-only — a dropped slot just re-decodes on its next query.
    /// </summary>
    private void EvictLeastRecentlyUsedIfOverCap(string justAddedKey)
    {
        int cap = _options.MaxCachedTables;
        if (cap <= 0 || _tables.Count <= cap) return;

        string? lruKey = null;
        long lruAccess = long.MaxValue;
        foreach (var kv in _tables)
        {
            if (kv.Key == justAddedKey) continue; // never evict the slot we are about to use
            if (kv.Value.LastAccess < lruAccess)
            {
                lruAccess = kv.Value.LastAccess;
                lruKey = kv.Key;
            }
        }

        if (lruKey is not null && _tables.Remove(lruKey, out var victim))
            ReleaseSlot(victim);
    }

    /// <summary>Disposes a slot's watcher (if any) and drops its decoded entries. Called under <see cref="_tablesGate"/>.</summary>
    private void ReleaseSlot(TableSlot s)
    {
        try
        {
            if (s.Watcher is { } w)
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
                s.Watcher = null;
                Interlocked.Decrement(ref _activeWatchers);
            }
        }
        catch { /* best-effort release */ }
        lock (s.Gate) s.Tags.Clear();
    }

    // ============================================================ FileSystemWatcher (Fixed drives only)

    /// <summary>
    /// On a Fixed drive — and only while under the <see cref="HighlikeOptions.MaxWatchers"/> cap — attach a
    /// watcher on the table's directory (filtered to the dbf file) for PROACTIVE eviction. On a Network/UNC
    /// path, beyond the watcher cap, or on any failure, attach NONE — the token poll is the sole guard.
    /// Called under <see cref="_tablesGate"/>.
    /// </summary>
    private void AttachWatcherIfFixed(TableSlot slot)
    {
        if (ResolveDriveKind(slot.DbfPath) != HighlikeDriveKind.Fixed) return;
        if (Volatile.Read(ref _activeWatchers) >= _options.MaxWatchers) return; // cap → poll-only
        try
        {
            string? dir = Path.GetDirectoryName(slot.DbfPath);
            string file = Path.GetFileName(slot.DbfPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file)) return;

            var w = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                             | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            FileSystemEventHandler onChange = (_, _) => OnWatcherEvent(slot);
            w.Changed += onChange;
            w.Created += onChange;
            w.Deleted += onChange;
            w.Renamed += (_, _) => OnWatcherEvent(slot);
            w.Error += (_, _) => { /* swallow: the token poll still guards correctness */ };
            w.EnableRaisingEvents = true;

            slot.Watcher = w;
            Interlocked.Increment(ref _activeWatchers);
        }
        catch
        {
            // Never throw: fall back to the token poll (still correct, just not proactive).
        }
    }

    /// <summary>
    /// A watcher fired: re-validate against the token and evict if it actually changed. Reusing the SAME
    /// token comparison as the query-entry poll means a spurious event (token unchanged) never evicts —
    /// so warm reuse is not disturbed by mere file-system noise.
    /// </summary>
    private void OnWatcherEvent(TableSlot slot)
    {
        try { slot.Validate(ChangeToken.Compute(slot.DbfPath, slot.CdxPath, slot.CdxLiveLength), ref _evictions); }
        catch { /* never throw from a watcher callback */ }
    }

    private HighlikeDriveKind ResolveDriveKind(string path)
    {
        if (_options.DriveKind != HighlikeDriveKind.Auto) return _options.DriveKind;
        try
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return HighlikeDriveKind.Network; // UNC
            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return HighlikeDriveKind.Fixed;
            return new DriveInfo(root).DriveType == DriveType.Network
                ? HighlikeDriveKind.Network
                : HighlikeDriveKind.Fixed;
        }
        catch
        {
            return HighlikeDriveKind.Fixed; // uncertain → behave locally (FSW + poll); the poll still guards
        }
    }

    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private static long SafeLength(CdxFile? cdx)
    {
        try { return cdx?.Index.Length ?? -1; }
        catch { return -1; }
    }

    // ============================================================ cached tag (identity + entries)

    /// <summary>
    /// One decoded tag: the entries PLUS the identity (KEY / FOR / collation / order / uniqueness / key type) they were decoded from, so a
    /// name collision against a recreated tag with a different expression is detected rather than served stale.
    /// </summary>
    private sealed class CachedTag
    {
        public readonly IReadOnlyList<IndexEntry> Entries;
        private readonly string _key;
        private readonly string _for;
        private readonly string _collation;
        private readonly bool _descending;
        private readonly bool _unique;
        private readonly IndexKeyType _keyType;

        public CachedTag(CdxTag tag, IReadOnlyList<IndexEntry> entries)
        {
            Entries = entries;
            _key = tag.KeyExpression ?? string.Empty;
            _for = tag.ForExpression ?? string.Empty;
            _collation = tag.Collation ?? string.Empty;
            _descending = tag.Descending;
            _unique = tag.IsUnique;
            _keyType = tag.KeyType;
        }

        /// <summary>True when <paramref name="tag"/> still has the exact identity we decoded.</summary>
        public bool MatchesIdentity(CdxTag tag)
            => string.Equals(_key, tag.KeyExpression ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(_for, tag.ForExpression ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(_collation, tag.Collation ?? string.Empty, StringComparison.Ordinal)
            && _descending == tag.Descending
            && _unique == tag.IsUnique
            && _keyType == tag.KeyType;
    }

    // ============================================================ per-table slot

    /// <summary>
    /// One (table + index) slot's warm state: its last-validated change-token, its decoded tags (keyed by
    /// tag name), its LRU stamp, and its optional FileSystemWatcher. All entry mutations happen under
    /// <see cref="Gate"/>.
    /// </summary>
    private sealed class TableSlot
    {
        public readonly object Gate = new();
        public readonly string DbfPath;
        public readonly string? CdxPath;
        public readonly long CdxLiveLength;
        public readonly Dictionary<string, CachedTag> Tags = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>LRU clock value of the most recent touch (read/written under the cache's <c>_tablesGate</c>).</summary>
        public long LastAccess;

        private ChangeToken _token;
        private bool _hasToken;

        /// <summary>The engine-owned watcher (Fixed drive only); null over the network or after disposal.</summary>
        public FileSystemWatcher? Watcher;

        public TableSlot(string dbfPath, string? cdxPath, long cdxLiveLength)
        {
            DbfPath = dbfPath;
            CdxPath = cdxPath;
            CdxLiveLength = cdxLiveLength;
        }

        public int EntryCount { get { lock (Gate) return Tags.Count; } }

        /// <summary>
        /// Validate the warm entries against <paramref name="token"/>: on the first call just record it; on
        /// a MISMATCH evict every resident tag (counting one eviction per dropped entry) and adopt the new
        /// token so the next access re-decodes. Cheap and idempotent — safe to call from both the query poll
        /// and the watcher callback.
        /// </summary>
        public void Validate(ChangeToken token, ref long evictions)
        {
            lock (Gate)
            {
                if (_hasToken && _token.Equals(token)) return; // unchanged → keep the warm entries
                if (Tags.Count > 0)
                {
                    Interlocked.Add(ref evictions, Tags.Count);
                    Tags.Clear();
                }
                _token = token;
                _hasToken = true;
            }
        }
    }

    // ============================================================ change token

    /// <summary>
    /// The cheap CHANGE-TOKEN read purely from the FILES (no decode): the dbf reccount + last-update stamp
    /// (header bytes 1..3) + file length + last-write time, AND the cdx length + last-write time. Used
    /// identically by the query-entry poll and the watcher callback.
    /// <para>
    /// NOTE ON DETECTION (corrected per the DBF format): an APPEND / DELETE changes reccount (and usually
    /// length); an IN-PLACE update does NOT change reccount or length, and the 3-byte stamp is only
    /// DAY-granularity — so a same-day in-place update changes NONE of reccount/length/stamp, and the dbf's
    /// last-write TIME (coarse on FAT/exFAT/SMB) is the only dbf-side signal. We therefore ALSO fold the cdx
    /// (length + last-write): a reindex following any of our writes rewrites the cdx and bumps its last-write,
    /// so our own writes are always detected. The remaining residual is a FOREIGN same-bucket in-place update
    /// that rewrites neither the dbf header/length nor the cdx — unclosable on the format.
    /// </para>
    /// </summary>
    private readonly struct ChangeToken : IEquatable<ChangeToken>
    {
        private readonly int _recordCount;
        private readonly long _length;
        private readonly long _lastWriteTicks;
        private readonly int _stamp;
        private readonly long _cdxLength;
        private readonly long _cdxLastWriteTicks;

        private ChangeToken(int recordCount, long length, long lastWriteTicks, int stamp,
            long cdxLength, long cdxLastWriteTicks)
        {
            _recordCount = recordCount;
            _length = length;
            _lastWriteTicks = lastWriteTicks;
            _stamp = stamp;
            _cdxLength = cdxLength;
            _cdxLastWriteTicks = cdxLastWriteTicks;
        }

        public static ChangeToken Compute(string dbfPath, string? cdxPath, long cdxLiveLength)
        {
            long length = -1, lastWrite = -1;
            int recordCount = -1, stamp = -1;

            try
            {
                var fi = new FileInfo(dbfPath);
                if (fi.Exists)
                {
                    length = fi.Length;
                    lastWrite = fi.LastWriteTimeUtc.Ticks;
                }
            }
            catch { /* leave sentinels */ }

            try
            {
                using var fs = new FileStream(dbfPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                Span<byte> head = stackalloc byte[8];
                int read = fs.Read(head);
                if (read >= 4) stamp = (head[1] << 16) | (head[2] << 8) | head[3];      // YY/MM/DD last-update
                if (read >= 8) recordCount = head[4] | (head[5] << 8) | (head[6] << 16) | (head[7] << 24);
            }
            catch { /* leave sentinels */ }

            // The index source — the cdx-blind hole closer. A rewritten / reindexed / swapped cdx changes
            // its length and/or last-write even when the dbf is byte-identical.
            long cdxLength = cdxLiveLength, cdxLastWrite = -1;
            if (cdxPath is { Length: > 0 })
            {
                try
                {
                    var ci = new FileInfo(cdxPath);
                    if (ci.Exists)
                    {
                        cdxLength = ci.Length;
                        cdxLastWrite = ci.LastWriteTimeUtc.Ticks;
                    }
                }
                catch { /* leave sentinels (live length retained) */ }
            }

            return new ChangeToken(recordCount, length, lastWrite, stamp, cdxLength, cdxLastWrite);
        }

        public bool Equals(ChangeToken o)
            => _recordCount == o._recordCount && _length == o._length
            && _lastWriteTicks == o._lastWriteTicks && _stamp == o._stamp
            && _cdxLength == o._cdxLength && _cdxLastWriteTicks == o._cdxLastWriteTicks;

        public override bool Equals(object? o) => o is ChangeToken c && Equals(c);

        public override int GetHashCode()
            => HashCode.Combine(_recordCount, _length, _lastWriteTicks, _stamp, _cdxLength, _cdxLastWriteTicks);
    }
}
