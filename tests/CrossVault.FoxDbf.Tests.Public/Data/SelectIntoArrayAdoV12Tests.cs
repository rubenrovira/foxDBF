using System;
using System.Data;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests.Data;

/// <summary>
/// ROADMAP v1.2 — <c>SELECT … INTO ARRAY name</c> (TDD). Shape + values are pinned to the authoritative
/// VFP9 runtime facts (see the internal <c>*Vfp9OracleAdoV12*</c> companion), reproduced here as
/// deterministic public assertions so the feature is guarded on a CI box without VFP9:
/// <list type="bullet">
///   <item>a MULTI-column result → a 2-D array of nRows × nCols (row-major).</item>
///   <item>a SINGLE-column result → a 2-D array of nRows × 1 — i.e. <c>ALEN(a,2)==1</c>, NOT a true 1-D
///   array (which reports <c>ALEN(a,2)==0</c>). This is what the VFP9 runtime actually does; the docs' "1-D"
///   wording is imprecise.</item>
///   <item>a single ROW still keeps the column count (1 × nCols).</item>
///   <item>ZERO rows leave the array UNCHANGED (an existing array is untouched; a new name is never created)
///   and set <c>_TALLY = 0</c>.</item>
/// </list>
/// The write lands in the ACTIVE session's memvar store (the bound interpreter's <see cref="MemoryStore"/>),
/// so it is readable from a following microVFP expression and is NOT visible to another connection.
/// SAFETY: throwaway temp tables only.
/// </summary>
public sealed class SelectIntoArrayAdoV12Tests : IDisposable
{
    private readonly SqlTestSupport.TempDir _dir = new();

    public SelectIntoArrayAdoV12Tests()
    {
        using var w = DbfWriter.Create(_dir.File("t3.dbf"), new[]
        {
            new DbfColumnDef("ID", 'I', 4),
            new DbfColumnDef("NAME", 'C', 10),
            new DbfColumnDef("AMOUNT", 'N', 10, 2),
        }, new DbfCreateOptions { Overwrite = true });
        w.AppendRecord(1, "Alice", 10.5m);
        w.AppendRecord(2, "Bob", 20m);
        w.AppendRecord(3, "Carol", 30m);
        w.Flush();
    }

    public void Dispose() => _dir.Dispose();

    private VfpInterpreter NewInterp(out VfpSession session)
    {
        session = new VfpSession();
        session.OpenDirectory(_dir.Path);
        return new VfpInterpreter(session);
    }

    private static int I(VfpInterpreter it, string expr) => (int)it.EvalExpression(expr).AsNumber;
    private static decimal N(VfpInterpreter it, string expr) => it.EvalExpression(expr).AsNumber;
    private static string S(VfpInterpreter it, string expr) => it.EvalExpression(expr).AsString;

    // ---- (1) microVFP SP path: SELECT … INTO ARRAY shape + values ----------------------------

    [Fact]
    public void IntoArray_MultiColumn_Is2D_RowsByCols_WithValues()
    {
        var interp = NewInterp(out var s);
        using (s)
        {
            interp.Execute("SELECT id, name, amount FROM t3 INTO ARRAY a");
            Assert.Equal(9, I(interp, "ALEN(a)"));
            Assert.Equal(3, I(interp, "ALEN(a,1)"));
            Assert.Equal(3, I(interp, "ALEN(a,2)"));
            Assert.Equal(2, I(interp, "a(2,1)"));
            Assert.Equal("Bob", S(interp, "ALLTRIM(a(2,2))"));
            Assert.Equal(20m, N(interp, "a(2,3)"));
            // linear (row-major) addressing: element 5 == (2,2)
            Assert.Equal("Bob", S(interp, "ALLTRIM(a(5))"));
            Assert.Equal(3, I(interp, "_TALLY"));
        }
    }

    [Fact]
    public void IntoArray_SingleColumn_Is2D_NRowsBy1_NotTrue1D()
    {
        var interp = NewInterp(out var s);
        using (s)
        {
            interp.Execute("SELECT id FROM t3 INTO ARRAY a");
            Assert.Equal(3, I(interp, "ALEN(a)"));
            Assert.Equal(3, I(interp, "ALEN(a,1)"));
            // THE crux: the VFP9 runtime reports 1 column here (an N×1 array), NOT 0 (a genuine 1-D array yields 0).
            Assert.Equal(1, I(interp, "ALEN(a,2)"));
            Assert.Equal(1, I(interp, "a(1)"));
            Assert.Equal(3, I(interp, "a(3)"));
        }
    }

    [Fact]
    public void IntoArray_SingleRow_MultiColumn_Is_1ByNCols()
    {
        var interp = NewInterp(out var s);
        using (s)
        {
            interp.Execute("SELECT id, name, amount FROM t3 WHERE id = 1 INTO ARRAY a");
            Assert.Equal(3, I(interp, "ALEN(a)"));
            Assert.Equal(1, I(interp, "ALEN(a,1)"));
            Assert.Equal(3, I(interp, "ALEN(a,2)"));
            Assert.Equal(1, I(interp, "a(1)"));
            Assert.Equal("Alice", S(interp, "ALLTRIM(a(2))"));
            Assert.Equal(10.5m, N(interp, "a(3)"));
            Assert.Equal(1, I(interp, "_TALLY"));
        }
    }

    [Fact]
    public void IntoArray_ZeroRows_PreExistingArray_LeftUnchanged_TallyZero()
    {
        var interp = NewInterp(out var s);
        using (s)
        {
            interp.Execute("DIMENSION a(3)");
            interp.Execute("a(1) = 'keep'");
            interp.Execute("SELECT id FROM t3 WHERE id = 999 INTO ARRAY a");
            // Unchanged: still 3 elements, first element still the char 'keep'.
            Assert.Equal("C", S(interp, "TYPE('a')"));
            Assert.Equal(3, I(interp, "ALEN(a)"));
            Assert.Equal("keep", S(interp, "ALLTRIM(a(1))"));
            Assert.Equal(0, I(interp, "_TALLY"));
        }
    }

    [Fact]
    public void IntoArray_ZeroRows_NewName_NotCreated_TallyZero()
    {
        var interp = NewInterp(out var s);
        using (s)
        {
            interp.Execute("SELECT id FROM t3 WHERE id = 999 INTO ARRAY newarr");
            Assert.Equal("U", S(interp, "TYPE('newarr')"));  // never created
            Assert.Equal(0, I(interp, "_TALLY"));
        }
    }

    // ---- (2) FoxDbfCommand (ADO.NET) path: _TALLY returned + array readable / session-scoped --

    [Fact]
    public void Ado_IntoArray_ExecuteNonQuery_Returns_Tally_And_Array_Is_Readable()
    {
        using var conn = new FoxDbfConnection($"Data Source={_dir.Path}");
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, amount FROM t3 INTO ARRAY qa";
            Assert.Equal(3, cmd.ExecuteNonQuery());   // _TALLY == row count
        }

        // The array is readable from a FOLLOWING microVFP expression on the SAME connection.
        Assert.Equal(6m, Convert.ToDecimal(ExecScalar(conn, "?ALEN(qa)")));       // 3 rows × 2 cols
        Assert.Equal(3m, Convert.ToDecimal(ExecScalar(conn, "?ALEN(qa,1)")));
        Assert.Equal(2m, Convert.ToDecimal(ExecScalar(conn, "?ALEN(qa,2)")));
        Assert.Equal(20m, Convert.ToDecimal(ExecScalar(conn, "?qa(2,2)")));
    }

    [Fact]
    public void Ado_IntoArray_ZeroRows_ExecuteNonQuery_Returns_Zero()
    {
        using var conn = new FoxDbfConnection($"Data Source={_dir.Path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM t3 WHERE id = 999 INTO ARRAY qz";
        Assert.Equal(0, cmd.ExecuteNonQuery());
        Assert.Equal("U", ExecScalar(conn, "?TYPE('qz')")?.ToString());
    }

    [Fact]
    public void Ado_IntoArray_Lands_In_Active_Session_Only_NotVisible_To_Another_Connection()
    {
        using var connA = new FoxDbfConnection($"Data Source={_dir.Path}");
        connA.Open();
        using var connB = new FoxDbfConnection($"Data Source={_dir.Path}");
        connB.Open();

        ExecNonQuery(connA, "SELECT id FROM t3 INTO ARRAY shared_name");

        // A's store has it…
        Assert.Equal(3m, Convert.ToDecimal(ExecScalar(connA, "?ALEN(shared_name)")));
        // …but a different connection's data session / memvar store does NOT.
        Assert.Equal("U", ExecScalar(connB, "?TYPE('shared_name')")?.ToString());
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
