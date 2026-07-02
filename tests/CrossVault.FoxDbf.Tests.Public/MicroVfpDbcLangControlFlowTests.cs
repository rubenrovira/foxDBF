using System;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP final P2 batch — LANGUAGE / CONTROL-FLOW features (MICROVFP_EXTENSIONS_BACKLOG.md §C.3 + §C.4):
/// generalised <c>&amp;macro</c> / <c>&amp;var.</c> runtime substitution, <c>FOR EACH … ENDFOR</c>, the
/// compile-time <c>#IF</c>/<c>#IFDEF</c>/<c>#IFNDEF</c>/<c>#ELSE</c>/<c>#ENDIF</c> preprocessor (with
/// <c>#DEFINE</c> constants), and the <c>CLEAR MEMORY</c>/<c>CLEAR ALL</c> memory-release family.
///
/// Written TESTS-FIRST — RED until the parser + interpreter implement these. SAFETY: pure in-memory
/// scoping/control-flow; NO data session, NO fixture, NO oracle here (the VFP9-runtime golden for the
/// tricky cases lives in the Internal project's *DbcLang* oracle class).
/// </summary>
public sealed class MicroVfpDbcLangControlFlowTests
{
    private sealed class H : IDisposable
    {
        public VfpSession Session { get; }
        public VfpInterpreter Interp { get; }
        public H()
        {
            Session = new VfpSession();
            Interp = new VfpInterpreter(Session);
        }
        public void Run(string prg) => Interp.Execute(prg);
        public string Str(string e) => Interp.EvalExpression(e).AsString;
        public decimal Num(string e) => Interp.EvalExpression(e).AsNumber;
        public bool Bool(string e) => Interp.EvalExpression(e).AsLogical;
        public string TypeOf(string name) => Interp.EvalExpression($"TYPE('{name}')").AsString;
        public void Dispose() { try { Session.Dispose(); } catch { } }
    }

    // ─────────────────────────── &macro / &var. runtime substitution ───────────────────────────

    [Fact]
    public void Macro_WholeLine_ReParsesAndExecutes()
    {
        using var h = new H();
        // pcmd holds a whole command; the &pcmd line expands to it and runs it (x = 2+3).
        h.Run("pcmd = 'x = 2 + 3'\n&pcmd");
        Assert.Equal(5m, h.Num("x"));
    }

    [Fact]
    public void Macro_InsideClause_ExpandsNameThenExecutes()
    {
        using var h = new H();
        // &lcVar. in a TO clause expands to the target variable NAME (textual substitution before exec).
        h.Run("lcVar = 'gcResult'\nSTORE 42 TO &lcVar.");
        Assert.Equal(42m, h.Num("gcResult"));
    }

    [Fact]
    public void Macro_CommandNamePosition_Expands()
    {
        using var h = new H();
        h.Run("lcCmd = 'y = 100'\n&lcCmd");
        Assert.Equal(100m, h.Num("y"));
    }

    [Fact]
    public void Macro_SelfReferential_RaisesGracefulError_NotStackOverflow()
    {
        using var h = new H();
        // pcmd = '&pcmd' expands to itself forever. Without a depth guard the runtime re-parse re-enters
        // unboundedly → StackOverflowException (uncatchable, kills the host). VFP raises a graceful error;
        // a THROWN MicroVfpRuntimeException here proves the recursion is bounded and the process survives.
        Assert.Throws<MicroVfpRuntimeException>(() => h.Run("pcmd = '&pcmd'\n&pcmd"));
    }

    // ─────────────────────────── FOR EACH … ENDFOR ───────────────────────────

    [Fact]
    public void ForEach_VisitsEveryElement_InOrder()
    {
        using var h = new H();
        h.Run(@"
DIMENSION aItems(3)
aItems(1) = 10
aItems(2) = 20
aItems(3) = 30
gnSum = 0
gcOrder = ''
FOR EACH lnEl IN aItems
  gnSum = gnSum + lnEl
  gcOrder = gcOrder + LTRIM(STR(lnEl))
ENDFOR
");
        Assert.Equal(60m, h.Num("gnSum"));
        Assert.Equal("102030", h.Str("gcOrder"));
    }

    [Fact]
    public void ForEach_HonoursExitAndLoop()
    {
        using var h = new H();
        h.Run(@"
DIMENSION aItems(4)
aItems(1) = 1
aItems(2) = 2
aItems(3) = 3
aItems(4) = 4
gnCount = 0
FOR EACH lnEl IN aItems
  IF lnEl = 2
     LOOP
  ENDIF
  IF lnEl = 4
     EXIT
  ENDIF
  gnCount = gnCount + 1
ENDFOR
");
        // visits 1 (count), 2 (loop→skip), 3 (count), 4 (exit) ⇒ 2 counted.
        Assert.Equal(2m, h.Num("gnCount"));
    }

    [Fact]
    public void ForEach_ClosesWithNext()
    {
        using var h = new H();
        h.Run(@"
DIMENSION aItems(2)
aItems(1) = 5
aItems(2) = 7
gnT = 0
FOR EACH e IN aItems
  gnT = gnT + e
NEXT
");
        Assert.Equal(12m, h.Num("gnT"));
    }

    // ─────────────────────────── #IF / #IFDEF / #IFNDEF preprocessor ───────────────────────────

    [Fact]
    public void Ifdef_DefinedConstant_IncludesThenBranch()
    {
        using var h = new H();
        h.Run(@"
#DEFINE DEBUGMODE 1
#IFDEF DEBUGMODE
  gnA = 1
#ELSE
  gnA = 2
#ENDIF
");
        Assert.Equal(1m, h.Num("gnA"));
    }

    [Fact]
    public void Ifndef_UndefinedConstant_IncludesThenBranch()
    {
        using var h = new H();
        h.Run(@"
#IFNDEF RELEASEBUILD
  gnB = 10
#ELSE
  gnB = 20
#ENDIF
");
        Assert.Equal(10m, h.Num("gnB"));
    }

    [Fact]
    public void If_BooleanExpressionOverDefine_Includes()
    {
        using var h = new H();
        h.Run(@"
#DEFINE LEVEL 3
#IF LEVEL > 2
  gnC = 100
#ELSE
  gnC = 0
#ENDIF
");
        Assert.Equal(100m, h.Num("gnC"));
    }

    [Fact]
    public void Ifdef_ExcludedBlock_IsNotExecuted()
    {
        using var h = new H();
        // The excluded block must NOT run (it would set gnX = 999 if it did).
        h.Run(@"
gnX = 5
#IFDEF NOTDEFINEDANYWHERE
  gnX = 999
#ENDIF
");
        Assert.Equal(5m, h.Num("gnX"));
    }

    // ─────────────────────────── CLEAR MEMORY / CLEAR ALL ───────────────────────────

    [Fact]
    public void ClearMemory_ReleasesVariablesAndArrays()
    {
        using var h = new H();
        h.Run(@"
gnVal = 7
DIMENSION aArr(2)
aArr(1) = 3
");
        Assert.Equal("N", h.TypeOf("gnVal"));
        h.Run("CLEAR MEMORY");
        Assert.Equal("U", h.TypeOf("gnVal"));
        Assert.Equal("U", h.TypeOf("aArr"));
    }

    [Fact]
    public void ClearAll_ReleasesVariables()
    {
        using var h = new H();
        h.Run("gnV = 1");
        Assert.Equal("N", h.TypeOf("gnV"));
        h.Run("CLEAR ALL");
        Assert.Equal("U", h.TypeOf("gnV"));
    }
}
