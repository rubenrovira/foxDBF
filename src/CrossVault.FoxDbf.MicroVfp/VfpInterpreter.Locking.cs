using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.MicroVfp;

/// <summary>
/// microVFP LOCK SURFACE (project-review finding 5.13). RLOCK/LOCK/FLOCK/UNLOCK/ISRLOCKED/ISFLOCKED are
/// wired to the REAL Core byte-range lock primitives (<see cref="DbfWriter"/> — VFP-byte-compatible offsets
/// via <c>VfpLock</c>), so a stored procedure that coordinates via RLOCK is mutually exclusive with a
/// concurrent VFP client on the same shared table (the 17/17 multi-user battle interop, both directions).
/// <para>
/// WHERE THE LOCK LIVES. Windows byte-range locks are per-HANDLE and MANDATORY (a lock on one handle blocks
/// even a raw write from another handle in the SAME process — verified empirically). So an explicit RLOCK
/// MUST live on the very handle that also performs the write, or our own lock would block our own REPLACE.
/// The write hot-path already keeps a per-path cached <see cref="DbfWriter"/> (finding 5.5); RLOCK/FLOCK
/// take their lock on THAT handle. A subsequent REPLACE/DELETE on the locked record goes through the same
/// cached writer, whose <c>WithLock</c> re-entry guard sees the held lock and skips re-locking — so the
/// canonical <c>RLOCK() → REPLACE → UNLOCK</c> idiom works without self-conflict. Because the lock rides the
/// cached writer, the cached-writer prune/invalidate is PINNED while a lock is held
/// (<see cref="DbfWriter.HasHeldLocks"/>): a held lock is dropped ONLY by UNLOCK / area close / CLEAR ALL /
/// session dispose, never silently.
/// </para>
/// <para>
/// COW TRANSACTION. Inside an ADO.NET copy-on-write transaction the write path is redirected to a PRIVATE
/// copy, but a lock is COORDINATION with other processes and must land on the LIVE file — so the lock target
/// path is canonicalised copy→live via <see cref="VfpSession.TxLivePath"/>. Writes still go to the isolated
/// copy (a different file), so there is no self-conflict.
/// </para>
/// </summary>
public sealed partial class VfpInterpreter
{
    // Per-work-area explicit lock state (keyed by AREA number). The byte-range locks themselves live on the
    // per-path cached DbfWriter (see class remarks); this tracks WHICH records / whole-file we hold so
    // ISRLOCKED/ISFLOCKED can report our own locks, MULTILOCKS OFF can release the previous record lock, and
    // the lifecycle hooks can release exactly what we own.
    // Per-DATA-SESSION (5.14): byte-range lock bookkeeping belongs to the session's areas; non-readonly so a
    // SET DATASESSION switch re-points it at the target session's locks (releasing a session frees its locks).
    private Dictionary<int, AreaLockSet> _areaLocks = new();

    private sealed class AreaLockSet
    {
        public string Path = string.Empty;         // full LIVE .dbf path the locks live on (cached-writer key).
        public readonly HashSet<int> Records = new(); // 1-based record numbers we hold (0 = header lock).
        public bool File;                            // whole-file (FLOCK) lock held.
        public bool Any => File || Records.Count > 0;
    }

    // Lock-acquire retry (SET REPROCESS). Timing is deliberately APPROXIMATE (finding 5.13 OUT item): a
    // headless library cannot block forever, so "retry forever" / AUTOMATIC is bounded. Count mode (TO n) is
    // n retries. The fail-fast branch (TO 0 + ON ERROR) never retries.
    private const int LockRetryDelayMs = 5;
    private const int LockAutomaticRetryCap = 20;   // AUTOMATIC / TO-0-without-handler ⇒ bounded ~100 ms, then .F.

    // ─────────────────────────── RLOCK / LOCK ───────────────────────────

    // RLOCK([cRecordList,] nWorkArea | cTableAlias) — 0 args: current record of current area; 1 arg:
    // current record of that area/alias; 2 args: a (comma-separated) record-number list in that area/alias.
    // Returns .T. only if EVERY requested record was locked (or is already ours), else .F.
    //
    // ATOMIC LIST CONTRACT (finding 5.13 — VFP9-oracle-verified, not guessed). A MULTI-record RLOCK is
    // all-or-nothing: RLOCK("2,3,4") that cannot lock a contended record (3) returns .F. and leaves NEITHER
    // 2 NOR 4 locked — but a record ALREADY OWNED before the call SURVIVES (oracle: pre-locking 2, then a
    // failing RLOCK("2,3,4") leaves ISRLOCKED(2)=.T., ISRLOCKED(4)=.F.). So this snapshots the records held
    // BEFORE the call and, on any failure, releases only the ones THIS call newly acquired.
    //
    // SET MULTILOCKS OFF + a list is NOT an error and NOT single-lock-only: the oracle shows real VFP9
    // processes the list record-by-record — each new lock releasing the previous one (the MULTILOCKS-OFF
    // rule) — and returns .T. holding ONLY THE LAST record (RLOCK("5,6,7") ⇒ .T., ISRLOCKED(7) only). That
    // is exactly what the record-by-record loop below produces, so MULTILOCKS OFF needs no special-casing;
    // the atomic rollback only ever fires on a genuine lock FAILURE (contention), never on the OFF-release.
    private VfpValue FnRlock(VfpValue[] a)
    {
        int area;
        List<int> records;
        if (a.Length >= 2)
        {
            area = AreaNumber(a[1]);
            records = ParseRecordList(a[0].AsString);
        }
        else
        {
            area = a.Length == 1 ? AreaNumber(a[0]) : Session.CurrentArea;
            records = new List<int> { CurrentRecNoForLock(area) };
        }

        if (Session.AreaAt(area) is null || records.Count == 0)
            return VfpValue.Logical(false);

        var set = GetOrCreateAreaLockSet(area);
        if (set is null) return VfpValue.Logical(false);

        // Records held BEFORE this call — only for the list form (a single-record RLOCK has nothing to roll
        // back). On failure, everything NOT in this snapshot is released; the pre-owned set is preserved.
        var preOwned = records.Count > 1 ? new HashSet<int>(set.Records) : null;

        foreach (var rec in records)
        {
            if (TryLockRecord(area, rec)) continue;
            // A record in the list could not be locked ⇒ VFP9's all-or-nothing: undo THIS call's new locks.
            if (preOwned is not null) ReleaseRecordsNotIn(set, preOwned);
            return VfpValue.Logical(false);
        }
        return VfpValue.Logical(true);
    }

    // Release (and forget) every record lock in <paramref name="set"/> that was NOT owned before the current
    // RLOCK call — the all-or-nothing rollback for a failed multi-record lock. Pre-owned records + any
    // whole-file lock are left untouched. Mirrors UnlockOne/ReleaseOwnRecordLocks off the cached writer.
    private void ReleaseRecordsNotIn(AreaLockSet set, HashSet<int> preOwned)
    {
        if (set.Records.Count == 0) return;
        _cachedWriters.TryGetValue(set.Path, out var w);
        List<int>? drop = null;
        foreach (var rec in set.Records)
            if (!preOwned.Contains(rec)) (drop ??= new()).Add(rec);
        if (drop is null) return;
        foreach (var rec in drop)
        {
            if (w is not null) UnlockOne(w, rec);
            set.Records.Remove(rec);
        }
    }

    // FLOCK([nWorkArea | cTableAlias]) — take the whole-file lock on the area's live .dbf. Mutually exclusive
    // with any record lock (the file range covers the record bytes) — enforced by the OS across handles.
    private VfpValue FnFlock(VfpValue[] a)
    {
        int area = a.Length > 0 ? AreaNumber(a[0]) : Session.CurrentArea;
        var set = GetOrCreateAreaLockSet(area);
        if (set is null) return VfpValue.Logical(false);
        if (set.File) return VfpValue.Logical(true);          // already ours (idempotent).

        var writer = GetOrOpenCachedWriter(set.Path);
        // A whole-file lock supersedes our own record locks. They sit INSIDE the file range, and Windows
        // rejects an overlapping lock on the SAME handle, so release our own tracked record locks first (they
        // are re-covered by the file lock). Cross-session exclusion is unaffected (the OS still denies others).
        int[] heldRecords = set.Records.ToArray();
        ReleaseOwnRecordLocks(set);
        bool ok = TryAcquireWithReprocess(writer.LockFile, () => set.File = true);
        if (!ok)
        {
            foreach (int rec in heldRecords)
                if (TryAcquireOnce(() => { if (rec == 0) writer.LockHeader(); else writer.Lock(rec); }))
                    set.Records.Add(rec);
        }
        return VfpValue.Logical(ok);
    }

    // Take (or confirm) the record lock for 1-based recNo (0 = header) in area, honouring SET MULTILOCKS.
    private bool TryLockRecord(int area, int recNo)
    {
        var set = GetOrCreateAreaLockSet(area);
        if (set is null) return false;
        if (set.File) return true;                            // the whole-file lock already covers every record.
        if (set.Records.Contains(recNo)) return true;         // already ours — idempotent (avoid same-handle re-lock).

        // MULTILOCKS OFF (default): a new record lock releases the previously-held record lock(s) in the area.
        if (!Runtime.Multilocks) ReleaseOwnRecordLocks(set);

        var writer = GetOrOpenCachedWriter(set.Path);
        return TryAcquireWithReprocess(
            () => { if (recNo == 0) writer.LockHeader(); else writer.Lock(recNo); },
            () => set.Records.Add(recNo));
    }

    // ─────────────────────────── UNLOCK ───────────────────────────

    // UNLOCK [ALL] [RECORD n] [IN area]. ALL ⇒ every lock in EVERY area. RECORD n ⇒ that one record in the
    // (IN) area (or current). Bare ⇒ all locks in the (IN) area (or current). Records/file both released.
    private void ExecUnlock(UnlockStmt u)
    {
        if (u.All) ReleaseAllLocks();
        else
        {
            int area = u.In is not null ? ResolveAreaRef(u.In) : Session.CurrentArea;
            if (area <= 0) return;
            if (u.Record is not null) ReleaseRecordLock(area, ResolveRecNoRef(u.Record));
            else ReleaseAreaLocks(area);
        }
        // 5.13 COW-transaction hygiene: inside a transaction the RLOCK/FLOCK cached writer sits on the LIVE
        // file, which backs NO open area (the area rides the private copy). Once UNLOCK releases its byte
        // ranges the writer is lock-free and area-less, so PruneCachedWriters disposes its open live handle —
        // otherwise it would linger to the commit swap and block File.Replace. Autocommit keeps its writer
        // (it still backs the open area), so this only ever prunes the tx-only lock handle.
        if (Session.TxLivePath is not null) PruneCachedWriters();
    }

    // ─────────────────────────── ISRLOCKED / ISFLOCKED ───────────────────────────

    // ISRLOCKED([nRecord [, nWorkArea | cTableAlias]]) — report whether WE (this session) hold the lock on
    // the record, WITHOUT taking one (a whole-file lock counts as locking every record). Verified semantics:
    // ISRLOCKED reports the CURRENT session's own held lock, not a foreign process's (hackfox s4g206).
    private VfpValue FnIsRlocked(VfpValue[] a)
    {
        int area;
        int rec;
        if (a.Length == 0)
        {
            area = Session.CurrentArea;
            rec = CurrentRecNoForLock(area);
        }
        else if (a.Length == 1)
        {
            // 1 arg: a character alias selects the area (current record); a numeric selects the record.
            if (a[0].Type == VfpType.Character) { area = AreaNumber(a[0]); rec = CurrentRecNoForLock(area); }
            else { area = Session.CurrentArea; rec = (int)a[0].AsNumber; }
        }
        else
        {
            area = AreaNumber(a[1]);
            rec = (int)a[0].AsNumber;
        }
        bool held = _areaLocks.TryGetValue(area, out var s) && (s.File || s.Records.Contains(rec));
        return VfpValue.Logical(held);
    }

    // ISFLOCKED([nWorkArea | cTableAlias]) — whether WE hold the whole-file lock on the area, without locking.
    private VfpValue FnIsFlocked(VfpValue[] a)
    {
        int area = a.Length > 0 ? AreaNumber(a[0]) : Session.CurrentArea;
        return VfpValue.Logical(_areaLocks.TryGetValue(area, out var s) && s.File);
    }

    // ─────────────────────────── acquire helper (SET REPROCESS) ───────────────────────────

    // Run acquire() (which throws IOException when the range is held by another handle/process); on success
    // run onSuccess() and return true. On contention, retry per SET REPROCESS: TO 0 + ON ERROR ⇒ fail-fast
    // .F.; TO 0 without a handler / AUTOMATIC ⇒ bounded "forever"; TO n ⇒ n retries. Returns false when the
    // lock never became available.
    private bool TryAcquireWithReprocess(Action acquire, Action onSuccess)
    {
        if (TryAcquireOnce(acquire)) { onSuccess(); return true; }

        int reproc = Runtime.Reprocess;
        if (reproc == 0)
        {
            if (Runtime.OnErrorInstalled) return false;  // SET REPROCESS TO 0 + ON ERROR ⇒ immediate .F.
            reproc = -2;                                 // no handler ⇒ "retry forever" (bounded below).
        }

        int attempts = reproc == -2 ? LockAutomaticRetryCap : reproc;   // AUTOMATIC/forever bounded; else n retries.
        for (int i = 0; i < attempts; i++)
        {
            Thread.Sleep(LockRetryDelayMs);
            if (TryAcquireOnce(acquire)) { onSuccess(); return true; }
        }
        return false;
    }

    private static bool TryAcquireOnce(Action acquire)
    {
        try { acquire(); return true; }
        catch (IOException) { return false; }             // the byte range is held by another handle/process.
    }

    // ─────────────────────────── registry + lifecycle ───────────────────────────

    // Resolve (or create) the lock set for an OPEN area, keyed to its LIVE .dbf path. Returns null when the
    // area is not open / has no on-disk file.
    private AreaLockSet? GetOrCreateAreaLockSet(int area)
    {
        var wa = Session.AreaAt(area);
        if (wa?.Table.SourcePath is not { } sp) return null;
        string path = LockLivePath(sp);
        if (_areaLocks.TryGetValue(area, out var set))
        {
            if (!SamePath(set.Path, path))                // the area was repurposed onto another file under us.
            {
                ReleaseSet(set);
                set = new AreaLockSet { Path = path };
                _areaLocks[area] = set;
            }
            return set;
        }
        set = new AreaLockSet { Path = path };
        _areaLocks[area] = set;
        return set;
    }

    // The path a lock must live on: the LIVE file. In a COW transaction the area may ride a private copy;
    // coordinate on the live file (TxLivePath canonicalises copy→live). Autocommit ⇒ the path unchanged.
    private string LockLivePath(string sourcePath)
    {
        string p = Session.TxLivePath is { } toLive ? toLive(sourcePath) : sourcePath;
        return System.IO.Path.GetFullPath(p);
    }

    // The 1-based current record of an area for a no-arg RLOCK/ISRLOCKED (meta record pointer).
    private int CurrentRecNoForLock(int area) => Meta(area).RecNo;

    private static List<int> ParseRecordList(string list)
    {
        var recs = new List<int>();
        foreach (var part in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                recs.Add(n);
        return recs;
    }

    private int ResolveRecNoRef(NameRef nr)
    {
        if (nr.IsNameExpr) { var v = Eval(nr.Expr!.Expression); return IsNumeric(v) ? (int)v.AsNumber : 0; }
        if (int.TryParse(nr.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
        var ev = EvalText(nr.Name ?? string.Empty);
        return IsNumeric(ev) ? (int)ev.AsNumber : 0;
    }

    // Release our own record locks in a set (keep any whole-file lock) — the MULTILOCKS-OFF single-lock rule
    // and the FLOCK supersede-records rule both use this.
    private void ReleaseOwnRecordLocks(AreaLockSet set)
    {
        if (set.Records.Count == 0) return;
        if (_cachedWriters.TryGetValue(set.Path, out var w))
            foreach (var rec in set.Records)
                UnlockOne(w, rec);
        set.Records.Clear();
    }

    // Release ONE record lock in an area (UNLOCK RECORD n).
    private void ReleaseRecordLock(int area, int rec)
    {
        if (!_areaLocks.TryGetValue(area, out var set) || !set.Records.Remove(rec)) return;
        if (_cachedWriters.TryGetValue(set.Path, out var w)) UnlockOne(w, rec);
        if (!set.Any) _areaLocks.Remove(area);
    }

    // Release EVERY lock (records + file) held in an area, and drop the set.
    private void ReleaseAreaLocks(int area)
    {
        if (_areaLocks.Remove(area, out var set)) ReleaseSet(set);
    }

    // Release every lock in every area (UNLOCK ALL / CLEAR ALL).
    private void ReleaseAllLocks()
    {
        foreach (var set in _areaLocks.Values) ReleaseSet(set);
        _areaLocks.Clear();
    }

    // Drop any lock set whose area was CLOSED or repurposed onto a different file (called after a USE, before
    // PruneCachedWriters, so the byte-range lock is released off the still-cached writer BEFORE it is pruned).
    private void ReleaseStaleLocks()
    {
        if (_areaLocks.Count == 0) return;
        List<int>? stale = null;
        foreach (var (area, set) in _areaLocks)
        {
            var wa = Session.AreaAt(area);
            bool live = wa?.Table.SourcePath is { } sp && SamePath(LockLivePath(sp), set.Path);
            if (!live) (stale ??= new()).Add(area);
        }
        if (stale is null) return;
        foreach (var area in stale)
            if (_areaLocks.Remove(area, out var set))
                ReleaseSet(set);
    }

    // Release the byte-range locks a set holds off its cached writer (best-effort — a disposed/absent writer
    // has already released the OS lock in DbfWriter.Dispose). Does NOT touch _areaLocks (callers do).
    private void ReleaseSet(AreaLockSet set)
    {
        if (_cachedWriters.TryGetValue(set.Path, out var w))
        {
            foreach (var rec in set.Records) UnlockOne(w, rec);
            if (set.File) { try { w.UnlockFile(); } catch { /* best-effort */ } }
        }
        set.Records.Clear();
        set.File = false;
    }

    private static void UnlockOne(DbfWriter w, int rec)
    {
        try { if (rec == 0) w.UnlockHeader(); else w.Unlock(rec); }
        catch { /* best-effort byte-range release */ }
    }
}
