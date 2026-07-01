using System.Threading;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Index;

namespace CrossVault.FoxDbf.Highlike;

/// <summary>
/// PERF-FIX TARGET (found by the 1,000,000-row benchmark): the engine-owned CROSS-QUERY memo of the
/// parsed / built <see cref="StxStatistics"/>, the analogue of <see cref="HighlikeIndexCache"/> for the
/// cost-policy statistics. Before this fix every <see cref="HighlikeEngine.FindRecords"/> / <c>Explain</c>
/// rebuilt the cost policy, which re-ran <see cref="HighlikeStatistics.GetOrBuild"/> — a full <c>.stx</c>
/// Load (File.ReadAllText + JSON parse) or a whole-index Build — ON EVERY QUERY. That is a constant
/// per-query floor (≈12ms on a 1M-row, 6-tag table) independent of how few rows match. This type now
/// memoises the result keyed by the table path and validated by the SAME cheap change-token the index
/// cache uses, so the statistics are parsed/built ONCE and reused until a write moves the token.
/// </summary>
/// <remarks>
/// CORRECTNESS / INVARIANT (unchanged): statistics are HINTS ONLY. A cached, stale, missing, or wrong
/// stat may only change the PLAN, never the result. The memo is validated at every query entry against a
/// cheap CHANGE-TOKEN (dbf reccount + last-update stamp + file length + last-write time, AND the cdx
/// length + last-write time) — the SAME token the <see cref="HighlikeIndexCache"/> uses; on a mismatch the
/// entry is re-acquired so a post-write query gets the FRESH distribution (plan quality), but correctness
/// never depended on it. BOUNDED (LRU at <see cref="HighlikeOptions.MaxCachedTables"/>), thread-safe
/// (concurrent queries acquire a table's stats exactly once), disposable (dropped with the engine). Never
/// throws: any failure → null → the Core path (still correct). <see cref="Snapshot"/> proves the perf
/// structure WITHOUT timing: <see cref="HighlikeStatsCacheStatistics.Computes"/> increments once per
/// (table, token), every other query is a <see cref="HighlikeStatsCacheStatistics.Reuses"/>.
/// </remarks>
internal sealed class HighlikeStatsCache : IDisposable
{
    private readonly HighlikeOptions _options;

    // Guards the slot map only (held briefly). Per-table acquisition happens under the slot's own gate,
    // and no path ever takes a slot gate then this one — so the fixed order (map → slot) cannot deadlock.
    private readonly object _slotsGate = new();
    private readonly Dictionary<string, StatsSlot> _slots = new(StringComparer.OrdinalIgnoreCase);

    private long _computes;
    private long _reuses;
    private long _accessSeq; // monotonic LRU clock (bumped under _slotsGate on every slot touch)
    private bool _disposed;

    public HighlikeStatsCache(HighlikeOptions options) => _options = options ?? HighlikeOptions.Default;

    /// <summary>
    /// Return the statistics backing the cost policy for <paramref name="table"/> (through
    /// <paramref name="cdx"/>), MEMOISED across queries. The entry point the engine calls instead of
    /// <see cref="HighlikeStatistics.GetOrBuild"/> directly.
    /// <para>
    /// The slot is keyed by (dbf path + cdx path) and validated against the cheap change-token: a HIT (token
    /// unchanged) reuses the in-memory <see cref="StxStatistics"/> with NO Load/Build; a MISS (first touch,
    /// or the token moved after a write) re-acquires it ONCE and adopts the new token. A stream-only table
    /// (no path) cannot be keyed, so it is built each call (unchanged from before). Never throws (any
    /// failure → null → Core path).
    /// </para>
    /// </summary>
    public StxStatistics? GetOrBuild(DbfTable? table, CdxFile? cdx)
    {
        if (_disposed || table is null) return null;
        try
        {
            // Stream-only table: no file location to key/validate by → build each call (no memo), exactly
            // as before. Still HINTS only; tests use file-based tables.
            if (table.SourcePath is not { Length: > 0 } dbfPath)
            {
                Interlocked.Increment(ref _computes);
                return HighlikeStatistics.Build(table, cdx, _options);
            }

            string dbfFull = Normalize(dbfPath);
            string? cdxPath = cdx?.SourcePath;
            string cdxFull = cdxPath is { Length: > 0 } ? Normalize(cdxPath) : "\0stream";
            long cdxLiveLength = SafeLength(cdx);
            string key = dbfFull + "\0" + cdxFull;

            StatsSlot slot;
            lock (_slotsGate)
            {
                if (_disposed) return null;
                if (!_slots.TryGetValue(key, out slot!))
                {
                    slot = new StatsSlot(dbfFull, cdxPath, cdxLiveLength);
                    _slots[key] = slot;
                    EvictLeastRecentlyUsedIfOverCap(key);
                }
                slot.LastAccess = ++_accessSeq; // LRU touch
            }

            // The correctness core: token poll at EVERY query entry. The token folds the cdx in, so a
            // reindex following a write is caught even when the dbf header looks unchanged.
            var token = ChangeToken.Compute(slot.DbfPath, slot.CdxPath, slot.CdxLiveLength);

            lock (slot.Gate)
            {
                if (slot.HasValue && slot.Token.Equals(token))
                {
                    Interlocked.Increment(ref _reuses);
                    return slot.Stats; // warm reuse — no Load, no Build, no parse
                }

                // First touch, or the token moved (post-write): re-acquire ONCE under the slot gate, so two
                // concurrent first-touches of the same table Load/Build it once, not twice.
                StxStatistics? stats = HighlikeStatistics.GetOrBuild(table, cdx, _options);
                slot.Stats = stats;
                slot.Token = token;
                slot.HasValue = true;
                Interlocked.Increment(ref _computes);
                return stats;
            }
        }
        catch
        {
            return null; // any failure → no stats → the Core path (still correct)
        }
    }

    /// <summary>An immutable snapshot of the live memo counters (computes / reuses / resident entries).</summary>
    public HighlikeStatsCacheStatistics Snapshot()
    {
        if (_disposed) return HighlikeStatsCacheStatistics.Empty;
        int entryCount;
        lock (_slotsGate) entryCount = _slots.Count;
        return new HighlikeStatsCacheStatistics(
            Interlocked.Read(ref _computes),
            Interlocked.Read(ref _reuses),
            entryCount);
    }

    /// <summary>Drops the memo. Idempotent; never throws.</summary>
    public void Dispose()
    {
        lock (_slotsGate)
        {
            if (_disposed) return;
            _disposed = true;
            _slots.Clear();
        }
    }

    // ============================================================ bounding (slots)

    /// <summary>
    /// Keep the slot count at or below <see cref="HighlikeOptions.MaxCachedTables"/>: when the just-inserted
    /// slot pushed us over, drop the LEAST-RECENTLY-USED OTHER slot. Called under <see cref="_slotsGate"/>.
    /// Speed-only — a dropped slot just re-acquires its stats on its next query.
    /// </summary>
    private void EvictLeastRecentlyUsedIfOverCap(string justAddedKey)
    {
        int cap = _options.MaxCachedTables;
        if (cap <= 0 || _slots.Count <= cap) return;

        string? lruKey = null;
        long lruAccess = long.MaxValue;
        foreach (var kv in _slots)
        {
            if (kv.Key == justAddedKey) continue; // never evict the slot we are about to use
            if (kv.Value.LastAccess < lruAccess)
            {
                lruAccess = kv.Value.LastAccess;
                lruKey = kv.Key;
            }
        }

        if (lruKey is not null)
            _slots.Remove(lruKey);
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

    // ============================================================ per-table slot

    /// <summary>
    /// One (table + index) slot's memoised statistics: the last-validated change-token and the
    /// <see cref="StxStatistics"/> acquired under it, plus the LRU stamp. All value mutations happen under
    /// <see cref="Gate"/>.
    /// </summary>
    private sealed class StatsSlot
    {
        public readonly object Gate = new();
        public readonly string DbfPath;
        public readonly string? CdxPath;
        public readonly long CdxLiveLength;

        /// <summary>LRU clock value of the most recent touch (read/written under the cache's <c>_slotsGate</c>).</summary>
        public long LastAccess;

        public StxStatistics? Stats;
        public ChangeToken Token;
        public bool HasValue;

        public StatsSlot(string dbfPath, string? cdxPath, long cdxLiveLength)
        {
            DbfPath = dbfPath;
            CdxPath = cdxPath;
            CdxLiveLength = cdxLiveLength;
        }
    }

    // ============================================================ change token

    /// <summary>
    /// The cheap CHANGE-TOKEN read purely from the FILES (no decode) — the SAME signal the
    /// <see cref="HighlikeIndexCache"/> uses: the dbf reccount + last-update stamp (header bytes 1..3) +
    /// file length + last-write time, AND the cdx length + last-write time. An append/delete changes
    /// reccount (and usually length); a reindex following any write rewrites the cdx and bumps its
    /// last-write — so our own writes always move the token and force one fresh acquisition. (A foreign
    /// same-bucket in-place update that rewrites neither the dbf header/length nor the cdx is the documented
    /// residual — but stats are HINTS, so a stale stat only affects plan quality, never the result.)
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
