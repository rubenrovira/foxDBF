using CrossVault.FoxDbf.MicroVfp;

namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpParserTests
{
    [Theory]
    [InlineData("PROCEDURE name(", 1)]
    [InlineData("PROCEDURE name(firstParameter", 1)]
    [InlineData("FUNCTION name(", 1)]
    [InlineData("FUNCTION name(firstParameter", 1)]
    [InlineData("\nFUNCTION name(", 2)]
    public void UnbalancedProcedureOrFunctionHeader_ThrowsTypedSyntaxError(string source, int expectedLine)
    {
        var ex = Assert.Throws<MicroVfpSyntaxException>(() => PrgParser.Parse(source));

        Assert.Equal(expectedLine, ex.Line);
        Assert.Contains("Unbalanced parameter list in PROCEDURE/FUNCTION header.", ex.Message);
    }

    [Fact]
    public void BalancedProcedureAndFunctionHeadersRemainValid()
    {
        var program = PrgParser.Parse("""
            PROCEDURE p(first, second)
            RETURN 'ok'
            ENDPROC
            FUNCTION f(value)
            RETURN value
            ENDFUNC
            """);

        Assert.Equal(2, program.Procedures.Count);
        Assert.Equal("p", program.Procedures[0].Name);
        Assert.Equal(new[] { "first", "second" }, program.Procedures[0].Parameters);
        Assert.False(program.Procedures[0].IsFunction);
        Assert.Equal("f", program.Procedures[1].Name);
        Assert.Equal(new[] { "value" }, program.Procedures[1].Parameters);
        Assert.True(program.Procedures[1].IsFunction);
    }

    [Fact]
    public void ParenthesesInsideHeaderLiteralDoNotCloseParameterList()
    {
        var program = PrgParser.Parse("PROCEDURE p(first, 'literal)', second)\nENDPROC");

        Assert.Single(program.Procedures);
        Assert.Equal(new[] { "first", "second" }, program.Procedures[0].Parameters);
    }
}
