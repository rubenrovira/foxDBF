using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// SET DATASESSION — the oracle-pinned facts that hold for a FRESH interpreter (its ONLY data session is the
/// default public session #1). These survived the 5.14 change from single-session STUB to the REAL
/// multi-session model UNCHANGED, because none of them creates a second session: with only session #1 open,
/// <c>SET("DATASESSION")</c> is still NUMERIC 1, <c>SET DATASESSION TO 1</c> is still a no-op, and
/// <c>SET DATASESSION TO 0</c> / any not-yet-created id still raise VFP error 1540 "Session number is
/// invalid." The one pin whose MEANING changed is <c>SET DATASESSION TO 2</c>: under the stub it raised
/// unconditionally ("microVFP has no second session"); under the real model it raises HERE only because this
/// test never calls <see cref="VfpInterpreter.CreateDataSession"/> — once session 2 exists the switch
/// succeeds (see <c>MicroVfpDataSessionModelTests</c>). Semantics verified against the VFP9 runtime; the
/// full multi-session isolation model is exercised in <c>MicroVfpDataSessionModelTests</c> (public) and
/// pinned against VFP9 in <c>MicroVfpDataSessionModelOracleTests</c> (internal).
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
    [InlineData("SET DATASESSION TO 2")]   // raises because THIS interpreter never created session 2 (real model).
    public void SetDataSession_ToNonExistent_RaisesInvalidSession(string prg)
    {
        var interp = New();
        var ex = Assert.Throws<MicroVfpRuntimeException>(() => interp.Execute(prg));
        Assert.Contains("session number is invalid", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }
}
