using System;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>Runtime macro substitution in expressions used by control-flow statements.</summary>
public sealed class MicroVfpMacroConditionTests
{
    private sealed class H : IDisposable
    {
        public VfpSession Session { get; } = new();
        public VfpInterpreter Interp { get; }

        public H() => Interp = new VfpInterpreter(Session);
        public void Run(string prg) => Interp.Execute(prg);
        public VfpValue Eval(string expression) => Interp.EvalExpression(expression);
        public void Dispose() => Session.Dispose();
    }

    [Fact]
    public void IfAndCase_ExpandMacroConditions_WithDotTerminator()
    {
        using var h = new H();
        h.Run(@"
lcIf = '.T.'
lcCase = 'lnValue = 7'
lnValue = 7
lcResult = ''
IF &lcIf.
  lcResult = 'if'
ENDIF
DO CASE
  CASE &lcCase.
    lcResult = lcResult + '|case'
  OTHERWISE
    lcResult = lcResult + '|otherwise'
ENDCASE
");

        Assert.Equal("if|case", h.Eval("lcResult").AsString);
    }

    [Fact]
    public void DoWhile_ReExpandsMacroCondition_EachIteration()
    {
        using var h = new H();
        h.Run(@"
lnCount = 0
lcCondition = 'lnCount < 1'
DO WHILE &lcCondition
  lnCount = lnCount + 1
  IF lnCount = 1
    lcCondition = 'lnCount < 3'
  ENDIF
ENDDO
");

        Assert.Equal(3m, h.Eval("lnCount").AsNumber);
    }

    [Fact]
    public void For_ExpandsMacroBoundsOnce_AndBurnsThemIn()
    {
        using var h = new H();
        h.Run(@"
lcFrom = '1'
lcTo = '3'
lcStep = '1'
lnCount = 0
FOR lnI = &lcFrom TO &lcTo STEP &lcStep
  lnCount = lnCount + 1
  lcFrom = '99'
  lcTo = '99'
  lcStep = '10'
ENDFOR
");

        Assert.Equal(3m, h.Eval("lnCount").AsNumber);
    }

    [Fact]
    public void Scan_ReExpandsForAndWhileMacros_PerRecord()
    {
        using var dir = new MicroVfpTestSupport.TempDir("macro_scan");
        var interp = MicroVfpTestSupport.NewTastrade(dir, out var session);
        using (session)
        {
            interp.Execute(@"
USE tastrade!setup IN 0
SELECT setup
lcFor = 'RECNO() <= 2'
lcWhile = 'RECNO() <= 3'
lnSeen = 0
SCAN FOR &lcFor WHILE &lcWhile
  lnSeen = lnSeen + RECNO()
  IF RECNO() = 1
    lcFor = 'RECNO() = 2'
  ENDIF
  IF RECNO() = 2
    lcWhile = 'RECNO() < 3'
  ENDIF
ENDSCAN
");

            Assert.Equal(3m, interp.EvalExpression("lnSeen").AsNumber);
        }
    }

    [Fact]
    public void MacroLikeText_InsideQuotedStrings_IsUnchanged()
    {
        using var h = new H();
        h.Run("name = 'expanded'");

        Assert.Equal("&name", h.Eval("'&name'").AsString);
        Assert.Equal("&name", h.Eval("\"&name\"").AsString);
    }

    [Fact]
    public void BracketStringStaysLiteral_ButArrayIndexMacroExpands()
    {
        using var h = new H();
        h.Run(@"
name = 'expanded'
DIMENSION a(2)
a(2) = 42
lnIndex = '2'
");

        Assert.Equal("literal&name", h.Eval("[literal&name]").AsString);
        Assert.Equal(42m, h.Eval("a[&lnIndex]").AsNumber);
    }
}
