using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpStoreSumTargetTests
{
    [Fact]
    public void Store_DottedMultipleBareAndMacroTargets_AssignsExpectedVariables()
    {
        using var session = new VfpSession();
        var interp = new VfpInterpreter(session);

        interp.Execute(
            "STORE 3 TO m.cnt\n" +
            "STORE 4 TO m.a, m.b\n" +
            "STORE 8 TO bare\n" +
            "lcv = 'macroTarget'\n" +
            "STORE 11 TO &lcv.");

        Assert.Equal(3m, interp.EvalExpression("cnt").AsNumber);
        Assert.Equal(4m, interp.EvalExpression("a").AsNumber);
        Assert.Equal(4m, interp.EvalExpression("b").AsNumber);
        Assert.Equal(8m, interp.EvalExpression("bare").AsNumber);
        Assert.Equal(11m, interp.EvalExpression("macroTarget").AsNumber);
    }

    [Fact]
    public void Store_IndexedTargets_ChangeOnlySelectedElements()
    {
        using var session = new VfpSession();
        var interp = new VfpInterpreter(session);

        interp.Execute("DIMENSION values(3)\nvalues = 1\nSTORE 7 TO values[2]\nSTORE 8 TO values(3)");

        Assert.Equal(1m, interp.EvalExpression("values(1)").AsNumber);
        Assert.Equal(7m, interp.EvalExpression("values(2)").AsNumber);
        Assert.Equal(8m, interp.EvalExpression("values(3)").AsNumber);
    }

    [Fact]
    public void Sum_DottedAndIndexedTargets_AssignsTotals()
    {
        using var dir = new MicroVfpTestSupport.TempDir("sum_targets");
        string dbf = dir.File("amounts.dbf");
        using (var writer = DbfWriter.Create(dbf, [new DbfColumnDef("AMOUNT", 'I', 4)]))
        {
            writer.AppendRecord(1);
            writer.AppendRecord(2);
            writer.AppendRecord(3);
        }

        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        var interp = new VfpInterpreter(session);
        interp.Execute(
            "USE amounts\n" +
            "DIMENSION totals(4)\n" +
            "totals = 99\n" +
            "i = 3\n" +
            "SUM amount TO m.total\n" +
            "SUM amount, amount * 2 TO totals[2], totals(i)");

        Assert.Equal(6m, interp.EvalExpression("total").AsNumber);
        Assert.Equal(99m, interp.EvalExpression("totals(1)").AsNumber);
        Assert.Equal(6m, interp.EvalExpression("totals(2)").AsNumber);
        Assert.Equal(12m, interp.EvalExpression("totals(3)").AsNumber);
        Assert.Equal(99m, interp.EvalExpression("totals(4)").AsNumber);
    }
}
