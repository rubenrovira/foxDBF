using System;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP final P2 batch — DBC / SESSION helper functions (MICROVFP_EXTENSIONS_BACKLOG.md §C.5 + §C.6):
/// <c>SET DATABASE TO [name]</c> + <c>SET("DATABASE")</c> + <c>DBC()</c>, <c>INDBC(cName, cType)</c> for
/// TABLE / FIELD existence in the current container, and the open-mode predicates
/// <c>ISEXCLUSIVE()</c> / <c>ISREADONLY()</c>.
///
/// Written TESTS-FIRST — RED until implemented. SAFETY: every case runs over a FRESH TEMP COPY of the
/// committed TasTrade sample database (MicroVfpTestSupport.NewTastrade); the committed fixture is never
/// touched, and there is NO customer data and NO VFP9 runtime here.
/// </summary>
public sealed class MicroVfpDbcLangDatabaseTests
{
    [Fact]
    public void Dbc_And_SetDatabaseFunction_ReportTheOpenContainer()
    {
        using var dir = new MicroVfpTestSupport.TempDir("dbclang_dbc");
        var interp = MicroVfpTestSupport.NewTastrade(dir, out var session);
        using var _ = session;

        // An open DBC is the current database: DBC() = its full path; SET("DATABASE") = the bare name.
        Assert.EndsWith("tastrade.dbc", interp.EvalExpression("DBC()").AsString, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("tastrade", interp.EvalExpression("SET('DATABASE')").AsString, ignoreCase: true);
    }

    [Fact]
    public void SetDatabaseTo_NoName_ClearsCurrentDesignation()
    {
        using var dir = new MicroVfpTestSupport.TempDir("dbclang_dbcclear");
        var interp = MicroVfpTestSupport.NewTastrade(dir, out var session);
        using var _ = session;

        interp.Execute("SET DATABASE TO");
        Assert.Equal("", interp.EvalExpression("DBC()").AsString);
        Assert.Equal("", interp.EvalExpression("SET('DATABASE')").AsString);

        // Re-selecting by name restores the current-database designation.
        interp.Execute("SET DATABASE TO tastrade");
        Assert.EndsWith("tastrade.dbc", interp.EvalExpression("DBC()").AsString, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("tastrade", interp.EvalExpression("SET('DATABASE')").AsString, ignoreCase: true);
    }

    [Fact]
    public void Indbc_Table_ExistenceCheck()
    {
        using var dir = new MicroVfpTestSupport.TempDir("dbclang_indbc_t");
        var interp = MicroVfpTestSupport.NewTastrade(dir, out var session);
        using var _ = session;

        Assert.True(interp.EvalExpression("INDBC('customer', 'TABLE')").AsLogical);
        Assert.True(interp.EvalExpression("INDBC('orders', 'Table')").AsLogical);   // case-insensitive type.
        Assert.False(interp.EvalExpression("INDBC('nosuchtable', 'TABLE')").AsLogical);
    }

    [Fact]
    public void Indbc_Field_RequiresAliasQualification()
    {
        using var dir = new MicroVfpTestSupport.TempDir("dbclang_indbc_f");
        var interp = MicroVfpTestSupport.NewTastrade(dir, out var session);
        using var _ = session;

        // Derive a REAL long field name from the container (robust to the fixture's exact schema).
        string field = session.Database!.GetTableRules("customer").Fields[0].FieldName;

        Assert.True(interp.EvalExpression($"INDBC('customer.{field}', 'FIELD')").AsLogical);
        Assert.False(interp.EvalExpression("INDBC('customer.nosuchfield', 'FIELD')").AsLogical);
        // hackfox s4g436: an UNQUALIFIED field name (no alias. prefix) returns .F. even though it exists.
        Assert.False(interp.EvalExpression($"INDBC('{field}', 'FIELD')").AsLogical);
    }

    [Fact]
    public void IsExclusive_And_IsReadOnly_ReflectHowTheTableWasUsed()
    {
        using var dir = new MicroVfpTestSupport.TempDir("dbclang_openmode");
        var interp = MicroVfpTestSupport.NewTastrade(dir, out var session);
        using var _ = session;

        // EXCLUSIVE, writable.
        interp.Execute("USE customer EXCLUSIVE");
        Assert.True(interp.EvalExpression("ISEXCLUSIVE()").AsLogical);
        Assert.False(interp.EvalExpression("ISREADONLY()").AsLogical);

        // SHARED + NOUPDATE in another work area.
        interp.Execute("USE employee SHARED NOUPDATE IN 0");
        Assert.False(interp.EvalExpression("ISEXCLUSIVE('employee')").AsLogical);
        Assert.True(interp.EvalExpression("ISREADONLY('employee')").AsLogical);
    }

    [Fact]
    public void SetDatabaseTo_UnknownName_RaisesNotOpen()
    {
        using var dir = new MicroVfpTestSupport.TempDir("dbclang_dbcbogus");
        var interp = MicroVfpTestSupport.NewTastrade(dir, out var session);
        using var _ = session;

        // VFP9: SET DATABASE TO <name-of-a-database-that-isn't-open> raises "Database 'X' is not open."
        var ex = Assert.Throws<MicroVfpRuntimeException>(() => interp.Execute("SET DATABASE TO bogusname"));
        Assert.Contains("not open", ex.Message, StringComparison.OrdinalIgnoreCase);
        // The open container is still current after the rejected switch.
        Assert.Equal("tastrade", interp.EvalExpression("SET('DATABASE')").AsString, ignoreCase: true);
    }

    [Fact]
    public void ClearAll_ClosesDatabase_DbcReportsEmpty()
    {
        using var dir = new MicroVfpTestSupport.TempDir("dbclang_clearalldbc");
        var interp = MicroVfpTestSupport.NewTastrade(dir, out var session);
        using var _ = session;

        Assert.Equal("tastrade", interp.EvalExpression("SET('DATABASE')").AsString, ignoreCase: true);
        // VFP9 CLEAR ALL closes all files INCLUDING databases ⇒ DBC()/SET("DATABASE") return "".
        interp.Execute("CLEAR ALL");
        Assert.Equal("", interp.EvalExpression("DBC()").AsString);
        Assert.Equal("", interp.EvalExpression("SET('DATABASE')").AsString);
    }
}
