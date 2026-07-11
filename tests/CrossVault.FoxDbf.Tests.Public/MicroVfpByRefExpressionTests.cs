using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpByRefExpressionTests
{
    [Fact]
    public void ByRefFallback_PreservesAtSignsInsideStringLiterals()
    {
        const string prg = @"
FUNCTION echoByRef
    LPARAMETERS ignored, value
    RETURN value
ENDFUNC

FUNCTION byRefLiterals
    LOCAL result
    result = echoByRef(@result, 'mail@example.com') + '|' + ;
             echoByRef(@result, ""mail@example.com"") + '|' + ;
             echoByRef(@result, [mail@example.com])
    RETURN result
ENDFUNC
";

        using var session = new VfpSession();
        var interpreter = new VfpInterpreter(session);
        interpreter.Execute(prg);

        Assert.Equal(
            "mail@example.com|mail@example.com|mail@example.com",
            interpreter.Call("byRefLiterals").AsString);
    }
}
