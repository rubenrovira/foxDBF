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
/// Acceptance tests (TDD RED) for the COPY-ON-WRITE transaction upgrade: real ISOLATION + optimistic
/// CONFLICT detection on top of the guarantees the existing <see cref="FoxDbfTransactionTests"/> already
/// assert (rollback == pre-state, commit durable, read-your-writes, multi-table, dispose = rollback,
/// double-op / nested guards — those MUST keep passing).
/// <para>
/// The new contract: the FIRST write a transaction makes to a table redirects THIS connection's reads
/// AND writes to a PRIVATE working copy for the duration of the transaction. So the transacting
/// connection has read-your-writes, while OTHER connections keep reading the untouched LIVE file
/// (ISOLATION / honest <see cref="IsolationLevel.ReadCommitted"/>). Commit verifies each touched
/// table's change-token still matches the live file and atomically swaps the copy in, else throws
/// <see cref="FoxDbfTransactionConflictException"/> (no lost update). Rollback discards the copies.
/// </para>
/// <para>
/// SAFETY: every test runs on a THROWAWAY temp copy of a freshly-built table (committed fixtures under
/// <c>data/</c>, <c>*_VFPData/</c>, <c>ref/</c> are never touched).
/// </para>
/// </summary>
public sealed class FoxDbfTransactionIsolationTests
{
    // ---- temp EMP table (memo + cdx) ------------------------------------------------------

    /// <summary>A throwaway temp dir holding one EMP table: ID(I) NAME(C20) NOTES(M) AMOUNT(N10,2)
    /// ACTIVE(L), 5 rows, structural CDX on ID + NAME. Deleted on Dispose.</summary>
    private sealed class EmpDb : IDisposable
    {
        public string Path { get; }
        public string Dbf => System.IO.Path.Combine(Path, "emp.dbf");
        public string Fpt => System.IO.Path.Combine(Path, "emp.fpt");
        public string Cdx => System.IO.Path.Combine(Path, "emp.cdx");

        public EmpDb()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foxdbf_txiso_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            using var w = DbfWriter.Create(Dbf, new[]
            {
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("NAME", 'C', 20),
                new DbfColumnDef("NOTES", 'M', 4),
                new DbfColumnDef("AMOUNT", 'N', 10, 2),
                new DbfColumnDef("ACTIVE", 'L', 1),
            }, new DbfCreateOptions { Overwrite = true });
            w.AppendRecord(1, "Alice", "alpha note",   100.00m, true);
            w.AppendRecord(2, "Bob",   "bravo note",   200.00m, false);
            w.AppendRecord(3, "Carol", "charlie note", 300.00m, true);
            w.AppendRecord(4, "Dave",  "delta note",   400.00m, true);
            w.AppendRecord(5, "Eve",   "echo note",    500.00m, false);
            w.CreateTag(new CdxTagDefinition("TID", "ID"));
            w.CreateTag(new CdxTagDefinition("TNAME", "NAME"));
            w.Flush();
        }

        public string ConnectionString(string? extra = null)
            => $"Data Source={Path}" + (extra is null ? "" : ";" + extra);

        public FoxDbfConnection Open(string? extra = null)
        {
            var c = new FoxDbfConnection(ConnectionString(extra));
            c.Open();
            return c;
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    private readonly record struct Row(int Id, string Name, string Notes, decimal Amount, bool Active, bool Deleted);

    /// <summary>Read the full logical state of a table (all rows incl. deleted) as a stable list.</summary>
    private static List<Row> ReadState(string dbf)
    {
        using var t = DbfTable.Open(dbf);
        var rows = new List<Row>();
        foreach (var rec in t.EnumerateAll(includeDeleted: true))
        {
            rows.Add(new Row(
                Convert.ToInt32(rec["ID"]),
                ((string?)rec["NAME"] ?? "").TrimEnd(),
                ((string?)rec["NOTES"] ?? "").TrimEnd(),
                Convert.ToDecimal(rec["AMOUNT"]),
                rec["ACTIVE"] is bool b && b,
                rec.IsDeleted));
        }
        return rows;
    }

    private static int ExecNonQuery(System.Data.Common.DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    private static object? ExecScalar(System.Data.Common.DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    /// <summary>A foreign writer (a SEPARATE handle, NOT the transacting connection) appends a row to
    /// the LIVE table — simulates another process / connection mutating the table during A's tx.</summary>
    private static void ForeignAppend(string dbf, int id, string name)
    {
        using var w = DbfWriter.Open(dbf);
        w.AppendRecord(id, name, "intruder note", 700.00m, true);
        w.Flush();
    }

    // ---- (1) ISOLATION: a concurrent connection does NOT see uncommitted writes ------------

    [Fact]
    public void Isolation_OtherConnection_DoesNotSee_Uncommitted_Writes_Until_Commit()
    {
        using var db = new EmpDb();
        using var connA = db.Open();
        using var connB = db.Open();

        var tx = connA.BeginTransaction();
        Assert.Equal(1, ExecNonQuery(connA, "INSERT INTO emp (id, name, notes, amount) VALUES (6, 'Frank', 'foxtrot note', 600.00)"));
        Assert.Equal(1, ExecNonQuery(connA, "UPDATE emp SET amount = 111.11 WHERE id = 1"));
        Assert.Equal(1, ExecNonQuery(connA, "DELETE FROM emp WHERE id = 3"));

        // A (read-your-writes): A's OWN select inside the tx DOES see its pending changes.
        Assert.Equal("Frank", ((string)ExecScalar(connA, "SELECT name FROM emp WHERE id = 6")!).TrimEnd());
        Assert.Equal(111.11m, Convert.ToDecimal(ExecScalar(connA, "SELECT amount FROM emp WHERE id = 1")));
        Assert.Null(ExecScalar(connA, "SELECT id FROM emp WHERE id = 3")); // soft-deleted, hidden to A

        // B (ISOLATION): a SEPARATE connection reading DURING A's tx sees only the LIVE / OLD state.
        Assert.Null(ExecScalar(connB, "SELECT name FROM emp WHERE id = 6"));          // insert not visible
        Assert.Equal(100.00m, Convert.ToDecimal(ExecScalar(connB, "SELECT amount FROM emp WHERE id = 1"))); // update not visible
        Assert.NotNull(ExecScalar(connB, "SELECT id FROM emp WHERE id = 3"));         // delete not visible
        Assert.Equal(5, Convert.ToInt32(ExecScalar(connB, "SELECT COUNT(*) FROM emp")));

        tx.Commit();

        // AFTER commit B sees the committed changes.
        Assert.Equal("Frank", ((string)ExecScalar(connB, "SELECT name FROM emp WHERE id = 6")!).TrimEnd());
        Assert.Equal(111.11m, Convert.ToDecimal(ExecScalar(connB, "SELECT amount FROM emp WHERE id = 1")));
        Assert.Null(ExecScalar(connB, "SELECT id FROM emp WHERE id = 3"));
    }

    [Fact]
    public void Isolation_Rollback_OtherConnection_Never_Saw_The_Writes()
    {
        using var db = new EmpDb();
        using var connA = db.Open();
        using var connB = db.Open();

        var tx = connA.BeginTransaction();
        ExecNonQuery(connA, "INSERT INTO emp (id, name, notes, amount) VALUES (6, 'Frank', 'foxtrot note', 600.00)");

        // During A's tx, B sees the unchanged live table.
        Assert.Equal(5, Convert.ToInt32(ExecScalar(connB, "SELECT COUNT(*) FROM emp")));

        tx.Rollback();

        // After A rolls back, B still sees the original 5 rows (A's write was never on the live file).
        Assert.Equal(5, Convert.ToInt32(ExecScalar(connB, "SELECT COUNT(*) FROM emp")));
        Assert.Null(ExecScalar(connB, "SELECT name FROM emp WHERE id = 6"));
    }

    // ---- (2) CONFLICT: a foreign write during A's tx makes A.Commit fail (no lost update) --

    [Fact]
    public void Commit_Conflict_When_Foreign_Writer_Changed_Live_Table_Throws()
    {
        using var db = new EmpDb();
        using var connA = db.Open();

        var tx = connA.BeginTransaction();
        ExecNonQuery(connA, "UPDATE emp SET amount = 111.11 WHERE id = 1"); // takes the private copy of emp

        // A FOREIGN writer mutates the LIVE table after the copy was taken (change-token now differs).
        ForeignAppend(db.Dbf, 7, "Mallory");

        // Optimistic concurrency: committing the private copy would silently lose the foreign append,
        // so Commit MUST fail with a conflict (no lost update).
        Assert.Throws<FoxDbfTransactionConflictException>(() => tx.Commit());
    }

    [Fact]
    public void After_Conflict_Commit_The_Transaction_Can_Still_Rollback_Cleanly()
    {
        using var db = new EmpDb();
        using var connA = db.Open();

        var tx = connA.BeginTransaction();
        ExecNonQuery(connA, "UPDATE emp SET amount = 111.11 WHERE id = 1");
        ForeignAppend(db.Dbf, 7, "Mallory");

        Assert.Throws<FoxDbfTransactionConflictException>(() => tx.Commit());

        // A failed (conflict) commit leaves the tx rollback-able; rollback discards the private copy.
        tx.Rollback();

        // The foreign append SURVIVED (no lost update) and A's pending update was discarded.
        var state = ReadState(db.Dbf);
        Assert.Contains(state, r => r.Id == 7 && r.Name == "Mallory");
        Assert.Equal(100.00m, state.Single(r => r.Id == 1).Amount); // A's update was NOT applied
        Assert.Equal(6, state.Count(r => !r.Deleted));              // 5 original + the foreign row
    }

    // ---- (3) No handle leak / no corruption after commit AND after a failed-conflict commit -

    [Fact]
    public void After_Commit_Table_Is_Fully_Usable_NoLeak_NoCorruption()
    {
        using var db = new EmpDb();

        using (var conn = db.Open())
        {
            var tx = conn.BeginTransaction();
            ExecNonQuery(conn, "INSERT INTO emp (id, name, notes, amount) VALUES (6, 'Frank', 'foxtrot note', 600.00)");
            ExecNonQuery(conn, "UPDATE emp SET notes = 'committed note' WHERE id = 2");
            tx.Commit();

            // The connection keeps working after the commit: a read sees the committed state, and a
            // fresh autocommit write succeeds — against the swapped-in (live) file, not a stale copy.
            Assert.Equal(6, Convert.ToInt32(ExecScalar(conn, "SELECT COUNT(*) FROM emp")));
            Assert.Equal("committed note", ((string)ExecScalar(conn, "SELECT notes FROM emp WHERE id = 2")!).TrimEnd());
            Assert.Equal(1, ExecNonQuery(conn, "UPDATE emp SET amount = 222.22 WHERE id = 2"));
        }

        // No leaked handle: the file re-opens, the last write is durable, and it can be re-opened to write.
        Assert.Equal(222.22m, ReadState(db.Dbf).Single(r => r.Id == 2).Amount);
        using (var w = DbfWriter.Open(db.Dbf)) { w.Flush(); } // exclusive-ish re-open proves no leak.
    }

    [Fact]
    public void After_Failed_Conflict_Commit_And_Rollback_Table_Is_Usable_NoLeak()
    {
        using var db = new EmpDb();

        using (var conn = db.Open())
        {
            var tx = conn.BeginTransaction();
            ExecNonQuery(conn, "UPDATE emp SET amount = 111.11 WHERE id = 1");
            ForeignAppend(db.Dbf, 7, "Mallory");

            Assert.Throws<FoxDbfTransactionConflictException>(() => tx.Commit());
            tx.Rollback();

            // After the failed-then-rolled-back tx the SAME connection's table is fully usable: a read
            // sees the live state (incl. the foreign row) and a fresh autocommit write succeeds.
            Assert.Equal(6, Convert.ToInt32(ExecScalar(conn, "SELECT COUNT(*) FROM emp")));
            Assert.Equal(1, ExecNonQuery(conn, "UPDATE emp SET amount = 222.22 WHERE id = 2"));
        }

        Assert.Equal(222.22m, ReadState(db.Dbf).Single(r => r.Id == 2).Amount);
        using (var w = DbfWriter.Open(db.Dbf)) { w.Flush(); } // no leaked handle.
    }

    // ---- (4) IsolationLevel now honestly ReadCommitted ------------------------------------

    [Fact]
    public void IsolationLevel_Reports_ReadCommitted()
    {
        using var db = new EmpDb();
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        Assert.Equal(IsolationLevel.ReadCommitted, tx.IsolationLevel);
    }

    // ---- (5) DBC (.dbc database) mode: read-your-writes + isolation for a member table ------

    /// <summary>A throwaway temp <c>.dbc</c> container with one member table EMP (ID I, NAME C20,
    /// AMOUNT N10,2), 3 rows. Connecting with <c>Data Source=&lt;dbc&gt;</c> exercises the DBC code path
    /// (long table-name resolution + member .dbf redirect) instead of free-table directory mode.</summary>
    private sealed class EmpDbc : IDisposable
    {
        public string Dir { get; }
        public string Dbc => System.IO.Path.Combine(Dir, "shop.dbc");
        public string MemberDbf => System.IO.Path.Combine(Dir, "emp.dbf");

        public EmpDbc()
        {
            Dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foxdbf_txdbc_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            DbfDatabaseBuilder.Create(Dbc, new[]
            {
                new DbcTableSpec("emp", "emp.dbf", new[]
                {
                    new DbfColumnDef("ID", 'I', 4),
                    new DbfColumnDef("NAME", 'C', 20),
                    new DbfColumnDef("AMOUNT", 'N', 10, 2),
                }),
            });
            using var w = DbfWriter.Open(MemberDbf);
            w.AppendRecord(1, "Alice", 100.00m);
            w.AppendRecord(2, "Bob", 200.00m);
            w.AppendRecord(3, "Carol", 300.00m);
            w.Flush();
        }

        public FoxDbfConnection Open()
        {
            var c = new FoxDbfConnection($"Data Source={Dbc}");
            c.Open();
            return c;
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); }
            catch { /* best-effort */ }
        }
    }

    [Fact]
    public void Dbc_Member_ReadYourWrites_And_Isolation_Inside_Transaction()
    {
        using var db = new EmpDbc();
        using var connA = db.Open();
        using var connB = db.Open();

        var tx = connA.BeginTransaction();
        Assert.Equal(1, ExecNonQuery(connA, "INSERT INTO emp (id, name, amount) VALUES (4, 'Dave', 400.00)"));
        Assert.Equal(1, ExecNonQuery(connA, "UPDATE emp SET amount = 111.11 WHERE id = 1"));

        // A (read-your-writes): the transacting connection sees its own pending writes to the DBC member,
        // even though the writes went to the private working copy and the re-opened work area resolves the
        // member through the DBC (the previously-broken branch).
        Assert.Equal("Dave", ((string)ExecScalar(connA, "SELECT name FROM emp WHERE id = 4")!).TrimEnd());
        Assert.Equal(111.11m, Convert.ToDecimal(ExecScalar(connA, "SELECT amount FROM emp WHERE id = 1")));

        // B (ISOLATION): a separate connection on the same .dbc sees only the untouched LIVE member.
        Assert.Null(ExecScalar(connB, "SELECT name FROM emp WHERE id = 4"));
        Assert.Equal(100.00m, Convert.ToDecimal(ExecScalar(connB, "SELECT amount FROM emp WHERE id = 1")));
        Assert.Equal(3, Convert.ToInt32(ExecScalar(connB, "SELECT COUNT(*) FROM emp")));

        tx.Commit();

        // After commit B sees the committed member changes.
        Assert.Equal("Dave", ((string)ExecScalar(connB, "SELECT name FROM emp WHERE id = 4")!).TrimEnd());
        Assert.Equal(111.11m, Convert.ToDecimal(ExecScalar(connB, "SELECT amount FROM emp WHERE id = 1")));
        Assert.Equal(4, Convert.ToInt32(ExecScalar(connB, "SELECT COUNT(*) FROM emp")));
    }
}
