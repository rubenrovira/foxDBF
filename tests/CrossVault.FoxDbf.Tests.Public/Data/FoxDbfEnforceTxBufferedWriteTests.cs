using System;
using System.Data.Common;
using System.IO;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.MicroVfp;
using Xunit;

namespace CrossVault.FoxDbf.Tests.Data;

/// <summary>
/// Project-review 5.4 follow-up — the DIRECT-WRITER interpreter paths that the base seam fix
/// (ExecReplace/WriteFlag/ExecInsert) did NOT cover: the BUFFERED commit (TABLEUPDATE / row-buffer
/// pointer-move auto-commit) and the WHOLE-TABLE writers (<c>APPEND FROM</c>, <c>PACK</c>). Each opens its
/// own <see cref="Write.DbfWriter"/> on the table's <c>SourcePath</c>. Before the fix, run as the FIRST
/// write to a table inside a <see cref="FoxDbfTransaction"/> (e.g. from a stored procedure), they hit the
/// LIVE <c>.dbf</c>/<c>.fpt</c>/<c>.cdx</c> and escaped <see cref="FoxDbfTransaction.Rollback"/> — and for
/// <c>PACK</c> that live-file escape is an irreversible truncation.
/// <para>
/// These write commands are not SQL DML, so they can only reach the interpreter through a stored
/// procedure / ad-hoc PRG — exactly what a bound UDF or business proc does. The tests drive the connection's
/// SHARED <see cref="VfpInterpreter"/> (the same instance <c>FoxDbfEnforcedWriteModel</c> uses, over the
/// same <see cref="Sql.VfpSession"/> the transaction wires its COPY-ON-WRITE seam onto), so the write flows
/// through the identical seam a real stored proc would. Rollback ⇒ the on-disk LIVE table is byte-unchanged;
/// Commit ⇒ persisted; and an EnforceRules=on write with NO transaction stays byte-identical (regression
/// guard — <c>BeginTxWrite</c> returns the path unchanged in autocommit).
/// </para>
/// <para>
/// SAFETY: every test runs on a FRESH TEMP COPY of the committed TasTrade database; the committed
/// <c>Tastrade_VFPData/</c> original is read ONLY for planning, never mutated.
/// </para>
/// </summary>
public sealed class FoxDbfEnforceTxBufferedWriteTests
{
    private static string TastradeDir => Path.Combine(Fixtures.RepoRoot, "Tastrade_VFPData");

    private static string DbcOf(string copyDir) => Path.Combine(copyDir, "tastrade.dbc");

    // Fresh keys (no collision with setup's real key_names — SUPPLIER/PRODUCTS/CATEGORY/…).
    private const string BufKey = "ZKEY";
    private const string AppendKey = "BKEY";
    private const string SingleKey = "CKEY";
    private const string RowBufKey = "DKEY";

    private static FoxDbfConnection OpenEnforced(string copyDir)
    {
        var c = new FoxDbfConnection($"Data Source={DbcOf(copyDir)};EnforceRules=on");
        c.Open();
        return c;
    }

    private static int ExecNonQuery(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    private static object? ExecScalar(DbConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void SeedSetupRow(string copy, string key, string value)
    {
        using var seed = OpenEnforced(copy);
        Assert.Equal(1, ExecNonQuery(seed, $"INSERT INTO setup (key_name, value) VALUES ('{key}', '{value}')"));
    }

    // ═══════════════════════════ (1) buffered TABLEUPDATE — existing-row REPLACE (CommitExistingRow) ═══════════════════════════

    [Fact]
    public void EnforceTx_BufferedTableUpdate_ExistingRowReplace_Rollback_LiveUnchanged()
    {
        using var dir = new MicroVfpTestSupport.TempDir("enftx_buf_repl_rollback");
        string copy = MicroVfpTestSupport.CopyDatabase(TastradeDir, dir);
        SeedSetupRow(copy, BufKey, "7");
        Assert.Equal("7", LiveValue(copy, BufKey));

        using (var conn = OpenEnforced(copy))
        {
            var interp = conn.Interpreter;      // the SHARED interpreter over conn.Session (as a stored proc runs).
            var tx = conn.BeginTransaction();

            // CURSORSETPROP('Buffering',5) + REPLACE + TABLEUPDATE(.T.) — the buffered all-rows commit that
            // opens DbfWriter directly on setup.dbf. It must land on the transaction's private copy now.
            interp.Execute(
                "USE setup\n" +
                $"LOCATE FOR key_name = '{BufKey}'\n" +
                "=CURSORSETPROP('Buffering', 5)\n" +
                "REPLACE value WITH '99'\n" +
                "=TABLEUPDATE(.T.)");

            // read-your-writes: the committed buffered edit is visible on the private copy inside the tx.
            Assert.Equal("99", Str(ExecScalar(conn, $"SELECT value FROM setup WHERE key_name = '{BufKey}'")));

            tx.Rollback();
        }

        // VERDICT: the buffered write went to the private copy — Rollback discarded it; live setup unchanged.
        Assert.Equal("7", LiveValue(copy, BufKey));
    }

    [Fact]
    public void EnforceTx_BufferedTableUpdate_ExistingRowReplace_Commit_Persists()
    {
        using var dir = new MicroVfpTestSupport.TempDir("enftx_buf_repl_commit");
        string copy = MicroVfpTestSupport.CopyDatabase(TastradeDir, dir);
        SeedSetupRow(copy, BufKey, "7");

        using (var conn = OpenEnforced(copy))
        {
            var interp = conn.Interpreter;
            var tx = conn.BeginTransaction();
            interp.Execute(
                "USE setup\n" +
                $"LOCATE FOR key_name = '{BufKey}'\n" +
                "=CURSORSETPROP('Buffering', 5)\n" +
                "REPLACE value WITH '99'\n" +
                "=TABLEUPDATE(.T.)");
            tx.Commit();
        }

        // Commit swaps the private copy over live — the buffered edit persists.
        Assert.Equal("99", LiveValue(copy, BufKey));
    }

    // ═══════════════════════════ (2) buffered TABLEUPDATE — buffered APPEND (CommitAppend) ═══════════════════════════

    [Fact]
    public void EnforceTx_BufferedTableUpdate_Append_Rollback_LiveUnchanged()
    {
        using var dir = new MicroVfpTestSupport.TempDir("enftx_buf_app_rollback");
        string copy = MicroVfpTestSupport.CopyDatabase(TastradeDir, dir);
        Assert.Equal(0, LiveKeyCount(copy, AppendKey));

        using (var conn = OpenEnforced(copy))
        {
            var interp = conn.Interpreter;
            var tx = conn.BeginTransaction();

            // A buffered INSERT committed by TABLEUPDATE goes through CommitAppend's direct DbfWriter.
            interp.Execute(
                "USE setup\n" +
                "=CURSORSETPROP('Buffering', 5)\n" +
                $"INSERT INTO setup (key_name, value) VALUES ('{AppendKey}', '3')\n" +
                "=TABLEUPDATE(.T.)");

            Assert.Equal(1, Int(ExecScalar(conn, $"SELECT COUNT(*) FROM setup WHERE key_name = '{AppendKey}'")));

            tx.Rollback();
        }

        Assert.Equal(0, LiveKeyCount(copy, AppendKey));
    }

    // ═══════════════════════════ (3) buffered TABLEUPDATE — SINGLE row (CommitBuffer non-allRows) ═══════════════════════════

    [Fact]
    public void EnforceTx_BufferedTableUpdate_SingleRow_Rollback_LiveUnchanged()
    {
        using var dir = new MicroVfpTestSupport.TempDir("enftx_buf_single_rollback");
        string copy = MicroVfpTestSupport.CopyDatabase(TastradeDir, dir);
        SeedSetupRow(copy, SingleKey, "1");

        using (var conn = OpenEnforced(copy))
        {
            var interp = conn.Interpreter;
            var tx = conn.BeginTransaction();

            // TABLEUPDATE() with no arg = commit the CURRENT row only (CommitBuffer non-allRows branch).
            interp.Execute(
                "USE setup\n" +
                $"LOCATE FOR key_name = '{SingleKey}'\n" +
                "=CURSORSETPROP('Buffering', 5)\n" +
                "REPLACE value WITH '88'\n" +
                "=TABLEUPDATE()");

            Assert.Equal("88", Str(ExecScalar(conn, $"SELECT value FROM setup WHERE key_name = '{SingleKey}'")));

            tx.Rollback();
        }

        Assert.Equal("1", LiveValue(copy, SingleKey));
    }

    // ═══════════════════════════ (4) row-buffer pointer-move auto-commit (MaybeAutoCommitRow) ═══════════════════════════

    [Fact]
    public void EnforceTx_RowBuffer_AutoCommitOnMove_Rollback_LiveUnchanged()
    {
        using var dir = new MicroVfpTestSupport.TempDir("enftx_rowbuf_rollback");
        string copy = MicroVfpTestSupport.CopyDatabase(TastradeDir, dir);
        SeedSetupRow(copy, RowBufKey, "2");

        using (var conn = OpenEnforced(copy))
        {
            var interp = conn.Interpreter;
            var tx = conn.BeginTransaction();

            // Mode 3 (row buffering): a pointer move implicitly commits the pending row edit — MaybeAutoCommitRow
            // opens the direct DbfWriter. GO TOP moves off the edited row and triggers the auto-commit.
            interp.Execute(
                "USE setup\n" +
                "=CURSORSETPROP('Buffering', 3)\n" +
                $"LOCATE FOR key_name = '{RowBufKey}'\n" +
                "REPLACE value WITH '77'\n" +
                "GO TOP");

            Assert.Equal("77", Str(ExecScalar(conn, $"SELECT value FROM setup WHERE key_name = '{RowBufKey}'")));

            tx.Rollback();
        }

        Assert.Equal("2", LiveValue(copy, RowBufKey));
    }

    // ═══════════════════════════ (5) buffered commit with NO transaction — byte-identical regression guard ═══════════════════════════

    [Fact]
    public void EnforceTx_BufferedTableUpdate_NoTransaction_Persists_Unchanged()
    {
        using var dir = new MicroVfpTestSupport.TempDir("enftx_buf_notx");
        string copy = MicroVfpTestSupport.CopyDatabase(TastradeDir, dir);
        SeedSetupRow(copy, BufKey, "7");

        using (var conn = OpenEnforced(copy))
        {
            // NO BeginTransaction: BeginTxWrite is a strict no-op, so the buffered write lands on the live file
            // exactly as before.
            conn.Interpreter.Execute(
                "USE setup\n" +
                $"LOCATE FOR key_name = '{BufKey}'\n" +
                "=CURSORSETPROP('Buffering', 5)\n" +
                "REPLACE value WITH '99'\n" +
                "=TABLEUPDATE(.T.)");
        }

        Assert.Equal("99", LiveValue(copy, BufKey));
    }

    // ═══════════════════════════ (6) APPEND FROM — whole-table bulk append ═══════════════════════════

    [Fact]
    public void EnforceTx_AppendFrom_Rollback_LiveUnchanged()
    {
        using var dir = new MicroVfpTestSupport.TempDir("enftx_appfrom_rollback");
        string copy = MicroVfpTestSupport.CopyDatabase(TastradeDir, dir);

        int before = PhysicalCount(copy, "setup");
        using (var conn = OpenEnforced(copy))
        {
            var interp = conn.Interpreter;
            interp.Execute("USE setup\nCOPY TO appsrc");   // autocommit: appsrc = a same-structure snapshot of setup.

            var tx = conn.BeginTransaction();
            interp.Execute("USE setup\nAPPEND FROM appsrc");   // whole-table bulk append via a direct DbfWriter.

            Assert.Equal(before * 2, Int(ExecScalar(conn, "SELECT COUNT(*) FROM setup")));   // read-your-writes.

            tx.Rollback();
        }

        // VERDICT: the appended rows went to the private copy — Rollback discarded them; live count unchanged.
        Assert.Equal(before, PhysicalCount(copy, "setup"));
    }

    [Fact]
    public void EnforceTx_AppendFrom_Commit_Persists()
    {
        using var dir = new MicroVfpTestSupport.TempDir("enftx_appfrom_commit");
        string copy = MicroVfpTestSupport.CopyDatabase(TastradeDir, dir);

        int before = PhysicalCount(copy, "setup");
        using (var conn = OpenEnforced(copy))
        {
            var interp = conn.Interpreter;
            interp.Execute("USE setup\nCOPY TO appsrc");

            var tx = conn.BeginTransaction();
            interp.Execute("USE setup\nAPPEND FROM appsrc");
            tx.Commit();
        }

        Assert.Equal(before * 2, PhysicalCount(copy, "setup"));
    }

    [Fact]
    public void EnforceTx_AppendFrom_NoTransaction_Persists_Unchanged()
    {
        using var dir = new MicroVfpTestSupport.TempDir("enftx_appfrom_notx");
        string copy = MicroVfpTestSupport.CopyDatabase(TastradeDir, dir);

        int before = PhysicalCount(copy, "setup");
        using (var conn = OpenEnforced(copy))
        {
            // Autocommit: byte-identical to the pre-fix APPEND FROM.
            conn.Interpreter.Execute("USE setup\nCOPY TO appsrc");
            conn.Interpreter.Execute("USE setup\nAPPEND FROM appsrc");
        }

        Assert.Equal(before * 2, PhysicalCount(copy, "setup"));
    }

    // ═══════════════════════════ (7) PACK — irreversible physical delete-compaction ═══════════════════════════

    [Fact]
    public void EnforceTx_Pack_Rollback_RestoresDeletedRows()
    {
        using var dir = new MicroVfpTestSupport.TempDir("enftx_pack_rollback");
        string copy = MicroVfpTestSupport.CopyDatabase(TastradeDir, dir);

        // A deleted row that PACK would physically remove: seed then logically delete it (autocommit).
        using (var seed = OpenEnforced(copy))
        {
            Assert.Equal(1, ExecNonQuery(seed, $"INSERT INTO setup (key_name, value) VALUES ('{BufKey}', '7')"));
            Assert.Equal(1, ExecNonQuery(seed, $"DELETE FROM setup WHERE key_name = '{BufKey}'"));
        }
        int physBefore = PhysicalCount(copy, "setup");
        int delBefore = DeletedCount(copy, "setup");
        Assert.True(delBefore >= 1, "fixture precondition: setup must have a deleted row for PACK to remove");

        using (var conn = OpenEnforced(copy))
        {
            var interp = conn.Interpreter;
            var tx = conn.BeginTransaction();
            interp.Execute("USE setup\nPACK");   // physical delete-compaction via a direct DbfWriter on the copy.
            tx.Rollback();
        }

        // VERDICT: PACK truncated the private copy, not the live file — Rollback discarded it; every physical
        // row (incl. the deleted one) is intact.
        Assert.Equal(physBefore, PhysicalCount(copy, "setup"));
        Assert.Equal(delBefore, DeletedCount(copy, "setup"));
    }

    [Fact]
    public void EnforceTx_Pack_Commit_Persists()
    {
        using var dir = new MicroVfpTestSupport.TempDir("enftx_pack_commit");
        string copy = MicroVfpTestSupport.CopyDatabase(TastradeDir, dir);

        using (var seed = OpenEnforced(copy))
        {
            Assert.Equal(1, ExecNonQuery(seed, $"INSERT INTO setup (key_name, value) VALUES ('{BufKey}', '7')"));
            Assert.Equal(1, ExecNonQuery(seed, $"DELETE FROM setup WHERE key_name = '{BufKey}'"));
        }
        int physBefore = PhysicalCount(copy, "setup");
        int delBefore = DeletedCount(copy, "setup");

        using (var conn = OpenEnforced(copy))
        {
            var interp = conn.Interpreter;
            var tx = conn.BeginTransaction();
            interp.Execute("USE setup\nPACK");
            tx.Commit();
        }

        Assert.Equal(physBefore - delBefore, PhysicalCount(copy, "setup"));   // the deleted rows are gone.
        Assert.Equal(0, DeletedCount(copy, "setup"));
    }

    [Fact]
    public void EnforceTx_Pack_NoTransaction_Persists_Unchanged()
    {
        using var dir = new MicroVfpTestSupport.TempDir("enftx_pack_notx");
        string copy = MicroVfpTestSupport.CopyDatabase(TastradeDir, dir);

        using (var seed = OpenEnforced(copy))
        {
            Assert.Equal(1, ExecNonQuery(seed, $"INSERT INTO setup (key_name, value) VALUES ('{BufKey}', '7')"));
            Assert.Equal(1, ExecNonQuery(seed, $"DELETE FROM setup WHERE key_name = '{BufKey}'"));
        }
        int physBefore = PhysicalCount(copy, "setup");
        int delBefore = DeletedCount(copy, "setup");

        using (var conn = OpenEnforced(copy))
        {
            // Autocommit: byte-identical to the pre-fix PACK.
            conn.Interpreter.Execute("USE setup\nPACK");
        }

        Assert.Equal(physBefore - delBefore, PhysicalCount(copy, "setup"));
        Assert.Equal(0, DeletedCount(copy, "setup"));
    }

    // ═══════════════════════════ helpers (read the on-disk LIVE tables of a copied DB) ═══════════════════════════

    private static string? LiveValue(string dbDir, string key)
    {
        using var db = DbfDatabase.OpenFoxpro(DbcOf(dbDir));
        using var t = db.OpenTable("setup");
        for (int i = 0; i < t.RecordCount; i++)
        {
            if (t.IsRecordDeleted(i)) continue;
            if (Field(t, i, "KEY_NAME") == key) return Field(t, i, "VALUE");
        }
        return null;
    }

    private static int LiveKeyCount(string dbDir, string key)
    {
        using var db = DbfDatabase.OpenFoxpro(DbcOf(dbDir));
        using var t = db.OpenTable("setup");
        int live = 0;
        for (int i = 0; i < t.RecordCount; i++)
            if (!t.IsRecordDeleted(i) && Field(t, i, "KEY_NAME") == key) live++;
        return live;
    }

    private static int PhysicalCount(string dbDir, string table)
    {
        using var db = DbfDatabase.OpenFoxpro(DbcOf(dbDir));
        using var t = db.OpenTable(table);
        return t.RecordCount;
    }

    private static int DeletedCount(string dbDir, string table)
    {
        using var db = DbfDatabase.OpenFoxpro(DbcOf(dbDir));
        using var t = db.OpenTable(table);
        int deleted = 0;
        for (int i = 0; i < t.RecordCount; i++)
            if (t.IsRecordDeleted(i)) deleted++;
        return deleted;
    }

    private static string Field(DbfTable t, int index, string col)
        => t.GetRecord(index)?[col]?.ToString()?.TrimEnd() ?? string.Empty;

    private static string Str(object? scalar) => scalar?.ToString()?.TrimEnd() ?? string.Empty;

    private static int Int(object? scalar) => Convert.ToInt32(scalar);
}
