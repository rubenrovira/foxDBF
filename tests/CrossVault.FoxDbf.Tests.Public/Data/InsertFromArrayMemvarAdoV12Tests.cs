using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests.Data;

/// <summary>
/// ROADMAP v1.2 — <c>INSERT INTO tbl FROM ARRAY arr</c> and <c>… FROM MEMVAR</c> (TDD). The mapping rules
/// are pinned to the authoritative VFP9 runtime facts (see the internal <c>*Vfp9OracleAdoV12*</c> companion):
/// <list type="bullet">
///   <item>FROM ARRAY, 2-D array → ONE appended row PER ARRAY ROW; columns map to the table's fields by
///   POSITION within each row.</item>
///   <item>FROM ARRAY, 1-D array → exactly ONE row (all elements = its "columns").</item>
///   <item>excess array columns are IGNORED; a row narrower than the table leaves the missing fields BLANK
///   (default).</item>
///   <item>FROM MEMVAR → one row whose fields map by NAME from the <c>m.&lt;field&gt;</c> memvars; a field
///   with no matching memvar stays BLANK.</item>
/// </list>
/// The append runs through the NORMAL DML path (<c>OpenWritableTarget</c> → index maintenance + the ADO.NET
/// copy-on-write transaction seam + EnforceRules), which these tests exercise directly.
/// SAFETY: throwaway temp tables / a throwaway temp <c>.dbc</c> only.
/// </summary>
public sealed class InsertFromArrayMemvarAdoV12Tests : IDisposable
{
    private readonly SqlTestSupport.TempDir _dir = new();
    private readonly string _tgt;

    public InsertFromArrayMemvarAdoV12Tests()
    {
        _tgt = _dir.File("tgt.dbf");
        using var w = DbfWriter.Create(_tgt, new[]
        {
            new DbfColumnDef("F1", 'I', 4),
            new DbfColumnDef("F2", 'C', 10),
            new DbfColumnDef("F3", 'N', 6, 2),
        }, new DbfCreateOptions { Overwrite = true });
        w.CreateTag(new CdxTagDefinition("TF1", "F1"));
        w.Flush();
    }

    public void Dispose() => _dir.Dispose();

    private VfpInterpreter NewInterp(out VfpSession session)
    {
        session = new VfpSession();
        session.OpenDirectory(_dir.Path);
        return new VfpInterpreter(session);
    }

    private static List<(int F1, string F2, decimal F3)> Rows(VfpSession s)
    {
        var res = s.Execute("SELECT f1, f2, f3 FROM tgt ORDER BY f1")!;
        return res.Rows.Select(r => (
            Convert.ToInt32(r[0]),
            ((string?)r[1] ?? "").TrimEnd(),
            Convert.ToDecimal(r[2]))).ToList();
    }

    // ---- (1) microVFP path: FROM ARRAY mapping rules -----------------------------------------

    [Fact]
    public void FromArray_2D_Appends_OneRow_PerArrayRow_PositionMapped()
    {
        var interp = NewInterp(out var s);
        using (s)
        {
            interp.Execute("DIMENSION arr(2,3)");
            interp.Execute("arr(1,1) = 100");
            interp.Execute("arr(1,2) = 'row1'");
            interp.Execute("arr(1,3) = 1.5");
            interp.Execute("arr(2,1) = 200");
            interp.Execute("arr(2,2) = 'row2'");
            interp.Execute("arr(2,3) = 2.5");
            interp.Execute("INSERT INTO tgt FROM ARRAY arr");

            var rows = Rows(s);
            Assert.Equal(2, rows.Count);
            Assert.Equal((100, "row1", 1.5m), rows[0]);
            Assert.Equal((200, "row2", 2.5m), rows[1]);
        }
    }

    [Fact]
    public void FromArray_1D_Wider_Than_Table_Is_OneRow_ExcessIgnored()
    {
        var interp = NewInterp(out var s);
        using (s)
        {
            interp.Execute("DIMENSION arr(5)");
            interp.Execute("arr(1) = 11");
            interp.Execute("arr(2) = 'two'");
            interp.Execute("arr(3) = 3.5");
            interp.Execute("arr(4) = 44");     // excess → ignored
            interp.Execute("arr(5) = 'five'"); // excess → ignored
            interp.Execute("INSERT INTO tgt FROM ARRAY arr");

            var rows = Rows(s);
            Assert.Single(rows);
            Assert.Equal((11, "two", 3.5m), rows[0]);
        }
    }

    [Fact]
    public void FromArray_1D_Narrower_Than_Table_Is_OneRow_MissingFieldsBlank()
    {
        var interp = NewInterp(out var s);
        using (s)
        {
            interp.Execute("DIMENSION arr(2)");
            interp.Execute("arr(1) = 77");
            interp.Execute("arr(2) = 'nn'");
            interp.Execute("INSERT INTO tgt FROM ARRAY arr");

            var rows = Rows(s);
            Assert.Single(rows);
            Assert.Equal((77, "nn", 0m), rows[0]);  // F3 missing → blank numeric (0)
        }
    }

    [Fact]
    public void FromMemvar_Maps_By_Name()
    {
        var interp = NewInterp(out var s);
        using (s)
        {
            interp.Execute("m.f1 = 555");
            interp.Execute("m.f2 = 'mv'");
            interp.Execute("m.f3 = 9.9");
            interp.Execute("INSERT INTO tgt FROM MEMVAR");

            var rows = Rows(s);
            Assert.Single(rows);
            Assert.Equal((555, "mv", 9.9m), rows[0]);
        }
    }

    [Fact]
    public void FromMemvar_Missing_Field_Stays_Blank()
    {
        var interp = NewInterp(out var s);
        using (s)
        {
            interp.Execute("m.f1 = 42");
            interp.Execute("m.f3 = 7.7");   // f2 intentionally unset
            interp.Execute("INSERT INTO tgt FROM MEMVAR");

            var rows = Rows(s);
            Assert.Single(rows);
            Assert.Equal((42, "", 7.7m), rows[0]);  // F2 → blank
        }
    }

    // ---- (1b) a column list is a SYNTAX ERROR with FROM ARRAY / FROM MEMVAR (VFP9 error 10) --

    /// <summary>VFP9 rejects <c>INSERT INTO tbl (cols) FROM ARRAY|MEMVAR</c> with error 10 "Syntax error" — a
    /// column list is only valid with VALUES (oracle-pinned in the internal typed-error oracle). The parser
    /// throws the same typed error rather than silently ignoring the list and mapping the array/memvar data to
    /// the table's full physical field order (wrong columns). Both the array and the memvar form, single- and
    /// multi-column lists, are rejected — and the throw happens at PARSE time, before any array need exist.</summary>
    [Theory]
    [InlineData("INSERT INTO tgt (f1) FROM ARRAY arr")]
    [InlineData("INSERT INTO tgt (f1, f2) FROM ARRAY arr")]
    [InlineData("INSERT INTO tgt (f1) FROM MEMVAR")]
    [InlineData("INSERT INTO tgt (f1, f2) FROM MEMVAR")]
    public void ColumnList_With_FromArrayOrMemvar_Throws_Vfp9SyntaxError(string sql)
    {
        var interp = NewInterp(out var s);
        using (s)
        {
            var ex = Assert.Throws<FoxDbfSqlException>(() => s.Execute(sql));
            Assert.Equal(10, ex.VfpErrorNumber);   // VFP9 error 10 "Syntax error".
        }
    }

    /// <summary>The same rejection reaches the ADO.NET surface: <see cref="FoxDbfCommand.ExecuteNonQuery"/> of
    /// the illegal statement throws the typed syntax error (no row is appended).</summary>
    [Fact]
    public void Ado_ColumnList_With_FromArray_Throws_And_Appends_Nothing()
    {
        using var conn = new FoxDbfConnection($"Data Source={_dir.Path}");
        conn.Open();
        conn.Interpreter.Execute("DIMENSION arr(1,3)");
        conn.Interpreter.Execute("arr(1,1)=1");
        conn.Interpreter.Execute("arr(1,2)='x'");
        conn.Interpreter.Execute("arr(1,3)=2");

        var ex = Assert.Throws<FoxDbfSqlException>(() => ExecNonQuery(conn, "INSERT INTO tgt (f1) FROM ARRAY arr"));
        Assert.Equal(10, ex.VfpErrorNumber);
        Assert.Equal(0, Convert.ToInt32(ExecScalar(conn, "SELECT COUNT(*) FROM tgt")));
    }

    // ---- (2) ADO.NET copy-on-write transaction: FROM ARRAY rollback undoes rows + index ------

    [Fact]
    public void Ado_FromArray_InsideTransaction_Rollback_Undoes_AllRows_And_IndexEntries()
    {
        byte[] cdxBefore = File.ReadAllBytes(Path.ChangeExtension(_tgt, ".cdx"));

        using (var conn = new FoxDbfConnection($"Data Source={_dir.Path}"))
        {
            conn.Open();
            // Build a 2×3 array in this connection's memvar store.
            conn.Interpreter.Execute("DIMENSION arr(2,3)");
            conn.Interpreter.Execute("arr(1,1)=1001");
            conn.Interpreter.Execute("arr(1,2)='tx-a'");
            conn.Interpreter.Execute("arr(1,3)=5.5");
            conn.Interpreter.Execute("arr(2,1)=1002");
            conn.Interpreter.Execute("arr(2,2)='tx-b'");
            conn.Interpreter.Execute("arr(2,3)=6.5");

            var tx = conn.BeginTransaction();
            Assert.Equal(2, ExecNonQuery(conn, "INSERT INTO tgt FROM ARRAY arr"));

            // read-your-writes inside the tx: both appended rows are visible.
            Assert.Equal(2, Convert.ToInt32(ExecScalar(conn, "SELECT COUNT(*) FROM tgt")));
            Assert.Equal("tx-a", ((string)ExecScalar(conn, "SELECT f2 FROM tgt WHERE f1 = 1001")!).TrimEnd());

            tx.Rollback();

            // Rollback leaves ZERO trace: no rows, and an indexed lookup finds nothing.
            Assert.Equal(0, Convert.ToInt32(ExecScalar(conn, "SELECT COUNT(*) FROM tgt")));
            Assert.Null(ExecScalar(conn, "SELECT f1 FROM tgt WHERE f1 = 1001"));
        }

        // The live table is empty again and the .cdx is byte-for-byte the pre-transaction file.
        using (var t = DbfTable.Open(_tgt)) Assert.Equal(0, t.RecordCount);
        Assert.Equal(cdxBefore, File.ReadAllBytes(Path.ChangeExtension(_tgt, ".cdx")));
    }

    [Fact]
    public void Ado_FromArray_Autocommit_Persists_All_Rows()
    {
        using (var conn = new FoxDbfConnection($"Data Source={_dir.Path}"))
        {
            conn.Open();
            conn.Interpreter.Execute("DIMENSION arr(2,3)");
            conn.Interpreter.Execute("arr(1,1)=7");
            conn.Interpreter.Execute("arr(1,2)='p'");
            conn.Interpreter.Execute("arr(1,3)=1");
            conn.Interpreter.Execute("arr(2,1)=8");
            conn.Interpreter.Execute("arr(2,2)='q'");
            conn.Interpreter.Execute("arr(2,3)=2");
            Assert.Equal(2, ExecNonQuery(conn, "INSERT INTO tgt FROM ARRAY arr"));
        }
        using var t = DbfTable.Open(_tgt);
        Assert.Equal(2, t.RecordCount);
    }

    // ---- (3) EnforceRules (DBC) path: FROM ARRAY / FROM MEMVAR run through the enforced model --

    /// <summary>A throwaway temp <c>.dbc</c> with a free-of-rules member table PARTS (PID I, PNAME C10,
    /// QTY N6,2) so an EnforceRules connection routes the write through the enforced write-model.</summary>
    private sealed class PartsDbc : IDisposable
    {
        public string Dir { get; }
        public string Dbc => Path.Combine(Dir, "shop.dbc");
        public string MemberDbf => Path.Combine(Dir, "parts.dbf");

        public PartsDbc()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_v12enf_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            DbfDatabaseBuilder.Create(Dbc, new[]
            {
                new DbcTableSpec("parts", "parts.dbf", new[]
                {
                    new DbfColumnDef("PID", 'I', 4),
                    new DbfColumnDef("PNAME", 'C', 10),
                    new DbfColumnDef("QTY", 'N', 6, 2),
                }),
            });
        }

        public FoxDbfConnection OpenEnforced()
        {
            var c = new FoxDbfConnection($"Data Source={Dbc};EnforceRules=on");
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
    public void EnforceRules_FromArray_Appends_Through_EnforcedModel()
    {
        using var db = new PartsDbc();
        using (var conn = db.OpenEnforced())
        {
            conn.Interpreter.Execute("DIMENSION arr(2,3)");
            conn.Interpreter.Execute("arr(1,1)=10");
            conn.Interpreter.Execute("arr(1,2)='bolt'");
            conn.Interpreter.Execute("arr(1,3)=3");
            conn.Interpreter.Execute("arr(2,1)=20");
            conn.Interpreter.Execute("arr(2,2)='nut'");
            conn.Interpreter.Execute("arr(2,3)=4");

            Assert.Equal(2, ExecNonQuery(conn, "INSERT INTO parts FROM ARRAY arr"));
            Assert.Equal(2, Convert.ToInt32(ExecScalar(conn, "SELECT COUNT(*) FROM parts")));
        }
        using var t = DbfTable.Open(db.MemberDbf);
        Assert.Equal(2, t.RecordCount);
    }

    [Fact]
    public void EnforceRules_FromArray_InsideTransaction_Rollback_Undoes()
    {
        using var db = new PartsDbc();
        using (var conn = db.OpenEnforced())
        {
            conn.Interpreter.Execute("DIMENSION arr(1,3)");
            conn.Interpreter.Execute("arr(1,1)=99");
            conn.Interpreter.Execute("arr(1,2)='temp'");
            conn.Interpreter.Execute("arr(1,3)=1");

            var tx = conn.BeginTransaction();
            Assert.Equal(1, ExecNonQuery(conn, "INSERT INTO parts FROM ARRAY arr"));
            Assert.Equal(1, Convert.ToInt32(ExecScalar(conn, "SELECT COUNT(*) FROM parts")));
            tx.Rollback();
            Assert.Equal(0, Convert.ToInt32(ExecScalar(conn, "SELECT COUNT(*) FROM parts")));
        }
        using var t = DbfTable.Open(db.MemberDbf);
        Assert.Equal(0, t.RecordCount);
    }

    [Fact]
    public void EnforceRules_FromMemvar_Appends_Through_EnforcedModel()
    {
        using var db = new PartsDbc();
        using (var conn = db.OpenEnforced())
        {
            conn.Interpreter.Execute("m.pid = 5");
            conn.Interpreter.Execute("m.pname = 'gear'");
            conn.Interpreter.Execute("m.qty = 12");
            Assert.Equal(1, ExecNonQuery(conn, "INSERT INTO parts FROM MEMVAR"));

            Assert.Equal("gear", ((string)ExecScalar(conn, "SELECT pname FROM parts WHERE pid = 5")!).TrimEnd());
        }
        using var t = DbfTable.Open(db.MemberDbf);
        Assert.Equal(1, t.RecordCount);
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
}
