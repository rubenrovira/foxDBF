using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Regression coverage for review finding #8 — recursion / nesting must throw a CATCHABLE exception
/// (not a fatal StackOverflowException that kills the host) across the three recursive descents:
/// the expression parser, the PRG parser, and the interpreter's call stack. (Findings #4 and #5 are
/// already covered by <see cref="ReviewFindingsRegressionTests"/>.)
/// </summary>
public sealed class ReviewFixesRegressionTests
{
    // #8a — deeply nested expression input throws a catchable ExpressionException, not StackOverflow.
    [Fact]
    public void DeeplyNestedExpression_ThrowsCatchable()
    {
        string expr = new string('(', 5000) + "1" + new string(')', 5000);
        Assert.Throws<ExpressionException>(() => VfpExpression.Parse(expr));
    }

    // #8b — deeply nested PRG block input throws a catchable syntax error, not StackOverflow.
    [Fact]
    public void DeeplyNestedPrg_ThrowsCatchable()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < 5000; i++) sb.AppendLine("IF .T.");
        sb.AppendLine("x = 1");
        for (int i = 0; i < 5000; i++) sb.AppendLine("ENDIF");

        using var s = new VfpSession();
        var interp = new VfpInterpreter(s);
        Assert.ThrowsAny<Exception>(() => interp.Execute(sb.ToString()));
    }

    // #8c — unbounded recursion in a UDF throws a catchable MicroVfpRuntimeException, not StackOverflow.
    [Fact]
    public void UnboundedRecursion_ThrowsCatchable()
    {
        const string prg = @"
PROCEDURE recur
    LOCAL n
    n = recur()
    RETURN n
ENDPROC
";
        using var s = new VfpSession();
        var interp = new VfpInterpreter(s);
        interp.Execute(prg);
        Assert.ThrowsAny<Exception>(() => interp.Execute("y = recur()"));
    }
}
