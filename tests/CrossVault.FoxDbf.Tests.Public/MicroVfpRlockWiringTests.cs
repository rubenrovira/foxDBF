using System;
using System.IO;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

// FileStream.Lock/Unlock are Windows byte-range locks (§D3 VFP coexistence); these tests run on Windows.
#pragma warning disable CA1416

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP LOCK-SURFACE wiring (project-review finding 5.13). Pins that RLOCK/LOCK/FLOCK/UNLOCK/
/// ISRLOCKED/ISFLOCKED take REAL VFP-byte-compatible byte-range locks (not the old always-.T. stub), so
/// TWO independent in-process VfpSession+interpreter instances contend on one shared table exactly as a
/// microVFP stored proc contends with a real VFP client: a lock held in A denies A's neighbour B, UNLOCK
/// releases, FLOCK excludes record locks, SET MULTILOCKS OFF keeps a single record lock, and locks release
/// on USE/close/dispose. Also pins the 5.5 cached-writer interplay: a held RLOCK survives a writer prune,
/// and inside a COW transaction the lock lands on the LIVE file.
///
/// These were RED against the pre-5.13 stubs (RLOCK/FLOCK always .T., ISRLOCKED/ISFLOCKED always .F.,
/// UNLOCK a no-op). SAFETY: every case builds a FRESH synthetic table in its own throwaway temp dir; no
/// committed fixture is mutated and no VFP9 oracle is invoked (public-safe). Named *RlockWiring* for the
/// fast filter.
/// </summary>
public sealed class MicroVfpRlockWiringTests
{
    // ─────────────────────────── two-session shared-table scaffolding ───────────────────────────

    private sealed class Shared : IDisposable
    {
        public string Dir { get; }
        public string DbfPath { get; }

        public Shared(int rows = 5)
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_rlock_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            DbfPath = Path.Combine(Dir, "bt.dbf");
            using var w = DbfWriter.Create(DbfPath, new[]
            {
                new DbfColumnDef("id", 'I'),
                new DbfColumnDef("name", 'C', 20),
                new DbfColumnDef("amount", 'N', 10, 2),
            });
            for (int i = 1; i <= rows; i++) w.AppendRecord(i, "ROW" + i, (decimal)i);
            w.CreateTag(new CdxTagDefinition("tid", "id"));  // structural .cdx ⇒ structural lock scheme.
            w.Flush();
        }

        /// <summary>A fresh independent session+interpreter over the shared directory (a distinct "client").</summary>
        public VfpInterpreter NewClient(out VfpSession session)
        {
            session = new VfpSession();
            session.OpenDirectory(Dir);
            return new VfpInterpreter(session);
        }

        /// <summary>The structural record-lock byte for a 1-based record (v0x30 + structural cdx).</summary>
        public static long RecByte(int recNo) => 0x7FFFFFFEL - recNo;

        /// <summary>True when the record-lock byte for <paramref name="recNo"/> is held on <paramref name="path"/>
        /// (an independent coexisting handle cannot take it).</summary>
        public static bool RecordLockedOnDisk(string path, int recNo) => ByteLocked(path, RecByte(recNo), 1);

        /// <summary>True when the whole-file FLOCK range is held on <paramref name="path"/>.</summary>
        public static bool FileLockedOnDisk(string path) => ByteLocked(path, 0x40000000L, 0x3FFFFFFEL);

        private static bool ByteLocked(string path, long pos, long len)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            try { fs.Lock(pos, len); fs.Unlock(pos, len); return false; }
            catch (IOException) { return true; }
        }

        public decimal DiskAmount(int rec0)
        {
            using var t = DbfTable.Open(DbfPath, new DbfOptions { LockMode = LockMode.Shared });
            var v = t.GetRecord(rec0)?["amount"];
            return v is null ? 0m : Convert.ToDecimal(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    private static bool Bool(VfpInterpreter it, string expr) => it.EvalExpression(expr).AsLogical;

    // ─────────────────────────── (6) in-process contention matrix ───────────────────────────

    [WindowsOnlyFact]
    public void RlockInA_BlocksRlockInB_ThenUnlockReleases()
    {
        using var s = new Shared();
        var a = s.NewClient(out var sa);
        var b = s.NewClient(out var sb);
        try
        {
            a.Execute("USE bt\nGO 3");
            Assert.True(Bool(a, "RLOCK()"));                     // A takes the record-3 lock.

            b.Execute("USE bt\nSET REPROCESS TO 1\nGO 3");
            Assert.False(Bool(b, "RLOCK()"));                    // B is denied while A holds it.
            Assert.True(Shared.RecordLockedOnDisk(s.DbfPath, 3));// really held at the VFP byte.

            a.Execute("UNLOCK");
            Assert.False(Shared.RecordLockedOnDisk(s.DbfPath, 3));
            Assert.True(Bool(b, "RLOCK()"));                     // B now succeeds.
            b.Execute("UNLOCK");
        }
        finally { sa.Dispose(); sb.Dispose(); }
    }

    [WindowsOnlyFact]
    public void RlockInA_BlocksReplaceInB_NoLostUpdate()
    {
        using var s = new Shared();
        var a = s.NewClient(out var sa);
        var b = s.NewClient(out var sb);
        try
        {
            a.Execute("USE bt\nGO 2");
            Assert.True(Bool(a, "RLOCK()"));

            b.Execute("USE bt\nGO 2");
            bool threw = false;
            try { b.Execute("REPLACE amount WITH 999.00"); } catch { threw = true; }
            Assert.True(threw);                                  // B's write on the locked record is refused.
            Assert.Equal(2.00m, s.DiskAmount(1));                // no lost update — record 2 unchanged.

            a.Execute("UNLOCK");
        }
        finally { sa.Dispose(); sb.Dispose(); }
    }

    [WindowsOnlyFact]
    public void FlockInA_BlocksRlockAndFlockInB()
    {
        using var s = new Shared();
        var a = s.NewClient(out var sa);
        var b = s.NewClient(out var sb);
        try
        {
            a.Execute("USE bt");
            Assert.True(Bool(a, "FLOCK()"));                     // A takes the whole-file lock.
            Assert.True(Shared.FileLockedOnDisk(s.DbfPath));

            b.Execute("USE bt\nSET REPROCESS TO 1\nGO 1");
            Assert.False(Bool(b, "RLOCK()"));                    // a record lock is denied under a foreign FLOCK.
            Assert.False(Bool(b, "FLOCK()"));                    // another FLOCK is denied too.

            a.Execute("UNLOCK");
            Assert.True(Bool(b, "RLOCK()"));                     // released ⇒ B succeeds.
            b.Execute("UNLOCK");
        }
        finally { sa.Dispose(); sb.Dispose(); }
    }

    [WindowsOnlyFact]
    public void FailedFlock_PreservesCallersExistingRecordLock()
    {
        using var s = new Shared(rows: 9);
        var a = s.NewClient(out var sa);
        var b = s.NewClient(out var sb);
        try
        {
            a.Execute("USE bt\nSET REPROCESS TO 1\nGO 5");
            Assert.True(Bool(a, "RLOCK()"));
            Assert.True(Bool(a, "ISRLOCKED(5)"));

            b.Execute("USE bt\nSET REPROCESS TO 1\nGO 9");
            Assert.True(Bool(b, "RLOCK()"));                  // foreign record lock forces A's FLOCK failure.

            Assert.False(Bool(a, "FLOCK()"));
            Assert.True(Bool(a, "ISRLOCKED(5)"));            // failed promotion must restore A's old lock.
            Assert.True(Shared.RecordLockedOnDisk(s.DbfPath, 5));

            b.Execute("GO 5");
            Assert.False(Bool(b, "RLOCK()"));                // A still owns rec5 at the OS layer.
        }
        finally { sa.Dispose(); sb.Dispose(); }
    }

    [WindowsOnlyFact]
    public void IsRlockedAndIsFlocked_ReportOwnHeldLocks()
    {
        using var s = new Shared();
        var a = s.NewClient(out var sa);
        try
        {
            a.Execute("USE bt\nGO 3");
            Assert.False(Bool(a, "ISRLOCKED(3)"));               // nothing held yet.
            Assert.False(Bool(a, "ISFLOCKED()"));

            Assert.True(Bool(a, "RLOCK()"));
            Assert.True(Bool(a, "ISRLOCKED(3)"));                // our own record lock is reported.
            Assert.False(Bool(a, "ISRLOCKED(4)"));              // a record we do NOT hold.
            Assert.False(Bool(a, "ISFLOCKED()"));

            a.Execute("UNLOCK");
            Assert.False(Bool(a, "ISRLOCKED(3)"));

            Assert.True(Bool(a, "FLOCK()"));
            Assert.True(Bool(a, "ISFLOCKED()"));
            Assert.True(Bool(a, "ISRLOCKED(3)"));                // a file lock covers every record.
            a.Execute("UNLOCK");
            Assert.False(Bool(a, "ISFLOCKED()"));
        }
        finally { sa.Dispose(); }
    }

    [WindowsOnlyFact]
    public void MultilocksOff_NewRlockReleasesPreviousRecordLock()
    {
        using var s = new Shared();
        var a = s.NewClient(out var sa);
        var b = s.NewClient(out var sb);
        try
        {
            a.Execute("USE bt");                                 // MULTILOCKS OFF (default).
            a.Execute("GO 1");
            Assert.True(Bool(a, "RLOCK()"));
            a.Execute("GO 2");
            Assert.True(Bool(a, "RLOCK()"));                     // taking record 2 releases record 1.

            Assert.False(Bool(a, "ISRLOCKED(1)"));               // record 1 was auto-released.
            Assert.True(Bool(a, "ISRLOCKED(2)"));

            b.Execute("USE bt\nSET REPROCESS TO 1");
            b.Execute("GO 1");
            Assert.True(Bool(b, "RLOCK()"));                     // B can take record 1 (A let it go).
            b.Execute("GO 2");
            Assert.False(Bool(b, "RLOCK()"));                    // record 2 is still A's.

            b.Execute("UNLOCK");
            a.Execute("UNLOCK");
        }
        finally { sa.Dispose(); sb.Dispose(); }
    }

    [WindowsOnlyFact]
    public void MultilocksOn_RecordLocksAccumulate_AndUnlockRecordReleasesOne()
    {
        using var s = new Shared();
        var a = s.NewClient(out var sa);
        try
        {
            a.Execute("USE bt\nSET MULTILOCKS ON");
            a.Execute("GO 1");
            Assert.True(Bool(a, "RLOCK()"));
            a.Execute("GO 2");
            Assert.True(Bool(a, "RLOCK()"));

            Assert.True(Bool(a, "ISRLOCKED(1)"));                // both held under MULTILOCKS ON.
            Assert.True(Bool(a, "ISRLOCKED(2)"));

            a.Execute("UNLOCK RECORD 1");
            Assert.False(Bool(a, "ISRLOCKED(1)"));               // only record 1 released.
            Assert.True(Bool(a, "ISRLOCKED(2)"));
            Assert.False(Shared.RecordLockedOnDisk(s.DbfPath, 1));
            Assert.True(Shared.RecordLockedOnDisk(s.DbfPath, 2));

            a.Execute("UNLOCK");                                 // release the rest.
            Assert.False(Bool(a, "ISRLOCKED(2)"));
        }
        finally { sa.Dispose(); }
    }

    [WindowsOnlyFact]
    public void Reprocess0WithOnErrorFailsFast_And2SecondsCountAlsoDenies()
    {
        using var s = new Shared();
        var a = s.NewClient(out var sa);
        var b = s.NewClient(out var sb);
        try
        {
            a.Execute("USE bt\nGO 4");
            Assert.True(Bool(a, "RLOCK()"));

            // SET REPROCESS TO 0 + an installed ON ERROR ⇒ the lock function fails FAST (immediate .F.).
            b.Execute("USE bt\nGO 4\nON ERROR lcErr = MESSAGE()\nSET REPROCESS TO 0");
            Assert.False(Bool(b, "RLOCK()"));

            // A bounded retry count (TO 2) still ends in .F. when the holder never releases.
            b.Execute("SET REPROCESS TO 2");
            Assert.False(Bool(b, "RLOCK()"));

            a.Execute("UNLOCK");
            Assert.True(Bool(b, "RLOCK()"));                     // released ⇒ granted.
            b.Execute("UNLOCK");
        }
        finally { sa.Dispose(); sb.Dispose(); }
    }

    [WindowsOnlyFact]
    public void DefaultReprocess_RetriesAreBounded_AndEventuallyDeny()
    {
        using var s = new Shared();
        var holder = s.NewClient(out var holderSession);
        var contender = s.NewClient(out var contenderSession);
        try
        {
            holder.Execute("USE bt\nGO 4");
            Assert.True(Bool(holder, "RLOCK()"));
            contender.Execute("USE bt\nGO 4");

            Assert.False(Bool(contender, "RLOCK()"));

            holder.Execute("UNLOCK");
            Assert.True(Bool(contender, "RLOCK()"));
            contender.Execute("UNLOCK");

            Assert.True(Bool(holder, "RLOCK()"));
            using var releaseStarted = new ManualResetEventSlim(false);
            Task release = Task.Run(() =>
            {
                releaseStarted.Wait();
                Thread.Sleep(25); // safely inside the 20 x 5ms AUTOMATIC retry window.
                holder.Execute("UNLOCK");
            });

            releaseStarted.Set();
            Assert.True(Bool(contender, "RLOCK()")); // this same call must observe a retry, not fail fast.
            release.GetAwaiter().GetResult();
            contender.Execute("UNLOCK");
        }
        finally { holderSession.Dispose(); contenderSession.Dispose(); }
    }

    [WindowsOnlyFact]
    public void LocksReleasedOnClose_And_OnSessionDispose()
    {
        using var s = new Shared();

        // (a) USE (close the area) releases the area's locks.
        {
            var a = s.NewClient(out var sa);
            var b = s.NewClient(out var sb);
            try
            {
                a.Execute("USE bt\nGO 3");
                Assert.True(Bool(a, "RLOCK()"));
                Assert.True(Shared.RecordLockedOnDisk(s.DbfPath, 3));

                a.Execute("USE");                               // close ⇒ VFP releases the lock.
                Assert.False(Shared.RecordLockedOnDisk(s.DbfPath, 3));
                b.Execute("USE bt\nGO 3");
                Assert.True(Bool(b, "RLOCK()"));
                b.Execute("UNLOCK");
            }
            finally { sa.Dispose(); sb.Dispose(); }
        }

        // (b) disposing the session releases every held byte-range lock (never leak past the session).
        {
            var a = s.NewClient(out var sa);
            a.Execute("USE bt\nGO 5");
            Assert.True(Bool(a, "RLOCK()"));
            Assert.True(Shared.RecordLockedOnDisk(s.DbfPath, 5));
            sa.Dispose();                                        // session dispose ⇒ cached writer disposed ⇒ lock gone.
            Assert.False(Shared.RecordLockedOnDisk(s.DbfPath, 5));
        }
    }

    // ─────────────────────────── (5) cached-writer interplay pins ───────────────────────────

    [WindowsOnlyFact]
    public void HeldRlock_SurvivesReplaceThroughCachedWriter_AndAPrune()
    {
        using var s = new Shared();
        // a second table so a USE of it triggers PruneCachedWriters while bt's area (and lock) stay open.
        string bt2 = Path.Combine(s.Dir, "bt2.dbf");
        using (var w = DbfWriter.Create(bt2, new[] { new DbfColumnDef("id", 'I') })) { w.AppendRecord(1); w.Flush(); }

        var a = s.NewClient(out var sa);
        var b = s.NewClient(out var sb);
        try
        {
            a.Execute("USE bt\nGO 3");
            Assert.True(Bool(a, "RLOCK()"));

            // A REPLACE on the SAME record goes through the SAME cached writer — its WithLock re-entry guard
            // sees our held lock and does not self-conflict; the write lands and the lock stays held.
            a.Execute("REPLACE amount WITH 500.00");
            Assert.Equal(500.00m, s.DiskAmount(2));              // the write persisted.
            Assert.True(Shared.RecordLockedOnDisk(s.DbfPath, 3));// lock survived the write.

            b.Execute("USE bt\nSET REPROCESS TO 1\nGO 3");
            Assert.False(Bool(b, "RLOCK()"));                    // B still denied.

            a.Execute("USE bt2 IN 0");                           // opens a 2nd area ⇒ PruneCachedWriters runs.
            Assert.True(Shared.RecordLockedOnDisk(s.DbfPath, 3));// the pinned writer kept the lock.
            Assert.False(Bool(b, "RLOCK()"));

            a.Execute("UNLOCK");                                 // only UNLOCK releases it.
            Assert.True(Bool(b, "RLOCK()"));
            b.Execute("UNLOCK");
        }
        finally { sa.Dispose(); sb.Dispose(); }
    }

    [WindowsOnlyFact]
    public void CowTransaction_RlockLandsOnLiveFile_NotThePrivateCopy()
    {
        using var s = new Shared();
        // Simulate an ADO.NET copy-on-write transaction: a private working copy of the table, with the
        // session's COW hooks wired so reads/writes ride the copy but a lock must coordinate on the LIVE file.
        string copy = Path.Combine(s.Dir, "bt.copy.dbf");
        File.Copy(s.DbfPath, copy, overwrite: true);
        string liveFull = Path.GetFullPath(s.DbfPath);
        string copyFull = Path.GetFullPath(copy);

        var it = s.NewClient(out var session);
        session.TxRedirectReadPath = p => string.Equals(Path.GetFullPath(p), liveFull, StringComparison.OrdinalIgnoreCase) ? copyFull : p;
        session.TxBeginWritePath = p => string.Equals(Path.GetFullPath(p), liveFull, StringComparison.OrdinalIgnoreCase) ? copyFull : p;
        session.TxLivePath = p => string.Equals(Path.GetFullPath(p), copyFull, StringComparison.OrdinalIgnoreCase) ? liveFull : p;
        try
        {
            it.Execute("USE bt\nGO 3");                          // the area rides the private copy (read redirect).
            Assert.True(Bool(it, "RLOCK()"));

            // Coordination happens on the LIVE file: the live record byte is locked, the copy's is free.
            Assert.True(Shared.RecordLockedOnDisk(liveFull, 3));
            Assert.False(Shared.RecordLockedOnDisk(copyFull, 3));

            it.Execute("UNLOCK");
            Assert.False(Shared.RecordLockedOnDisk(liveFull, 3));
        }
        finally { session.Dispose(); }
    }

    // ─────────────────────────── (2) atomic multi-record RLOCK list (must-fix #2) ───────────────────────────
    //
    // VFP9-ORACLE-VERIFIED (two independent VFP9 runtime processes handshaking on one shared table): under
    // SET MULTILOCKS ON, RLOCK("2,3,4") that hits a contended record 3 returns .F. and leaves NEITHER 2 NOR 4
    // locked — all-or-nothing — while a record the caller already owned BEFORE the call survives. The pre-fix
    // loop left the records it managed to grab (2) locked on a .F. return, silently pinning byte ranges the
    // SP believed it did not hold. These pin the atomic contract with in-process contention (session A ↔ B).

    [WindowsOnlyFact]
    public void MultilocksOn_ListLock_Failure_IsAtomic_LeavesNoNewLocks()
    {
        using var s = new Shared();
        var a = s.NewClient(out var sa);
        var b = s.NewClient(out var sb);
        try
        {
            a.Execute("USE bt\nGO 3");
            Assert.True(Bool(a, "RLOCK()"));                                 // A pins record 3.

            b.Execute("USE bt\nSET MULTILOCKS ON\nSET REPROCESS TO 1");
            Assert.False(Bool(b, "RLOCK(\"2,3,4\", \"bt\")"));               // the list fails on contended 3.
            Assert.False(Bool(b, "ISRLOCKED(2, \"bt\")"));                   // 2 rolled back (not left locked)…
            Assert.False(Bool(b, "ISRLOCKED(4, \"bt\")"));                   // …and 4 too — all-or-nothing.
            Assert.False(Shared.RecordLockedOnDisk(s.DbfPath, 2));           // really free on disk (no silent pin).
            Assert.False(Shared.RecordLockedOnDisk(s.DbfPath, 4));

            a.Execute("UNLOCK");
            Assert.True(Bool(b, "RLOCK(\"2,3,4\", \"bt\")"));                // released ⇒ the whole list locks.
            Assert.True(Bool(b, "ISRLOCKED(2, \"bt\")"));
            Assert.True(Bool(b, "ISRLOCKED(4, \"bt\")"));
            b.Execute("UNLOCK");
        }
        finally { sa.Dispose(); sb.Dispose(); }
    }

    [WindowsOnlyFact]
    public void MultilocksOn_ListLock_Failure_PreOwnedRecordSurvives()
    {
        using var s = new Shared();
        var a = s.NewClient(out var sa);
        var b = s.NewClient(out var sb);
        try
        {
            a.Execute("USE bt\nGO 3");
            Assert.True(Bool(a, "RLOCK()"));                                 // A pins record 3.

            b.Execute("USE bt\nSET MULTILOCKS ON\nSET REPROCESS TO 1");
            Assert.True(Bool(b, "RLOCK(\"2\", \"bt\")"));                    // B pre-owns record 2.
            Assert.False(Bool(b, "RLOCK(\"2,3,4\", \"bt\")"));              // the list fails on contended 3…
            Assert.True(Bool(b, "ISRLOCKED(2, \"bt\")"));                    // …but the PRE-OWNED 2 survives (oracle)…
            Assert.False(Bool(b, "ISRLOCKED(4, \"bt\")"));                   // …while newly-attempted 4 is rolled back.
            Assert.True(Shared.RecordLockedOnDisk(s.DbfPath, 2));
            Assert.False(Shared.RecordLockedOnDisk(s.DbfPath, 4));
            b.Execute("UNLOCK");
            a.Execute("UNLOCK");
        }
        finally { sa.Dispose(); sb.Dispose(); }
    }

    // ─────────────────────────── (4) MULTILOCKS OFF list == VFP9 (must-fix #4, oracle-corrected) ───────────
    //
    // The 2026-07 review proposed making a MULTILOCKS-OFF record LIST return .F. / raise an error. The VFP9
    // oracle REFUTES that: real VFP9 processes RLOCK("2,3,4") under SET MULTILOCKS OFF record-by-record (each
    // new lock releasing the previous one) and returns .T. holding ONLY the LAST record — the exact behaviour
    // the interpreter already produces. Forcing .F. would DIVERGE from VFP. This pins the verified VFP9 shape.

    [WindowsOnlyFact]
    public void MultilocksOff_ListLock_ReturnsTrue_HoldingOnlyLastRecord_MatchesVfp9Oracle()
    {
        using var s = new Shared();
        var a = s.NewClient(out var sa);
        try
        {
            a.Execute("USE bt");                                            // MULTILOCKS OFF (default).
            Assert.True(Bool(a, "RLOCK(\"2,3,4\", \"bt\")"));               // VFP9: .T. …
            Assert.False(Bool(a, "ISRLOCKED(2, \"bt\")"));                  // …releasing 2…
            Assert.False(Bool(a, "ISRLOCKED(3, \"bt\")"));                  // …and 3…
            Assert.True(Bool(a, "ISRLOCKED(4, \"bt\")"));                   // …holding only the LAST record.
            Assert.False(Shared.RecordLockedOnDisk(s.DbfPath, 2));
            Assert.True(Shared.RecordLockedOnDisk(s.DbfPath, 4));
            a.Execute("UNLOCK");
        }
        finally { sa.Dispose(); }
    }

    // ─────────────────────────── (1) rollback while a lock is held actually reverts (must-fix #1) ───────────
    //
    // A held RLOCK pins the 5.5 cached writer. Pre-fix, RestoreSnapshot's InvalidateCachedWriter SKIPPED the
    // pinned writer, so File.WriteAllBytes hit a sharing violation the surrounding catch{} swallowed — the
    // ROLLBACK silently no-op'd, leaving the written bytes on disk under a raised error (an RI/CANDIDATE-abort
    // bypass). The fix force-closes the writer, reverts, then re-acquires the tracked lock.

    [WindowsOnlyFact]
    public void RollbackWhileLockHeld_ActuallyRevertsFile_AndLockSurvives()
    {
        using var s = new Shared();
        var a = s.NewClient(out var sa);
        try
        {
            a.Execute("USE bt\nGO 3");
            Assert.True(Bool(a, "RLOCK()"));                                 // hold record 3 (pins the cached writer).
            Assert.True(Shared.RecordLockedOnDisk(s.DbfPath, 3));

            a.Execute("BEGIN TRANSACTION");
            a.Execute("GO 2\nREPLACE amount WITH 777.00");                   // writes record 2 to the live file.
            Assert.Equal(777.00m, s.DiskAmount(1));                         // the write really landed.

            a.Execute("ROLLBACK");                                          // must ACTUALLY revert the file…
            Assert.Equal(2.00m, s.DiskAmount(1));                          // …record 2 back to its pre-image (not 777).
            Assert.True(Bool(a, "ISRLOCKED(3)"));                          // the lock survived the rollback…
            Assert.True(Shared.RecordLockedOnDisk(s.DbfPath, 3));          // …and is really re-held on disk.
            a.Execute("UNLOCK");
        }
        finally { sa.Dispose(); }
    }

    // ─────────────────────────── (3) RLOCK inside a real ADO.NET COW transaction (must-fix #3) ──────────────
    //
    // A stored proc that RLOCKs inside a FoxDbfConnection copy-on-write transaction takes the byte-range lock
    // on the LIVE file (coordination happens there) via a cached writer. Pre-fix that open live handle + its
    // lock made the commit's File.Replace/File.Copy writeback throw, so Commit() deterministically failed. The
    // fix releases the interpreter's held locks + cached writers at the transaction's quiesce point (before
    // writeback) — a DEFINED disposition: the lock is released at the transaction boundary and the write commits.

    [WindowsOnlyFact]
    public void Rlock_InsideAdoCowTransaction_CommitSucceeds_LockReleasedAtBoundary()
    {
        using var s = new Shared();
        using var conn = new FoxDbfConnection($"Data Source={s.Dir}");
        conn.Open();
        var tx = conn.BeginTransaction();
        try
        {
            var it = conn.Interpreter;                                       // the connection's session-bound interpreter.
            it.Execute("USE bt\nGO 3");
            Assert.True(it.EvalExpression("RLOCK()").AsLogical);            // lock record 3 on the LIVE file.
            Assert.True(Shared.RecordLockedOnDisk(s.DbfPath, 3));           // really held on the live .dbf.

            it.Execute("REPLACE amount WITH 321.00");                       // first write ⇒ private COW copy.
            tx.Commit();                                                    // MUST succeed (was: live lock handle blocked writeback).

            Assert.Equal(321.00m, s.DiskAmount(2));                        // the write committed to the live file.
            Assert.False(Shared.RecordLockedOnDisk(s.DbfPath, 3));         // lock released at the transaction boundary.
        }
        finally { try { tx.Dispose(); } catch { } conn.Close(); }
    }
}
