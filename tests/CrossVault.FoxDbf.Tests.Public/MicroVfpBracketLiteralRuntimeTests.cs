using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpBracketLiteralRuntimeTests
{
    [Fact]
    public void Assignment_BracketLiteral_PreservesInlineAmpersands()
    {
        using var session = new VfpSession();
        var interp = new VfpInterpreter(session);

        interp.Execute("x = [50% && rising]");

        Assert.Equal("50% && rising", interp.EvalExpression("x").AsString);
    }
}
