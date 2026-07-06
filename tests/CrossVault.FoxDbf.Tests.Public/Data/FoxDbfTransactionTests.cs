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
/// Acceptance tests (TDD RED) for REAL, snapshot-based transactions on <see cref="FoxDbfConnection"/>:
/// an atomic <c>Commit</c> persists, a <c>Rollback</c> (or a dispose-without-commit) restores every
/// touched table to its EXACT pre-transaction bytes — including the <c>.fpt</c> memo and <c>.cdx</c>
/// index sidecars — with no leaked file handles and no corruption.
/// <para>
/// SAFETY: every test runs on a THROWAWAY temp copy of a freshly-built table (committed fixtures under
/// <c>data/</c>, <c>*_VFPData/</c>, <c>ref/</c> are never touched). The table carries a memo column so
/// the <c>.fpt</c> is exercised, and a structural <c>.cdx</c> so the index sidecar is exercised too.
/// </para>
/// </summary>
public sealed class FoxDbfTransactionTests
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foxdbf_tx_" + Guid.NewGuid().ToString("N"));
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

    private static int RecordCount(string dbf)
    {
        using var t = DbfTable.Open(dbf);
        return t.RecordCount;
    }

    private static byte[] Bytes(string path) => File.ReadAllBytes(path);

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

    // ---- (1) ROLLBACK restores exactly (incl. .fpt memo + .cdx index) ---------------------

    [Fact]
    public void Rollback_Restores_Exact_PreTransaction_State()
    {
        using var db = new EmpDb();

        // capture the pre-transaction state: logical rows + raw bytes of every file.
        var beforeState = ReadState(db.Dbf);
        var beforeDbf = Bytes(db.Dbf);
        var beforeFpt = Bytes(db.Fpt);
        var beforeCdx = Bytes(db.Cdx);

        using (var conn = db.Open())
        {
            var tx = conn.BeginTransaction();

            // INSERT (touches .dbf + .fpt memo), UPDATE a memo (touches .fpt), DELETE a row.
            Assert.Equal(1, ExecNonQuery(conn, "INSERT INTO emp (id, name, notes, amount) VALUES (6, 'Frank', 'foxtrot note', 600.00)"));
            Assert.Equal(1, ExecNonQuery(conn, "UPDATE emp SET notes = 'updated golf note', amount = 111.11 WHERE id = 1"));
            Assert.Equal(1, ExecNonQuery(conn, "DELETE FROM emp WHERE id = 3"));

            tx.Rollback();
        }

        // After rollback: logical state, RecordCount, and the exact bytes of .dbf/.fpt/.cdx restored.
        Assert.Equal(5, RecordCount(db.Dbf));
        Assert.Equal(beforeState, ReadState(db.Dbf));
        Assert.Equal(beforeDbf, Bytes(db.Dbf));
        Assert.Equal(beforeFpt, Bytes(db.Fpt));
        Assert.Equal(beforeCdx, Bytes(db.Cdx));
    }

    // ---- (2) COMMIT persists --------------------------------------------------------------

    [Fact]
    public void Commit_Persists_All_Changes()
    {
        using var db = new EmpDb();

        using (var conn = db.Open())
        {
            var tx = conn.BeginTransaction();
            ExecNonQuery(conn, "INSERT INTO emp (id, name, notes, amount) VALUES (6, 'Frank', 'foxtrot note', 600.00)");
            ExecNonQuery(conn, "UPDATE emp SET amount = 111.11 WHERE id = 1");
            ExecNonQuery(conn, "DELETE FROM emp WHERE id = 3");
            tx.Commit();
        }

        // Re-open fresh and observe durable changes.
        Assert.Equal(6, RecordCount(db.Dbf));
        var state = ReadState(db.Dbf);
        Assert.Contains(state, r => r.Id == 6 && r.Name == "Frank" && r.Notes == "foxtrot note");
        Assert.Equal(111.11m, state.Single(r => r.Id == 1).Amount);
        Assert.True(state.Single(r => r.Id == 3).Deleted);
    }

    // ---- (3) Read-your-writes within the transaction --------------------------------------

    [Fact]
    public void Within_Transaction_Select_Sees_Pending_Writes()
    {
        using var db = new EmpDb();
        using var conn = db.Open();
        var tx = conn.BeginTransaction();

        ExecNonQuery(conn, "INSERT INTO emp (id, name, notes, amount) VALUES (6, 'Frank', 'foxtrot note', 600.00)");
        ExecNonQuery(conn, "UPDATE emp SET amount = 111.11 WHERE id = 1");
        ExecNonQuery(conn, "DELETE FROM emp WHERE id = 3");

        // The pending INSERT is visible.
        Assert.Equal("Frank", ((string)ExecScalar(conn, "SELECT name FROM emp WHERE id = 6")!).TrimEnd());
        // The pending UPDATE is visible.
        Assert.Equal(111.11m, Convert.ToDecimal(ExecScalar(conn, "SELECT amount FROM emp WHERE id = 1")));
        // The pending (soft) DELETE is visible: row 3 excluded under SET DELETED ON.
        Assert.Null(ExecScalar(conn, "SELECT id FROM emp WHERE id = 3"));

        tx.Rollback();
    }

    // ---- (4) lifecycle: dispose-without-commit, double complete, nested begin --------------

    [Fact]
    public void Dispose_Without_Commit_Rolls_Back()
    {
        using var db = new EmpDb();
        var beforeState = ReadState(db.Dbf);
        var beforeDbf = Bytes(db.Dbf);

        using (var conn = db.Open())
        {
            using (var tx = conn.BeginTransaction())
            {
                ExecNonQuery(conn, "INSERT INTO emp (id, name, notes, amount) VALUES (6, 'Frank', 'foxtrot note', 600.00)");
                ExecNonQuery(conn, "UPDATE emp SET amount = 111.11 WHERE id = 1");
                // leave the using-block WITHOUT Commit -> Dispose must roll back.
            }
        }

        Assert.Equal(beforeState, ReadState(db.Dbf));
        Assert.Equal(beforeDbf, Bytes(db.Dbf));
    }

    [Fact]
    public void Double_Commit_And_Double_Rollback_Throw()
    {
        using var db = new EmpDb();
        using var conn = db.Open();

        var tx1 = conn.BeginTransaction();
        tx1.Commit();
        Assert.Throws<InvalidOperationException>(() => tx1.Commit());
        Assert.Throws<InvalidOperationException>(() => tx1.Rollback());

        var tx2 = conn.BeginTransaction();
        tx2.Rollback();
        Assert.Throws<InvalidOperationException>(() => tx2.Rollback());
        Assert.Throws<InvalidOperationException>(() => tx2.Commit());
    }

    [Fact]
    public void Nested_BeginTransaction_On_Busy_Connection_Throws()
    {
        using var db = new EmpDb();
        using var conn = db.Open();

        var tx = conn.BeginTransaction();
        Assert.Throws<InvalidOperationException>(() => conn.BeginTransaction());

        // After the first transaction completes, a new one is allowed again.
        tx.Commit();
        using var tx2 = conn.BeginTransaction();
        Assert.NotNull(tx2);
    }

    [Fact]
    public void IsolationLevel_Reports_ReadCommitted()
    {
        using var db = new EmpDb();
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        Assert.Equal(IsolationLevel.ReadCommitted, tx.IsolationLevel);
    }

    // ---- (5) Multiple tables in one transaction -------------------------------------------

    [Fact]
    public void Rollback_Restores_All_Tables_Touched()
    {
        using var db = new EmpDb();
        // a second table in the same directory (a copy of emp) -> emp2.
        File.Copy(db.Dbf, Path.Combine(db.Path, "emp2.dbf"));
        File.Copy(db.Fpt, Path.Combine(db.Path, "emp2.fpt"));
        File.Copy(db.Cdx, Path.Combine(db.Path, "emp2.cdx"));
        string dbf2 = Path.Combine(db.Path, "emp2.dbf");

        var beforeEmp = Bytes(db.Dbf);
        var beforeEmp2 = Bytes(dbf2);

        using (var conn = db.Open())
        {
            var tx = conn.BeginTransaction();
            ExecNonQuery(conn, "INSERT INTO emp  (id, name, notes, amount) VALUES (6, 'Frank', 'foxtrot note', 600.00)");
            ExecNonQuery(conn, "DELETE FROM emp2 WHERE id = 2");
            tx.Rollback();
        }

        Assert.Equal(5, RecordCount(db.Dbf));
        Assert.Equal(5, RecordCount(dbf2));
        Assert.Equal(beforeEmp, Bytes(db.Dbf));
        Assert.Equal(beforeEmp2, Bytes(dbf2));
    }

    [Fact]
    public void Commit_Persists_All_Tables_Touched()
    {
        using var db = new EmpDb();
        File.Copy(db.Dbf, Path.Combine(db.Path, "emp2.dbf"));
        File.Copy(db.Fpt, Path.Combine(db.Path, "emp2.fpt"));
        File.Copy(db.Cdx, Path.Combine(db.Path, "emp2.cdx"));
        string dbf2 = Path.Combine(db.Path, "emp2.dbf");

        using (var conn = db.Open())
        {
            var tx = conn.BeginTransaction();
            ExecNonQuery(conn, "INSERT INTO emp  (id, name, notes, amount) VALUES (6, 'Frank', 'foxtrot note', 600.00)");
            ExecNonQuery(conn, "DELETE FROM emp2 WHERE id = 2");
            tx.Commit();
        }

        Assert.Equal(6, RecordCount(db.Dbf));
        Assert.True(ReadState(dbf2).Single(r => r.Id == 2).Deleted);
    }

    [WindowsOnlyFact]
    public void Commit_PromotionFailure_RestoresAlreadyPromotedTables_And_RemainsRollbackable()
    {
        using var db = new EmpDb();
        File.Copy(db.Dbf, Path.Combine(db.Path, "emp2.dbf"));
        File.Copy(db.Fpt, Path.Combine(db.Path, "emp2.fpt"));
        File.Copy(db.Cdx, Path.Combine(db.Path, "emp2.cdx"));
        string dbf2 = Path.Combine(db.Path, "emp2.dbf");
        string fpt2 = Path.ChangeExtension(dbf2, ".fpt");

        var empBefore = ReadState(db.Dbf);
        var emp2Before = ReadState(dbf2);
        var empDbfBefore = Bytes(db.Dbf);
        var empFptBefore = Bytes(db.Fpt);
        var empCdxBefore = Bytes(db.Cdx);
        var emp2DbfBefore = Bytes(dbf2);
        var emp2FptBefore = Bytes(fpt2);

        using var conn = db.Open();
        var tx = conn.BeginTransaction();
        Assert.Equal(1, ExecNonQuery(conn, "UPDATE emp SET amount = 111.11 WHERE id = 1"));
        Assert.Equal(1, ExecNonQuery(conn, "UPDATE emp2 SET amount = 222.22 WHERE id = 2"));

        using (new FileStream(fpt2, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => tx.Commit());
        }

        tx.Rollback();

        Assert.Equal(empBefore, ReadState(db.Dbf));
        Assert.Equal(emp2Before, ReadState(dbf2));
        Assert.Equal(empDbfBefore, Bytes(db.Dbf));
        Assert.Equal(empFptBefore, Bytes(db.Fpt));
        Assert.Equal(empCdxBefore, Bytes(db.Cdx));
        Assert.Equal(emp2DbfBefore, Bytes(dbf2));
        Assert.Equal(emp2FptBefore, Bytes(fpt2));
    }

    // ---- (6) No handle leak / no corruption after rollback --------------------------------

    [Fact]
    public void After_Rollback_Table_Is_Fully_Usable_NoLeak_NoCorruption()
    {
        using var db = new EmpDb();

        using (var conn = db.Open())
        {
            var tx = conn.BeginTransaction();
            ExecNonQuery(conn, "INSERT INTO emp (id, name, notes, amount) VALUES (6, 'Frank', 'foxtrot note', 600.00)");
            ExecNonQuery(conn, "UPDATE emp SET notes = 'rolled back note' WHERE id = 2");
            tx.Rollback();

            // The connection (and its session handles) keep working after the rollback: a read sees
            // the restored state, and a fresh autocommit write succeeds.
            Assert.Equal(5, Convert.ToInt32(ExecScalar(conn, "SELECT COUNT(*) FROM emp")));
            Assert.Equal("bravo note", ((string)ExecScalar(conn, "SELECT notes FROM emp WHERE id = 2")!).TrimEnd());

            Assert.Equal(1, ExecNonQuery(conn, "UPDATE emp SET amount = 222.22 WHERE id = 2"));
        }

        // No leaked handle: the file can be re-opened, overwritten, and deleted afterwards.
        Assert.Equal(222.22m, ReadState(db.Dbf).Single(r => r.Id == 2).Amount);
        using (var w = DbfWriter.Open(db.Dbf)) { w.Flush(); } // exclusive-ish re-open proves no leak.
    }

    // ---- (7) DDL inside a transaction is atomic -------------------------------------------

    [Fact]
    public void Rollback_Of_DropTable_Restores_The_Dropped_Table()
    {
        using var db = new EmpDb();
        var beforeDbf = Bytes(db.Dbf);
        var beforeFpt = Bytes(db.Fpt);
        var beforeCdx = Bytes(db.Cdx);

        using (var conn = db.Open())
        {
            var tx = conn.BeginTransaction();
            // DROP would be permanent data loss if not snapshotted before the File.Delete.
            Assert.Equal(0, ExecNonQuery(conn, "DROP TABLE emp"));
            Assert.False(File.Exists(db.Dbf)); // gone within the transaction.
            tx.Rollback();
        }

        // Rollback brought the table (and its memo + index sidecars) back, byte-for-byte.
        Assert.True(File.Exists(db.Dbf));
        Assert.Equal(beforeDbf, Bytes(db.Dbf));
        Assert.Equal(beforeFpt, Bytes(db.Fpt));
        Assert.Equal(beforeCdx, Bytes(db.Cdx));
        Assert.Equal(5, RecordCount(db.Dbf));
    }

    [Fact]
    public void Commit_Of_DropTable_Is_Durable()
    {
        using var db = new EmpDb();

        using (var conn = db.Open())
        {
            var tx = conn.BeginTransaction();
            ExecNonQuery(conn, "DROP TABLE emp");
            tx.Commit();
        }

        Assert.False(File.Exists(db.Dbf)); // commit keeps the drop.
    }

    [Fact]
    public void Rollback_Of_CreateTable_Deletes_The_New_Table()
    {
        using var db = new EmpDb();
        string newDbf = Path.Combine(db.Path, "dept.dbf");

        using (var conn = db.Open())
        {
            var tx = conn.BeginTransaction();
            ExecNonQuery(conn, "CREATE TABLE dept (id I, name C(20), info M)");
            Assert.True(File.Exists(newDbf)); // visible within the transaction.
            tx.Rollback();
        }

        // The CREATE'd table (and any sidecar it produced, e.g. the .fpt for the memo column) is gone.
        Assert.False(File.Exists(newDbf));
        Assert.False(File.Exists(Path.ChangeExtension(newDbf, ".fpt")));
        // The untouched original table is unaffected.
        Assert.True(File.Exists(db.Dbf));
        Assert.Equal(5, RecordCount(db.Dbf));
    }

    [Fact]
    public void Commit_Of_CreateTable_Is_Durable()
    {
        using var db = new EmpDb();
        string newDbf = Path.Combine(db.Path, "dept.dbf");

        using (var conn = db.Open())
        {
            var tx = conn.BeginTransaction();
            ExecNonQuery(conn, "CREATE TABLE dept (id I, name C(20))");
            ExecNonQuery(conn, "INSERT INTO dept (id, name) VALUES (1, 'Sales')");
            tx.Commit();
        }

        Assert.True(File.Exists(newDbf));
        Assert.Equal(1, RecordCount(newDbf));
    }

    // ---- (8) DML THEN DDL on the SAME table in one transaction ----------------------------

    [Fact]
    public void Rollback_Of_Dml_Then_Ddl_Same_Table_Restores_PreTransaction_State()
    {
        using var db = new EmpDb();
        var beforeState = ReadState(db.Dbf);
        var beforeDbf = Bytes(db.Dbf);

        using (var conn = db.Open())
        {
            var tx = conn.BeginTransaction();
            // DML FIRST (takes the private copy at token T1), then DDL on the SAME table (rewrites the
            // LIVE file at token T2). Without a DDL snapshot taken even though a DML copy exists, the live
            // ALTER would survive the rollback (the DML copy is discarded but nothing restores the live).
            ExecNonQuery(conn, "UPDATE emp SET amount = 111.11 WHERE id = 1");
            ExecNonQuery(conn, "ALTER TABLE emp ADD COLUMN extra C(15)");
            tx.Rollback();
        }

        // Rollback restored the live file byte-for-byte: the ALTER's schema change is gone and the DML
        // update was never on the live file.
        Assert.Equal(beforeDbf, Bytes(db.Dbf));
        Assert.Equal(beforeState, ReadState(db.Dbf));
        using var t = DbfTable.Open(db.Dbf);
        Assert.DoesNotContain(t.Columns, c => string.Equals(c.Name, "extra", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Commit_Of_Dml_Then_Ddl_Same_Table_NoSpuriousConflict_KeepsLiveDdl()
    {
        using var db = new EmpDb();

        using (var conn = db.Open())
        {
            var tx = conn.BeginTransaction();
            ExecNonQuery(conn, "UPDATE emp SET amount = 111.11 WHERE id = 1"); // DML copy at token T1
            ExecNonQuery(conn, "ALTER TABLE emp ADD COLUMN extra C(15)");      // DDL rewrites live to T2

            // The same-transaction DDL changed the live token after the DML copy recorded T1; commit MUST
            // NOT raise a spurious conflict — the live post-DDL state is authoritative and is kept.
            tx.Commit();
        }

        // The DDL change is durable (the live file owns the committed state for a DML+DDL table).
        using var t = DbfTable.Open(db.Dbf);
        Assert.Contains(t.Columns, c => string.Equals(c.Name, "extra", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(5, RecordCount(db.Dbf));
    }

    [Fact]
    public void Rollback_Of_Dml_Then_DropTable_Same_Table_Restores_The_Table()
    {
        using var db = new EmpDb();
        var beforeDbf = Bytes(db.Dbf);
        var beforeState = ReadState(db.Dbf);

        using (var conn = db.Open())
        {
            var tx = conn.BeginTransaction();
            ExecNonQuery(conn, "UPDATE emp SET amount = 111.11 WHERE id = 1"); // DML copy first
            ExecNonQuery(conn, "DROP TABLE emp");                              // then DROP the live file
            Assert.False(File.Exists(db.Dbf));                                // gone within the tx
            tx.Rollback();
        }

        // The DROP's live deletion was undone AND the discarded DML copy left no trace: pre-tx state.
        Assert.True(File.Exists(db.Dbf));
        Assert.Equal(beforeDbf, Bytes(db.Dbf));
        Assert.Equal(beforeState, ReadState(db.Dbf));
    }

    [Fact]
    public void Rollback_Of_AlterTable_Restores_The_Original_Schema()
    {
        using var db = new EmpDb();
        var beforeDbf = Bytes(db.Dbf);

        using (var conn = db.Open())
        {
            var tx = conn.BeginTransaction();
            ExecNonQuery(conn, "ALTER TABLE emp ADD COLUMN extra C(15)");
            tx.Rollback();
        }

        // The irreversible schema rewrite was undone: the file is byte-for-byte the pre-ALTER table.
        Assert.Equal(beforeDbf, Bytes(db.Dbf));
        using var t = DbfTable.Open(db.Dbf);
        Assert.DoesNotContain(t.Columns, c => string.Equals(c.Name, "extra", StringComparison.OrdinalIgnoreCase));
    }
}
