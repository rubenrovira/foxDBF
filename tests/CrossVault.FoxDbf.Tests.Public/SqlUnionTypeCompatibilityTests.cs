using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

public sealed class SqlUnionTypeCompatibilityTests : IDisposable
{
    private readonly SqlTestSupport.TempDir _dir = new();

    public SqlUnionTypeCompatibilityTests()
    {
        Write("dates", new DbfColumnDef("DVAL", 'D', 8), new DateOnly(2024, 1, 2));
        Write("times", new DbfColumnDef("TVAL", 'T', 8), new DateTime(2024, 1, 2));
        Write("chars2", new DbfColumnDef("CVAL", 'C', 2), "AB");
        Write("chars6", new DbfColumnDef("CVAL", 'C', 6), "ABCDEF");
        Write("logical", new DbfColumnDef("LVAL", 'L', 1), true);
    }

    public void Dispose() => _dir.Dispose();

    private void Write(string name, DbfColumnDef column, object value)
    {
        using var writer = DbfWriter.Create(Path.Combine(_dir.Path, name + ".dbf"), [column],
            new DbfCreateOptions { Overwrite = true });
        writer.AppendRecord(value);
        writer.Flush();
    }

    private SqlResult Run(string sql)
    {
        using var session = new VfpSession();
        session.OpenDirectory(_dir.Path);
        return session.Execute(sql)!;
    }

    [Theory]
    [InlineData("SELECT DVAL FROM dates UNION SELECT TVAL FROM times")]
    [InlineData("SELECT TVAL FROM times UNION SELECT DVAL FROM dates")]
    public void Union_DateAndDateTime_CoercesBeforeDistinct(string sql)
    {
        SqlResult result = Run(sql);
        Assert.Equal('T', result.Columns[0].VfpType);
        Assert.Equal(8, result.Columns[0].Length);
        Assert.Equal(typeof(DateTime), result.Columns[0].ClrType);
        object?[] row = Assert.Single(result.Rows);
        Assert.Equal(new DateTime(2024, 1, 2, 0, 0, 0), Assert.IsType<DateTime>(row[0]));
    }

    [Theory]
    [InlineData("SELECT c.CVAL AS C_OUT FROM chars2 c UNION SELECT d.DVAL AS D_OUT FROM dates d", "SELECTs are not UNION compatible. Fields C_OUT and D_OUT are incompatible.")]
    [InlineData("SELECT d.DVAL AS D_OUT FROM dates d UNION SELECT c.CVAL AS C_OUT FROM chars2 c", "SELECTs are not UNION compatible. Fields D_OUT and C_OUT are incompatible.")]
    [InlineData("SELECT c.CVAL AS C_OUT FROM chars2 c UNION SELECT l.LVAL AS L_OUT FROM logical l", "SELECTs are not UNION compatible. Fields C_OUT and L_OUT are incompatible.")]
    [InlineData("SELECT l.LVAL AS L_OUT FROM logical l UNION SELECT c.CVAL AS C_OUT FROM chars2 c", "SELECTs are not UNION compatible. Fields L_OUT and C_OUT are incompatible.")]
    public void Union_IncompatibleTypes_ThrowVfp1851WithActualNames(string sql, string expectedMessage)
    {
        var error = Assert.Throws<FoxDbfSqlException>(() => Run(sql));
        Assert.Equal(1851, error.VfpErrorNumber);
        Assert.Equal(expectedMessage, error.Message);
    }

    [Theory]
    [InlineData("SELECT CVAL FROM chars2 UNION ALL SELECT CVAL FROM chars6")]
    [InlineData("SELECT CVAL FROM chars6 UNION ALL SELECT CVAL FROM chars2")]
    public void Union_CharacterColumns_WidenToSixInBothDirections(string sql)
    {
        SqlResult result = Run(sql);
        Assert.Equal('C', result.Columns[0].VfpType);
        Assert.Equal(6, result.Columns[0].Length);
        Assert.All(result.Rows, row => Assert.IsType<string>(row[0]));
    }

    [Theory]
    [InlineData("SELECT 'AB' AS X FROM chars2 UNION ALL SELECT 'ABCDEF' AS X FROM chars6")]
    [InlineData("SELECT 'ABCDEF' AS X FROM chars6 UNION ALL SELECT 'AB' AS X FROM chars2")]
    public void Union_CharacterLiterals_WidenToSixInBothDirections(string sql)
        => Assert.Equal(6, Run(sql).Columns[0].Length);

    [Fact]
    public void Union_DistinctThenAll_FoldsLeftAssociativelyAfterCoercion()
    {
        SqlResult result = Run(
            "SELECT DVAL FROM dates UNION SELECT TVAL FROM times UNION ALL SELECT DVAL FROM dates");
        Assert.Equal(2, result.Rows.Count());
        Assert.All(result.Rows, row => Assert.IsType<DateTime>(row[0]));
        Assert.All(result.Rows,
            row => Assert.Equal(new DateTime(2024, 1, 2), Assert.IsType<DateTime>(row[0])));
    }
}
