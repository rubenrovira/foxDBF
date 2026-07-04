using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests.Data;

/// <summary>
/// ROADMAP v1.2 — CHARACTERIZE + PIN the ADO.NET copy-on-write transaction's isolation boundary. These are
/// mostly PINS of behaviour that already holds (marked below); the (d) probe DOCUMENTS the deliberately-open
/// boundary of the honest <see cref="IsolationLevel.ReadCommitted"/> contract.
/// <list type="bullet">
///   <item>(a) read-your-own-writes inside the transaction — PIN.</item>
///   <item>(b) a second connection does NOT see uncommitted writes — PIN.</item>
///   <item>(c) rollback leaves ZERO trace, including the <c>.cdx</c> (with 5.1 index maintenance in play) — PIN.</item>
///   <item>(d) REPEATABLE READ probe: a FOREIGN process writes the live file mid-transaction. For a table the
///   transaction has NOT yet privately copied, the read IS non-repeatable (the foreign committed row becomes
///   visible) — the documented ReadCommitted boundary. For a table the transaction HAS written (already has a
///   private copy) the read is stable (isolated from the foreign change).</item>
/// </list>
/// SAFETY: throwaway temp table only.
/// </summary>
public sealed class TransactionIsolationAdoV12Tests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbf;
    private readonly string _cdx;

    public TransactionIsolationAdoV12Tests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "foxdbf_v12iso_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbf = Path.Combine(_dir, "emp.dbf");
        _cdx = Path.Combine(_dir, "emp.cdx");
        using var w = DbfWriter.Create(_dbf, new[]
        {
            new DbfColumnDef("ID", 'I', 4),
            new DbfColumnDef("NAME", 'C', 20),
            new DbfColumnDef("AMOUNT", 'N', 10, 2),
        }, new DbfCreateOptions { Overwrite = true });
        w.AppendRecord(1, "Alice", 100.00m);
        w.AppendRecord(2, "Bob", 200.00m);
        w.AppendRecord(3, "Carol", 300.00m);
        w.CreateTag(new CdxTagDefinition("TID", "ID"));
        w.CreateTag(new CdxTagDefinition("TNAME", "NAME"));
        w.Flush();
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort */ }
    }

    private FoxDbfConnection Open()
    {
        var c = new FoxDbfConnection($"Data Source={_dir}");
        c.Open();
        return c;
    }

    private static int ExecNonQuery(FoxDbfConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    private static object? ExecScalar(FoxDbfConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    /// <summary>A foreign writer (a SEPARATE raw handle — NOT the transacting connection) appends a row to
    /// the LIVE table, simulating another process mutating the file mid-transaction.</summary>
    private void ForeignAppend(int id, string name)
    {
        using var w = DbfWriter.Open(_dbf);
        w.AppendRecord(id, name, 700.00m);
        w.Flush();
    }

    // ---- (a) read-your-own-writes — PIN ------------------------------------------------------

    [Fact]
    public void A_ReadYourOwnWrites_Inside_Transaction()
    {
        using var conn = Open();
        var tx = conn.BeginTransaction();
        ExecNonQuery(conn, "INSERT INTO emp (id, name, amount) VALUES (4, 'Dave', 400.00)");
        ExecNonQuery(conn, "UPDATE emp SET amount = 111.11 WHERE id = 1");

        Assert.Equal("Dave", ((string)ExecScalar(conn, "SELECT name FROM emp WHERE id = 4")!).TrimEnd());
        Assert.Equal(111.11m, Convert.ToDecimal(ExecScalar(conn, "SELECT amount FROM emp WHERE id = 1")));
        tx.Rollback();
    }

    // ---- (b) another connection does NOT see uncommitted writes — PIN ------------------------

    [Fact]
    public void B_OtherConnection_DoesNotSee_Uncommitted_Writes()
    {
        using var connA = Open();
        using var connB = Open();

        var tx = connA.BeginTransaction();
        ExecNonQuery(connA, "INSERT INTO emp (id, name, amount) VALUES (4, 'Dave', 400.00)");

        Assert.Equal(3, Convert.ToInt32(ExecScalar(connB, "SELECT COUNT(*) FROM emp")));
        Assert.Null(ExecScalar(connB, "SELECT name FROM emp WHERE id = 4"));

        tx.Commit();
        Assert.Equal(4, Convert.ToInt32(ExecScalar(connB, "SELECT COUNT(*) FROM emp")));
    }

    // ---- (c) rollback leaves zero trace INCL. cdx — PIN --------------------------------------

    [Fact]
    public void C_Rollback_LeavesZeroTrace_Including_Cdx()
    {
        byte[] cdxBefore = File.ReadAllBytes(_cdx);

        using (var conn = Open())
        {
            var tx = conn.BeginTransaction();
            // Several appends + an update — each maintains the structural CDX (5.1) on the private copy.
            ExecNonQuery(conn, "INSERT INTO emp (id, name, amount) VALUES (4, 'Dave', 400.00)");
            ExecNonQuery(conn, "INSERT INTO emp (id, name, amount) VALUES (5, 'Zoe', 500.00)");
            ExecNonQuery(conn, "UPDATE emp SET name = 'Alicia' WHERE id = 1");

            // read-your-writes so the writes definitely happened (the post-rollback check is meaningful).
            Assert.Equal(5, Convert.ToInt32(ExecScalar(conn, "SELECT COUNT(*) FROM emp")));

            tx.Rollback();
        }

        // Zero trace: 3 rows again, the inserted keys gone, and the .cdx byte-for-byte the pre-tx file.
        using (var conn = Open())
        {
            Assert.Equal(3, Convert.ToInt32(ExecScalar(conn, "SELECT COUNT(*) FROM emp")));
            Assert.Null(ExecScalar(conn, "SELECT id FROM emp WHERE id = 4"));
            Assert.Null(ExecScalar(conn, "SELECT id FROM emp WHERE id = 5"));
            Assert.Equal("Alice", ((string)ExecScalar(conn, "SELECT name FROM emp WHERE id = 1")!).TrimEnd());
        }
        Assert.Equal(cdxBefore, File.ReadAllBytes(_cdx));
    }

    // ---- (d) REPEATABLE READ probe — the documented ReadCommitted boundary -------------------

    /// <summary>A table the transaction has NOT yet privately copied is read straight from the LIVE file, so a
    /// FOREIGN committed write mid-transaction IS visible to a later read in the same transaction (a
    /// non-repeatable read). This is the honest ReadCommitted boundary — pinned as KNOWN behaviour.</summary>
    [Fact]
    public void D_RepeatableReadProbe_NotYetCopiedTable_SeesForeignCommittedWrite()
    {
        using var connA = Open();
        var tx = connA.BeginTransaction();

        // First read inside the tx (emp is only READ so far — no private copy taken).
        Assert.Equal(3, Convert.ToInt32(ExecScalar(connA, "SELECT COUNT(*) FROM emp")));

        // A foreign process appends to the LIVE emp mid-transaction.
        ForeignAppend(4, "Mallory");

        // Second read: because emp was never written by this tx (no private copy), the read hits the live file
        // and the foreign row IS visible → non-repeatable read (documented ReadCommitted boundary).
        Assert.Equal(4, Convert.ToInt32(ExecScalar(connA, "SELECT COUNT(*) FROM emp")));

        tx.Rollback();
    }

    /// <summary>Contrast: once the transaction has WRITTEN the table (private copy taken), its reads are stable
    /// and a foreign write to the LIVE file is NOT visible to the transacting connection.</summary>
    [Fact]
    public void D_WrittenTable_IsIsolated_From_Foreign_Write()
    {
        using var connA = Open();
        var tx = connA.BeginTransaction();

        // Write emp → the tx takes emp's private working copy; A now reads that copy.
        ExecNonQuery(connA, "UPDATE emp SET amount = 111.11 WHERE id = 1");
        Assert.Equal(3, Convert.ToInt32(ExecScalar(connA, "SELECT COUNT(*) FROM emp")));

        // A foreign process appends to the LIVE emp.
        ForeignAppend(4, "Mallory");

        // A still reads its private copy → the foreign row is NOT visible (stable read for a written table).
        Assert.Equal(3, Convert.ToInt32(ExecScalar(connA, "SELECT COUNT(*) FROM emp")));
        Assert.Null(ExecScalar(connA, "SELECT name FROM emp WHERE id = 4"));

        tx.Rollback();
    }

    // ---- (e) explicit IsolationLevel request is CLAMPED to (and honestly reported as) ReadCommitted -----

    /// <summary>The copy-on-write model delivers EXACTLY ReadCommitted, so a caller that explicitly requests a
    /// STRONGER level (RepeatableRead / Snapshot / Serializable) or a WEAKER one (ReadUncommitted / Chaos) is
    /// clamped to ReadCommitted — and <see cref="IsolationLevel"/> honestly reports ReadCommitted rather than
    /// echoing back a level the transaction does not actually provide. The transaction still functions (a write
    /// + read-your-writes + rollback) under the clamped level. Closes the task-3 "no silent gaps" requirement on
    /// the explicit-request path (the earlier pin only covered the parameterless overload).</summary>
    [Theory]
    [InlineData(IsolationLevel.Unspecified)]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.ReadUncommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Snapshot)]
    [InlineData(IsolationLevel.Serializable)]
    [InlineData(IsolationLevel.Chaos)]
    public void ExplicitIsolationRequest_IsClampedTo_And_Reports_ReadCommitted(IsolationLevel requested)
    {
        using var conn = Open();
        var tx = conn.BeginTransaction(requested);

        // The property never lies: whatever was requested, the delivered (and reported) level is ReadCommitted.
        Assert.Equal(IsolationLevel.ReadCommitted, tx.IsolationLevel);

        // …and the transaction still works under the clamped level: a write is visible to itself, then rolls back.
        ExecNonQuery(conn, "INSERT INTO emp (id, name, amount) VALUES (9, 'Zed', 900.00)");
        Assert.Equal("Zed", ((string)ExecScalar(conn, "SELECT name FROM emp WHERE id = 9")!).TrimEnd());
        tx.Rollback();
        Assert.Null(ExecScalar(conn, "SELECT name FROM emp WHERE id = 9"));
    }
}
