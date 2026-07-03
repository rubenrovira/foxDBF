using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;

namespace CrossVault.FoxDbf.Data;

/// <summary>
/// A REAL, COPY-ON-WRITE transaction over a FoxDbf connection providing ATOMICITY, ROLLBACK,
/// ISOLATION and OPTIMISTIC CONCURRENCY.
/// <para>
/// DBF/VFP has no write-ahead / rollback log, so isolation is provided by PRIVATE PER-TABLE WORKING
/// COPIES. The FIRST time the transaction WRITES to a table (DML — INSERT/UPDATE/DELETE), that table's
/// files (<c>.dbf</c> + companion <c>.fpt</c>/<c>.dbt</c> memo + <c>.cdx</c> index, when present) are
/// copied to a private temp location and the live file's CHANGE-TOKEN (record count + last-update stamp
/// + length + mtime) is recorded. For the rest of the transaction THIS connection's reads AND writes to
/// that table are REDIRECTED to the private copy (read-your-writes), while OTHER connections keep reading
/// the untouched LIVE file (ISOLATION — honest <see cref="System.Data.IsolationLevel.ReadCommitted"/>).
/// </para>
/// <para>
/// <see cref="Commit"/> verifies each touched table's live change-token still matches what was recorded
/// at copy time; a mismatch means a FOREIGN writer mutated the live file, so the commit FAILS with a
/// <see cref="FoxDbfTransactionConflictException"/> (no lost update) and the transaction stays
/// rollback-able. Otherwise it closes every open handle and atomically swaps each private copy over its
/// live file. <see cref="Rollback"/> (and dispose-without-commit) simply DISCARDS the private copies —
/// the live files were never touched.
/// </para>
/// <para>
/// DDL (CREATE / ALTER / DROP) cannot be redirected to a private copy without breaking its in-transaction
/// visibility (a DROP must make the live file disappear to the transacting connection, a CREATE must make
/// it appear), so DDL operates on the LIVE files under a SNAPSHOT-AND-RESTORE scheme: the live files are
/// snapshotted before the irreversible op so a <see cref="Rollback"/> restores them byte-for-byte (or, for
/// CREATE, the to-be-created table is recorded so a rollback deletes it); a <see cref="Commit"/> keeps the
/// live change.
/// </para>
/// </summary>
public sealed class FoxDbfTransaction : DbTransaction
{
    private readonly FoxDbfConnection _connection;
    private readonly IsolationLevel _isolationLevel;

    // Per-table PRIVATE WORKING COPIES taken lazily on first DML write: live .dbf path -> the copy.
    private readonly Dictionary<string, WorkingCopy> _copies =
        new(StringComparer.OrdinalIgnoreCase);

    // Per-table DDL SNAPSHOTS taken lazily before CREATE/ALTER/DROP: live .dbf path -> the snapshot.
    private readonly Dictionary<string, TableSnapshot> _ddlSnapshots =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _completed;
    private bool _disposed;

    internal FoxDbfTransaction(FoxDbfConnection connection, IsolationLevel isolationLevel)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _isolationLevel = isolationLevel == IsolationLevel.Unspecified
            ? IsolationLevel.ReadCommitted
            : isolationLevel;
    }

    protected override DbConnection? DbConnection => _connection;

    public override IsolationLevel IsolationLevel => _isolationLevel;

    /// <summary>True once this transaction has been committed, rolled back, or disposed — a second
    /// Commit/Rollback throws and the connection treats it as no longer active.</summary>
    internal bool IsCompleted => _completed || _disposed;

    // ---- session hooks (installed by FoxDbfConnection.BeginDbTransaction) ------------------

    /// <summary>READ redirect: given a resolved live <c>.dbf</c> path, return the private working copy
    /// to read when this table has already been written in the transaction (read-your-writes), else the
    /// live path unchanged. A table only ever READ (never written) is read directly from the live file.</summary>
    internal string RedirectReadPath(string liveDbfPath)
    {
        string full = Path.GetFullPath(liveDbfPath);
        return _copies.TryGetValue(full, out var wc) ? wc.CopyDbfPath : liveDbfPath;
    }

    /// <summary>CANONICALIZE a resolved <c>.dbf</c> path back to the table's LIVE path: given one of this
    /// transaction's PRIVATE working-copy paths return the live file it copies; given anything else (a live
    /// path, or an untracked table) return it unchanged. Lets a caller holding a work area that is already
    /// riding a private copy (its <see cref="System.IO.Path"/>-form <c>SourcePath</c> is the copy) recover the
    /// live identity so it can re-resolve the DBC member (long field names) and re-apply the read redirect.</summary>
    internal string LivePathOf(string dbfPath)
    {
        string full = Path.GetFullPath(dbfPath);
        foreach (var wc in _copies.Values)
            if (string.Equals(Path.GetFullPath(wc.CopyDbfPath), full, StringComparison.OrdinalIgnoreCase))
                return wc.LiveDbfPath;
        return dbfPath;
    }

    /// <summary>WRITE redirect: ensure the table backing <paramref name="liveDbfPath"/> has a private
    /// working copy (created lazily, once per table, with the live change-token recorded) and return the
    /// COPY's <c>.dbf</c> path the writer should open — so the live file is never touched. A table the
    /// transaction is mutating via DDL (CREATE/ALTER/DROP, which operate on the live files) is written in
    /// place: its live path is returned unchanged.</summary>
    internal string BeginWritePath(string liveDbfPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) throw new InvalidOperationException("Transaction has already been committed or rolled back.");
        ArgumentException.ThrowIfNullOrEmpty(liveDbfPath);

        string full = Path.GetFullPath(liveDbfPath);

        // Already copied — reuse the same private copy (idempotent per table).
        if (_copies.TryGetValue(full, out var existing)) return existing.CopyDbfPath;

        // IDEMPOTENT when handed a path that IS ALREADY one of our private copies: the microVFP interpreter
        // resolves a direct write's path from its (possibly already-redirected) open work area, so on the
        // SECOND+ write to a table it passes the copy path back in — return it unchanged rather than taking
        // a copy-of-a-copy. (The raw DML path never hits this: it re-resolves the LIVE path from the table
        // NAME on every write, so it always passes a live path.)
        foreach (var owned in _copies.Values)
            if (string.Equals(Path.GetFullPath(owned.CopyDbfPath), full, StringComparison.OrdinalIgnoreCase))
                return owned.CopyDbfPath;

        // A table being created/altered/dropped via DDL is operated on the LIVE files; write in place.
        if (_ddlSnapshots.ContainsKey(full)) return liveDbfPath;

        var wc = WorkingCopy.Create(full);
        _copies[full] = wc;
        return wc.CopyDbfPath;
    }

    /// <summary>DDL SNAPSHOT-on-live: capture the live <c>.dbf</c> + sidecars before a CREATE/ALTER/DROP
    /// rewrites or removes them (lazy, once per table). For a not-yet-existing CREATE target nothing is
    /// copied but the table is recorded so a Rollback deletes the newly created files.</summary>
    internal void DdlSnapshot(string liveDbfPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) throw new InvalidOperationException("Transaction has already been committed or rolled back.");
        ArgumentException.ThrowIfNullOrEmpty(liveDbfPath);

        string full = Path.GetFullPath(liveDbfPath);
        // Once per table — but take the DDL snapshot EVEN WHEN a DML private copy already exists for this
        // table. The DDL executors (ALTER rewrite / DROP delete) operate on the LIVE file regardless of any
        // DML copy, so without a DDL snapshot a DML-then-DDL sequence would leave the live file permanently
        // in its DDL-modified state after a Rollback (the DML copy is discarded but nothing restores the
        // live DDL change). The DDL snapshot captures the live file's pre-DDL bytes so Rollback restores it.
        if (_ddlSnapshots.ContainsKey(full)) return;

        string snapDir = Path.Combine(
            Path.GetTempPath(), "foxdbf_txddl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(snapDir);

        try
        {
            foreach (string live in CompanionFiles(full))
            {
                if (File.Exists(live))
                    File.Copy(live, Path.Combine(snapDir, Path.GetFileName(live)), overwrite: true);
            }
        }
        catch
        {
            try { Directory.Delete(snapDir, recursive: true); } catch { /* best-effort */ }
            throw;
        }

        _ddlSnapshots[full] = new TableSnapshot { LiveDbfPath = full, SnapshotDir = snapDir };
    }

    /// <summary>The candidate file set for one table: the .dbf and its memo / index sidecars. Only the
    /// ones that actually exist on disk are copied / snapshotted / swapped.</summary>
    private static IEnumerable<string> CompanionFiles(string dbfPath)
    {
        yield return dbfPath;
        yield return Path.ChangeExtension(dbfPath, ".fpt"); // VFP memo
        yield return Path.ChangeExtension(dbfPath, ".dbt"); // dBase memo
        yield return Path.ChangeExtension(dbfPath, ".cdx"); // structural compound index
    }

    // ---- commit / rollback ----------------------------------------------------------------

    public override void Commit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) throw new InvalidOperationException("Transaction has already been committed or rolled back.");

        // OPTIMISTIC CONCURRENCY: re-verify every touched table's live change-token BEFORE marking the
        // transaction complete or touching any file. A mismatch (a foreign writer changed / removed the
        // live file after the copy was taken) fails the commit WITHOUT finalizing the transaction, so the
        // caller can still Rollback cleanly — and no foreign change is lost.
        foreach (var wc in _copies.Values)
        {
            // A table that was ALSO touched by DDL (DML copy first, then ALTER/DROP on the LIVE file) is
            // owned by its live post-DDL state, not by the stale DML copy — its copy is neither token-checked
            // nor swapped below (see the swap loop), so skip the optimistic check to avoid a SPURIOUS conflict
            // (the same-transaction DDL changed the live token after the copy recorded the pre-DDL one).
            if (_ddlSnapshots.ContainsKey(wc.LiveDbfPath)) continue;
            if (!ChangeToken.Read(wc.LiveDbfPath).Equals(wc.Token))
                throw new FoxDbfTransactionConflictException(
                    $"Commit conflict: table '{Path.GetFileName(wc.LiveDbfPath)}' was changed on disk by " +
                    "another writer after the transaction took its private copy; committing would lose that " +
                    "change. The transaction was not committed and can be rolled back.");
        }

        _completed = true;

        // No conflict — persist. Close every open handle first so the live files are unlocked for the
        // swap, then atomically replace each live file with its private copy. DDL changes are already on
        // the live files, so commit just drops their snapshots. Cleanup runs in a finally so the
        // connection always returns to autocommit and no temp dir leaks even on an I/O hiccup.
        try
        {
            if (_copies.Count > 0)
            {
                _connection.QuiesceForRollback(); // release handles on both live and copy files.
                foreach (var wc in _copies.Values)
                {
                    // Skip a table whose live file was also rewritten/removed by same-transaction DDL: its
                    // live post-DDL state is authoritative and is KEPT on commit (the class contract — "a
                    // Commit keeps the live change"). Swapping the pre-DDL DML copy back in would resurrect a
                    // DROPped table or revert an ALTER's schema, so the copy is just discarded afterwards.
                    if (_ddlSnapshots.ContainsKey(wc.LiveDbfPath)) continue;
                    wc.SwapIntoLive();
                }
            }
        }
        finally
        {
            DiscardCopies();
            DiscardDdlSnapshots();
            _connection.ClearTransaction(this);
        }
    }

    public override void Rollback()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed) throw new InvalidOperationException("Transaction has already been committed or rolled back.");
        _completed = true;

        // The private working copies were written instead of the live files, so a rollback just discards
        // them — the live files were never touched. Only DDL (which operated on the live files) needs the
        // live state restored. Close handles first (so copy/live files are released and re-open lazily
        // against the live file once the redirect is cleared), then restore the DDL snapshots. Always
        // clear the transaction + drop the copies/snapshots in a finally so a throw mid-restore cannot
        // leave the session's redirect hooks wired to this now-completed transaction nor orphan temp dirs.
        try
        {
            if (_copies.Count > 0 || _ddlSnapshots.Count > 0)
                _connection.QuiesceForRollback();
            RestoreDdlSnapshots();
        }
        finally
        {
            DiscardCopies();
            DiscardDdlSnapshots();
            _connection.ClearTransaction(this);
        }
    }

    /// <summary>Return every DDL-touched table to its EXACT pre-transaction on-disk shape: each file that
    /// existed at snapshot time is copied back over the live one, and each companion that did NOT exist at
    /// snapshot time but exists now (a CREATE'd table, or a sidecar a write added) is DELETED.</summary>
    private void RestoreDdlSnapshots()
    {
        foreach (var snap in _ddlSnapshots.Values)
        {
            string? liveDir = Path.GetDirectoryName(snap.LiveDbfPath);
            if (liveDir is null) continue;

            var restored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string saved in Directory.GetFiles(snap.SnapshotDir))
            {
                string fileName = Path.GetFileName(saved);
                File.Copy(saved, Path.Combine(liveDir, fileName), overwrite: true);
                restored.Add(fileName);
            }

            foreach (string companion in CompanionFiles(snap.LiveDbfPath))
            {
                if (!restored.Contains(Path.GetFileName(companion)) && File.Exists(companion))
                    File.Delete(companion);
            }
        }
    }

    private void DiscardCopies()
    {
        foreach (var wc in _copies.Values) wc.Dispose();
        _copies.Clear();
    }

    private void DiscardDdlSnapshots()
    {
        foreach (var snap in _ddlSnapshots.Values) snap.Dispose();
        _ddlSnapshots.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            base.Dispose(disposing);
            return;
        }

        if (disposing && !_completed)
        {
            try { Rollback(); }
            catch { /* dispose must not throw */ }
        }

        _disposed = true;
        if (disposing)
        {
            DiscardCopies();
            DiscardDdlSnapshots();
        }
        base.Dispose(disposing);
    }

    // ---- a table's change-token (optimistic-concurrency stamp) ----------------------------

    /// <summary>A cheap fingerprint of a table used to detect a FOREIGN write between the time the
    /// transaction took its private copy and the time it commits: the <c>.dbf</c> header record count +
    /// last-update stamp plus the <c>.dbf</c> length and last-write time, AND a signature of every present
    /// companion (<c>.fpt</c>/<c>.dbt</c> memo + <c>.cdx</c> index) — each sidecar's existence + length +
    /// mtime. Any append / update-in-place / pack / truncate changes at least one <c>.dbf</c> component; a
    /// sidecar-only foreign change (e.g. a REINDEX that rebuilds the <c>.cdx</c> without touching the
    /// <c>.dbf</c>, which the private copy would otherwise overwrite — a lost update) changes the sidecar
    /// signature. A MISSING <c>.dbf</c> (foreign DROP) is a distinct token.</summary>
    private readonly record struct ChangeToken(
        bool Exists, uint RecordCount, int HeaderStamp, long Length, long MTimeTicks, long SidecarSignature)
    {
        public static ChangeToken Read(string dbfPath)
        {
            try
            {
                long sidecars = SidecarSignatureOf(dbfPath);

                var fi = new FileInfo(dbfPath);
                if (!fi.Exists) return new ChangeToken(false, 0, 0, 0, 0, sidecars);

                Span<byte> head = stackalloc byte[8];
                int read;
                using (var fs = new FileStream(dbfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    read = fs.Read(head);
                }

                // DBF header: byte 0 = version, bytes 1..3 = last-update YY/MM/DD, bytes 4..7 = record count (LE).
                int stamp = read >= 4 ? (head[1] << 16) | (head[2] << 8) | head[3] : 0;
                uint recCount = read >= 8
                    ? (uint)(head[4] | (head[5] << 8) | (head[6] << 16) | (head[7] << 24))
                    : 0;
                return new ChangeToken(true, recCount, stamp, fi.Length, fi.LastWriteTimeUtc.Ticks, sidecars);
            }
            catch
            {
                // If the token cannot be read, treat it as a distinct value so commit fails safe (conflict)
                // rather than silently overwriting a file we could not verify.
                return new ChangeToken(false, 0, 0, -1, -1, -1);
            }
        }

        /// <summary>Fold each present companion side file (<c>.fpt</c>/<c>.dbt</c> memo + <c>.cdx</c> index)
        /// into one signature from its existence + length + mtime, so a foreign change that mutates ONLY a
        /// sidecar (leaving the <c>.dbf</c> bytes and mtime untouched) is still detected as a commit conflict.</summary>
        private static long SidecarSignatureOf(string dbfPath)
        {
            long sig = 17;
            foreach (string ext in (ReadOnlySpan<string>)[".fpt", ".dbt", ".cdx"])
            {
                var fi = new FileInfo(Path.ChangeExtension(dbfPath, ext));
                if (fi.Exists)
                {
                    sig = sig * 31 + 2;                       // present marker (distinct from absent).
                    sig = sig * 31 + fi.Length;
                    sig = sig * 31 + fi.LastWriteTimeUtc.Ticks;
                }
                else
                {
                    sig = sig * 31 + 1;                       // absent marker.
                }
            }
            return sig;
        }
    }

    // ---- one table's private working copy (copy-on-write) ---------------------------------

    /// <summary>A private temp copy of one table's files (<c>.dbf</c> + sidecars) plus the live file's
    /// change-token captured when the copy was taken. The transaction redirects this connection's reads
    /// and writes here; <see cref="SwapIntoLive"/> promotes it on commit; <see cref="Dispose"/> drops it on
    /// rollback / commit-cleanup.</summary>
    private sealed class WorkingCopy : IDisposable
    {
        public required string LiveDbfPath { get; init; }
        public required string CopyDir { get; init; }
        public required string CopyDbfPath { get; init; }
        public required ChangeToken Token { get; init; }

        public static WorkingCopy Create(string liveDbfPath)
        {
            // Record the live change-token first, then copy the files — the copy is an exact image of the
            // live table at this instant, so live-token == copy-content until a foreign writer intervenes.
            var token = ChangeToken.Read(liveDbfPath);

            string copyDir = Path.Combine(
                Path.GetTempPath(), "foxdbf_txcow_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(copyDir);

            try
            {
                foreach (string live in CompanionFiles(liveDbfPath))
                {
                    if (File.Exists(live))
                        File.Copy(live, Path.Combine(copyDir, Path.GetFileName(live)), overwrite: true);
                }
            }
            catch
            {
                try { Directory.Delete(copyDir, recursive: true); } catch { /* best-effort */ }
                throw;
            }

            return new WorkingCopy
            {
                LiveDbfPath = liveDbfPath,
                CopyDir = copyDir,
                CopyDbfPath = Path.Combine(copyDir, Path.GetFileName(liveDbfPath)),
                Token = token,
            };
        }

        /// <summary>Atomically promote the private copy over the live files: each companion present in the
        /// copy replaces the live one; each companion the copy no longer has (a sidecar removed during the
        /// transaction) is deleted from the live location. Callers must close all handles first.</summary>
        public void SwapIntoLive()
        {
            string? liveDir = Path.GetDirectoryName(LiveDbfPath);
            if (liveDir is null) return;

            // Promote in a DETERMINISTIC SAFE ORDER instead of the arbitrary Directory.GetFiles order:
            // the memo files (.fpt/.dbt) FIRST, the .dbf LAST. VFP memo blocks are append-only during DML,
            // so the new .fpt's old block offsets stay valid — meaning the table is still readable through
            // the OLD .dbf if a later promotion step fails. Promoting the .dbf last guarantees the live .dbf
            // never references memo offsets that are not yet present in the live .fpt (within-table partial
            // promotion would otherwise corrupt every memo read). The .cdx is not referenced by the .dbf
            // header, so its order is immaterial; it sits in the middle.
            var promoted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string copyFile in Directory.GetFiles(CopyDir).OrderBy(PromotionOrder))
            {
                string fileName = Path.GetFileName(copyFile);
                string liveFile = Path.Combine(liveDir, fileName);
                PromoteFile(copyFile, liveFile);
                promoted.Add(fileName);
            }

            foreach (string companion in CompanionFiles(LiveDbfPath))
            {
                if (!promoted.Contains(Path.GetFileName(companion)) && File.Exists(companion))
                    File.Delete(companion);
            }
        }

        /// <summary>Safe within-table promotion order: memo (.fpt/.dbt) first (0), the table header (.dbf)
        /// last (2), everything else (.cdx, …) in the middle (1) — so the live .dbf is never published ahead
        /// of the memo blocks it points at.</summary>
        private static int PromotionOrder(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".fpt" or ".dbt" => 0, // memo first: append-only offsets stay valid for the old .dbf.
                ".dbf" => 2,           // table header LAST: references memo block offsets.
                _ => 1,                // .cdx and any other sidecar: not referenced by the .dbf header.
            };
        }

        /// <summary>Replace the live file with the copy as atomically as the platform allows: an in-place
        /// <see cref="File.Replace(string, string, string?)"/> when both live on the same volume (atomic),
        /// falling back to a copy-overwrite when the temp copy is on a different volume (File.Replace /
        /// File.Move across volumes throw). Either way the live file ends up with the copy's bytes.</summary>
        private static void PromoteFile(string copyFile, string liveFile)
        {
            if (File.Exists(liveFile))
            {
                try
                {
                    File.Replace(copyFile, liveFile, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    return;
                }
                catch (IOException) { /* cross-volume / replace unsupported — fall back to copy. */ }
                File.Copy(copyFile, liveFile, overwrite: true);
            }
            else
            {
                try { File.Move(copyFile, liveFile); return; }
                catch (IOException) { /* cross-volume — fall back to copy. */ }
                File.Copy(copyFile, liveFile, overwrite: true);
            }
        }

        public void Dispose()
        {
            try { if (Directory.Exists(CopyDir)) Directory.Delete(CopyDir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }

    /// <summary>One DDL-touched table's snapshotted files (a private temp copy of .dbf + sidecars),
    /// restored over the live file on rollback and dropped on commit / rollback / dispose.</summary>
    private sealed class TableSnapshot : IDisposable
    {
        public required string LiveDbfPath { get; init; }
        public required string SnapshotDir { get; init; }

        public void Dispose()
        {
            try { if (Directory.Exists(SnapshotDir)) Directory.Delete(SnapshotDir, recursive: true); }
            catch { /* best-effort temp cleanup */ }
        }
    }
}
