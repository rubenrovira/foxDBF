using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP P2 ARRAY functions — public-safe unit tests pinned to VFP9 semantics (verified against the
/// VFP9 runtime during development; the authoritative cross-check lives in the internal oracle test).
/// Each function gets one focused test: ACOPY (linear copy + auto-create dest matching source), ADEL /
/// AINS (element / row / column with the .F.-fill and last-lost rules), AELEMENT ⇄ ASUBSCRIPT round-trip
/// on a 2-D array, AFIELDS over a fresh TEMP free table, ASORT ascending / descending / 2-D column key,
/// ADATABASES / AUSED counts + contents with a database and tables open, and ASESSIONS == 1.
///
/// SAFETY: pure in-memory arrays, or FRESH TEMP tables (MicroVfpTestSupport) / a TEMP COPY of the TasTrade
/// sample DBC — never a committed fixture.
/// </summary>
public sealed class MicroVfpArrayFnsTests
{
    private static VfpInterpreter New(out VfpSession s)
    {
        s = new VfpSession();
        return new VfpInterpreter(s);
    }

    // ─────────────────────────────── ACOPY ───────────────────────────────

    [Fact]
    public void ACopy_LinearRangeCopy_AutoCreatesDestMatchingSourceDims()
    {
        var interp = New(out _);
        interp.Execute(
            "DIMENSION src(3,2)\n" +
            "src(1,1)=11\nsrc(1,2)=12\nsrc(2,1)=21\nsrc(2,2)=22\nsrc(3,1)=31\nsrc(3,2)=32\n" +
            "lnR = ACOPY(src, dst, 3, 2)");

        Assert.Equal(2, (int)interp.EvalExpression("lnR").AsNumber);      // count copied

        var dst = interp.Memory.FindArray("dst");
        Assert.NotNull(dst);
        Assert.Equal(3, dst!.Rows);                                       // dest MATCHES SOURCE dims (hackfox quirk)
        Assert.Equal(2, dst.Cols);
        Assert.Equal(21m, interp.EvalExpression("dst(1)").AsNumber);      // linear element 3 of src (row2,col1)
        Assert.Equal(22m, interp.EvalExpression("dst(2)").AsNumber);      // linear element 4 of src (row2,col2)
        Assert.Equal(VfpType.Logical, interp.EvalExpression("dst(3)").Type);  // untouched tail ⇒ .F.
    }

    // ─────────────────────────────── ADEL ───────────────────────────────

    [Fact]
    public void ADel_Element_ShiftsUp_LastBecomesFalse()
    {
        var interp = New(out _);
        interp.Execute("DIMENSION a(4)\na(1)=1\na(2)=2\na(3)=3\na(4)=4");
        Assert.Equal(1, (int)interp.EvalExpression("ADEL(a, 2)").AsNumber);   // returns 1

        Assert.Equal(1m, interp.EvalExpression("a(1)").AsNumber);
        Assert.Equal(3m, interp.EvalExpression("a(2)").AsNumber);
        Assert.Equal(4m, interp.EvalExpression("a(3)").AsNumber);
        Assert.Equal(VfpType.Logical, interp.EvalExpression("a(4)").Type);    // freed tail ⇒ .F.
        Assert.False(interp.EvalExpression("a(4)").AsLogical);
    }

    [Fact]
    public void ADel_Row_ShiftsRowsUp_LastRowBecomesFalse()
    {
        var interp = New(out _);
        interp.Execute(
            "DIMENSION a(3,2)\n" +
            "a(1,1)='a11'\na(1,2)='a12'\na(2,1)='a21'\na(2,2)='a22'\na(3,1)='a31'\na(3,2)='a32'");
        interp.EvalExpression("ADEL(a, 2)");

        Assert.Equal("a11", interp.EvalExpression("a(1,1)").AsString);
        Assert.Equal("a31", interp.EvalExpression("a(2,1)").AsString);        // old row 3 shifted up
        Assert.Equal("a32", interp.EvalExpression("a(2,2)").AsString);
        Assert.Equal(VfpType.Logical, interp.EvalExpression("a(3,1)").Type);  // freed last row ⇒ .F.
        Assert.Equal(VfpType.Logical, interp.EvalExpression("a(3,2)").Type);
    }

    [Fact]
    public void ADel_Column_ShiftsColumnsLeft_LastColumnBecomesFalse()
    {
        var interp = New(out _);
        interp.Execute(
            "DIMENSION b(3,2)\n" +
            "b(1,1)='b11'\nb(1,2)='b12'\nb(2,1)='b21'\nb(2,2)='b22'\nb(3,1)='b31'\nb(3,2)='b32'");
        interp.EvalExpression("ADEL(b, 1, 2)");                              // delete column 1

        Assert.Equal("b12", interp.EvalExpression("b(1,1)").AsString);        // col 2 shifted into col 1
        Assert.Equal(VfpType.Logical, interp.EvalExpression("b(1,2)").Type);  // freed last column ⇒ .F.
        Assert.Equal("b22", interp.EvalExpression("b(2,1)").AsString);
        Assert.Equal(VfpType.Logical, interp.EvalExpression("b(2,2)").Type);
    }

    // ─────────────────────────────── AINS ───────────────────────────────

    [Fact]
    public void AIns_Row_ShiftsRowsDown_InsertedRowIsFalse_LastRowLost()
    {
        var interp = New(out _);
        interp.Execute(
            "DIMENSION c(3,2)\n" +
            "c(1,1)='c11'\nc(1,2)='c12'\nc(2,1)='c21'\nc(2,2)='c22'\nc(3,1)='c31'\nc(3,2)='c32'");
        Assert.Equal(1, (int)interp.EvalExpression("AINS(c, 2)").AsNumber);

        Assert.Equal("c11", interp.EvalExpression("c(1,1)").AsString);        // row 1 untouched
        Assert.Equal(VfpType.Logical, interp.EvalExpression("c(2,1)").Type);  // inserted blank row ⇒ .F.
        Assert.Equal(VfpType.Logical, interp.EvalExpression("c(2,2)").Type);
        Assert.Equal("c21", interp.EvalExpression("c(3,1)").AsString);        // old row 2 pushed down; old row 3 lost
    }

    [Fact]
    public void AIns_Column_ShiftsColumnsRight_InsertedColumnIsFalse_LastColumnLost()
    {
        var interp = New(out _);
        interp.Execute(
            "DIMENSION d(2,3)\n" +
            "d(1,1)='d11'\nd(1,2)='d12'\nd(1,3)='d13'\nd(2,1)='d21'\nd(2,2)='d22'\nd(2,3)='d23'");
        interp.EvalExpression("AINS(d, 2, 2)");                              // insert column 2

        Assert.Equal("d11", interp.EvalExpression("d(1,1)").AsString);
        Assert.Equal(VfpType.Logical, interp.EvalExpression("d(1,2)").Type);  // inserted blank column ⇒ .F.
        Assert.Equal("d12", interp.EvalExpression("d(1,3)").AsString);        // old col 2 pushed right; old col 3 lost
    }

    // ─────────────────────── AELEMENT / ASUBSCRIPT ───────────────────────

    [Fact]
    public void AElement_AndASubscript_RoundTrip_On2D()
    {
        var interp = New(out _);
        interp.Execute("DIMENSION e(3,4)");

        Assert.Equal(7, (int)interp.EvalExpression("AELEMENT(e, 2, 3)").AsNumber);   // (2-1)*4 + 3
        Assert.Equal(2, (int)interp.EvalExpression("ASUBSCRIPT(e, 7, 1)").AsNumber); // row of element 7
        Assert.Equal(3, (int)interp.EvalExpression("ASUBSCRIPT(e, 7, 2)").AsNumber); // column of element 7

        // AELEMENT out of bounds ⇒ 0 (no error).
        Assert.Equal(0, (int)interp.EvalExpression("AELEMENT(e, 4, 1)").AsNumber);
        // ASUBSCRIPT out of range is a real ERROR (hackfox: NOT a 0 fallback).
        Assert.False(interp.TryEvalExpression("ASUBSCRIPT(e, 99, 1)", out _));
    }

    [Fact]
    public void AElement_SingleSubscript_On2D_ReturnsSubscript_ErrorsOutOfRange()
    {
        // MUST-FIX: a SINGLE subscript on a 2-D array is validated against Rows and returns the subscript
        // itself (verified live: AELEMENT(g,2)=2 on a 3x4 array); out of range is a runtime ERROR, not 0.
        var interp = New(out _);
        interp.Execute("DIMENSION g(3,4)");

        Assert.Equal(1, (int)interp.EvalExpression("AELEMENT(g, 1)").AsNumber);
        Assert.Equal(2, (int)interp.EvalExpression("AELEMENT(g, 2)").AsNumber);
        Assert.Equal(3, (int)interp.EvalExpression("AELEMENT(g, 3)").AsNumber);
        // Two-subscript path is unaffected: (2-1)*4 + 1 = 5.
        Assert.Equal(5, (int)interp.EvalExpression("AELEMENT(g, 2, 1)").AsNumber);
        // Single subscript beyond Rows ⇒ ERROR (not 0).
        Assert.False(interp.TryEvalExpression("AELEMENT(g, 4)", out _));
        Assert.False(interp.TryEvalExpression("AELEMENT(g, 13)", out _));
    }

    [Fact]
    public void ADel_AIns_OutOfRangeIndex_IsRuntimeError()
    {
        // MUST-FIX: an out-of-range / non-positive element index errors in VFP9 (verified live), it does
        // NOT silently no-op and report success.
        var interp = New(out _);
        interp.Execute("DIMENSION ae(4)\nae(1)=1\nae(2)=2\nae(3)=3\nae(4)=4");

        Assert.False(interp.TryEvalExpression("ADEL(ae, 99)", out _));
        Assert.False(interp.TryEvalExpression("AINS(ae, 99)", out _));
        Assert.False(interp.TryEvalExpression("ADEL(ae, 0)", out _));

        // The array is untouched by the failed calls.
        Assert.Equal(1m, interp.EvalExpression("ae(1)").AsNumber);
        Assert.Equal(4m, interp.EvalExpression("ae(4)").AsNumber);
    }

    [Fact]
    public void ACopy_PreexistingDestTooSmall_IsRuntimeError()
    {
        // MUST-FIX: a PRE-EXISTING destination is never grown; VFP9 errors (verified live) rather than
        // silently clipping to a partial count. (The auto-create quirk only applies to a NEW dest.)
        var interp = New(out _);
        interp.Execute(
            "DIMENSION src2(5)\nsrc2(1)=1\nsrc2(2)=2\nsrc2(3)=3\nsrc2(4)=4\nsrc2(5)=5\n" +
            "DIMENSION dst2(2)");

        Assert.False(interp.TryEvalExpression("ACOPY(src2, dst2, 1, 5)", out _));
    }

    // ─────────────────────────────── AFIELDS ───────────────────────────────

    [Fact]
    public void AFields_FillsStructureArray_ReturnsFieldCount()
    {
        using var dir = new MicroVfpTestSupport.TempDir("afields");
        string dbf = dir.File("t.dbf");
        using (DbfWriter.Create(dbf, new[]
        {
            new DbfColumnDef("NAME", 'C', 10),
            new DbfColumnDef("AGE",  'N', 5, 0),
            new DbfColumnDef("NN",   'N', 8, 2, nullable: true),
        }, new DbfCreateOptions())) { }

        var interp = New(out var s);
        s.OpenDirectory(dir.Path);
        interp.Execute("USE t");

        Assert.Equal(3, (int)interp.EvalExpression("AFIELDS(fa)").AsNumber);

        var fa = interp.Memory.FindArray("fa");
        Assert.NotNull(fa);
        Assert.Equal(18, fa!.Cols);                                          // VFP9 ⇒ 18 columns
        Assert.Equal("NAME", interp.EvalExpression("fa(1,1)").AsString);
        Assert.Equal("C", interp.EvalExpression("fa(1,2)").AsString);
        Assert.Equal(10, (int)interp.EvalExpression("fa(1,3)").AsNumber);
        Assert.Equal("N", interp.EvalExpression("fa(2,2)").AsString);
        Assert.False(interp.EvalExpression("fa(1,5)").AsLogical);            // NAME not nullable
        Assert.True(interp.EvalExpression("fa(3,5)").AsLogical);             // NN nullable
    }

    // ─────────────────────────────── ASORT ───────────────────────────────

    [Fact]
    public void ASort_Ascending_Then_Descending_1D()
    {
        var interp = New(out _);
        interp.Execute("DIMENSION s(4)\ns(1)=30\ns(2)=10\ns(3)=40\ns(4)=20");

        interp.EvalExpression("ASORT(s)");
        Assert.Equal(10m, interp.EvalExpression("s(1)").AsNumber);
        Assert.Equal(20m, interp.EvalExpression("s(2)").AsNumber);
        Assert.Equal(30m, interp.EvalExpression("s(3)").AsNumber);
        Assert.Equal(40m, interp.EvalExpression("s(4)").AsNumber);

        interp.EvalExpression("ASORT(s, 1, -1, 2)");                        // nSortOrder 2 ⇒ descending
        Assert.Equal(40m, interp.EvalExpression("s(1)").AsNumber);
        Assert.Equal(30m, interp.EvalExpression("s(2)").AsNumber);
        Assert.Equal(20m, interp.EvalExpression("s(3)").AsNumber);
        Assert.Equal(10m, interp.EvalExpression("s(4)").AsNumber);
    }

    [Fact]
    public void ASort_AnyNonzeroSortOrder_IsDescending()
    {
        // MUST-FIX: VFP9 sorts descending for ANY nonzero nSortOrder — not only 2. Verified live:
        // ASORT([30,10,40,20],1,-1,1) ⇒ 40,30,20,10 (descending).
        var interp = New(out _);
        interp.Execute("DIMENSION s(4)\ns(1)=30\ns(2)=10\ns(3)=40\ns(4)=20");

        interp.EvalExpression("ASORT(s, 1, -1, 1)");                        // nSortOrder 1 ⇒ descending
        Assert.Equal(40m, interp.EvalExpression("s(1)").AsNumber);
        Assert.Equal(30m, interp.EvalExpression("s(2)").AsNumber);
        Assert.Equal(20m, interp.EvalExpression("s(3)").AsNumber);
        Assert.Equal(10m, interp.EvalExpression("s(4)").AsNumber);
    }

    [Fact]
    public void ASort_2D_SortsOnColumn1_MovesWholeRows()
    {
        var interp = New(out _);
        interp.Execute(
            "DIMENSION t(3,2)\n" +
            "t(1,1)='C'\nt(1,2)='c-row'\nt(2,1)='A'\nt(2,2)='a-row'\nt(3,1)='B'\nt(3,2)='b-row'");

        interp.EvalExpression("ASORT(t)");
        Assert.Equal("A", interp.EvalExpression("t(1,1)").AsString);
        Assert.Equal("a-row", interp.EvalExpression("t(1,2)").AsString);    // whole row moved with the key
        Assert.Equal("B", interp.EvalExpression("t(2,1)").AsString);
        Assert.Equal("b-row", interp.EvalExpression("t(2,2)").AsString);
        Assert.Equal("C", interp.EvalExpression("t(3,1)").AsString);
        Assert.Equal("c-row", interp.EvalExpression("t(3,2)").AsString);
    }

    // ─────────────────────── ADATABASES / AUSED / ASESSIONS ───────────────────────

    [Fact]
    public void ADatabases_ReturnsOpenDatabase_ZeroWhenNone()
    {
        var free = New(out _);
        Assert.Equal(0, (int)free.EvalExpression("ADATABASES(da)").AsNumber);   // no DBC open

        using var dir = new MicroVfpTestSupport.TempDir("adatabases");
        var interp = MicroVfpTestSupport.NewTastrade(dir, out _);
        Assert.Equal(1, (int)interp.EvalExpression("ADATABASES(da)").AsNumber);
        Assert.Contains("tastrade", interp.EvalExpression("da(1,1)").AsString.ToLowerInvariant());
        Assert.Contains("tastrade.dbc", interp.EvalExpression("da(1,2)").AsString.ToLowerInvariant());
    }

    [Fact]
    public void AUsed_ListsOpenWorkAreas_AliasAndAreaNumber()
    {
        using var dir = new MicroVfpTestSupport.TempDir("aused");
        string dbf = dir.File("t.dbf");
        using (DbfWriter.Create(dbf, new[] { new DbfColumnDef("K", 'C', 4) }, new DbfCreateOptions())) { }

        var interp = New(out var s);
        s.OpenDirectory(dir.Path);
        interp.Execute("USE t");

        Assert.Equal(1, (int)interp.EvalExpression("AUSED(ua)").AsNumber);
        var ua = interp.Memory.FindArray("ua");
        Assert.NotNull(ua);
        Assert.Equal(2, ua!.Cols);
        Assert.Equal("T", interp.EvalExpression("ua(1,1)").AsString.ToUpperInvariant());
        Assert.Equal(1, (int)interp.EvalExpression("ua(1,2)").AsNumber);
    }

    [Fact]
    public void ASessions_SingleDataSession_ReturnsOne()
    {
        var interp = New(out _);
        Assert.Equal(1, (int)interp.EvalExpression("ASESSIONS(sa)").AsNumber);
        Assert.Equal(1, (int)interp.EvalExpression("sa(1)").AsNumber);
    }
}
