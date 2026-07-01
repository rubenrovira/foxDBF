using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP P1 gap #4 — SET DATASESSION (single-session stub). microVFP has no form model, so private
/// data sessions (which in VFP only come from forms with DataSession=2) never exist: only the default
/// public session #1 is available. Semantics verified against the VFP9 runtime: <c>SET("DATASESSION")</c> is
/// NUMERIC 1; <c>SET DATASESSION TO 1</c> is a no-op; <c>SET DATASESSION TO 0</c> and any other id both
/// raise VFP error 1540 "Session number is invalid." A full multi-session work-area registry is the
/// deferred P3 architecture item.
/// </summary>
public sealed class MicroVfpDataSessionTests
{
    private static VfpInterpreter New() => new(new VfpSession());

    [Fact]
    public void SetDataSession_Getter_IsNumericOne()
    {
        var interp = New();
        var v = interp.EvalExpression("SET('DATASESSION')");
        Assert.Equal(VfpType.Numeric, v.Type);   // NUMERIC, not Character (matches VFP9)
        Assert.Equal(1m, v.AsNumber);
    }

    [Fact]
    public void SetDataSession_To1_IsNoOp()
    {
        var interp = New();
        interp.Execute("SET DATASESSION TO 1");   // must not throw
        Assert.Equal(1m, interp.EvalExpression("SET('DATASESSION')").AsNumber);
    }

    [Theory]
    [InlineData("SET DATASESSION TO 0")]
    [InlineData("SET DATASESSION TO 5")]
    [InlineData("SET DATASESSION TO 2")]
    public void SetDataSession_ToNonExistent_RaisesInvalidSession(string prg)
    {
        var interp = New();
        var ex = Assert.Throws<MicroVfpRuntimeException>(() => interp.Execute(prg));
        Assert.Contains("session number is invalid", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }
}
