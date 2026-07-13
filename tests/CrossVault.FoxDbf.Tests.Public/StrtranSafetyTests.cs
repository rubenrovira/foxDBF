using System;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>Focused STRTRAN parity, inference, and materialization safety coverage.</summary>
public sealed class StrtranSafetyTests
{
    [Theory]
    [InlineData("STRTRAN('abc','b')", "ac")]
    [InlineData("STRTRAN('abc','b','XYZ')", "aXYZc")]
    public void ExpressionRuntime_TwoAndThreeArguments_PreserveReplacementSemantics(
        string expression, string expected)
    {
        var parsed = VfpExpression.Parse(expression);

        Assert.Equal(expected, parsed.Evaluate(EmptyRow.Instance).AsString);
        Assert.Equal(expected, parsed.Compile()(EmptyRow.Instance).AsString);
    }

    [Fact]
    public void ExpressionInference_LiteralSearchAndReplacement_IsExact()
    {
        var type = VfpExpression.Parse("STRTRAN('abcabc','ab','XYZ')")
            .InferType(EmptySchema.Instance);

        Assert.Equal(VfpType.Character, type.Type);
        Assert.Equal(8, type.Length); // XYZcXYZc
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void ExpressionRuntime_UnsupportedFourToSixArguments_Throws(int arity)
    {
        string extras = string.Join(',', Enumerable.Repeat("1", arity - 3));
        var parsed = VfpExpression.Parse($"STRTRAN('abc','b','x',{extras})");

        Assert.Throws<NotSupportedException>(() => parsed.Evaluate(EmptyRow.Instance));
        Assert.Throws<NotSupportedException>(() => parsed.Compile()(EmptyRow.Instance));
    }

    [Fact]
    public void MicroVfpRuntime_TwoAndThreeArguments_PreserveReplacementSemantics()
    {
        using var session = new VfpSession();
        var interpreter = new VfpInterpreter(session);

        Assert.Equal("ac", interpreter.EvalExpression("STRTRAN('abc','b')").AsString);
        Assert.Equal("aXYZc", interpreter.EvalExpression("STRTRAN('abc','b','XYZ')").AsString);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void MicroVfpRuntime_UnsupportedFourToSixArguments_Throws(int arity)
    {
        string extras = string.Join(',', Enumerable.Repeat("1", arity - 3));
        using var session = new VfpSession();
        var interpreter = new VfpInterpreter(session);

        Assert.Throws<MicroVfpRuntimeException>(() =>
            interpreter.Execute($"=STRTRAN('abc','b','x',{extras})"));
    }

    [Fact]
    public void AdoReader_StrtranColumnSize_IsPositiveAndExactForLiteralExpression()
    {
        using var db = new Data.PersonDb();
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT STRTRAN('abcabc','ab','XYZ') AS expanded FROM person";

        using var reader = command.ExecuteReader();
        var schema = reader.GetSchemaTable();
        Assert.NotNull(schema);
        Assert.Equal(8, Convert.ToInt32(schema!.Rows[0]["ColumnSize"]));

        Assert.True(reader.Read());
        Assert.Equal("XYZcXYZc", reader.GetString(0));
    }

    [Fact]
    public void SingleAndJoinInference_StrtranWithLiteralReplacement_IsConservativeAndPositive()
    {
        using var dir = new SqlTestSupport.TempDir();
        SqlTestSupport.CreatePersonTable(dir.File("person.dbf"));
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);

        var single = session.Execute(
            "SELECT STRTRAN(name,'S','XYZ') AS expanded FROM person")!;
        var join = session.Execute(
            "SELECT STRTRAN(p.name,'S','XYZ') AS expanded " +
            "FROM person p INNER JOIN person q ON p.id = q.id")!;

        Assert.Equal(VfpType.Character, ToVfpType(single.Columns[0].VfpType));
        Assert.Equal(VfpType.Character, ToVfpType(join.Columns[0].VfpType));
        Assert.Equal(60, single.Columns[0].Length); // C(20), every char could be replaced by XYZ.
        Assert.Equal(60, join.Columns[0].Length);
    }

    [Theory]
    [InlineData("CURSOR", "strtran_cursor_wide")]
    [InlineData("TABLE", "strtran_table_wide")]
    public void Materialization_ExpandingResultOverTenCharacters_IsNotTruncated(
        string targetKind, string targetName)
    {
        using var dir = new SqlTestSupport.TempDir();
        SqlTestSupport.CreatePersonTable(dir.File("person.dbf"));
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);

        var result = session.Execute(
            $"SELECT STRTRAN('aaaaaaaaaa','a','xx') AS expanded FROM person " +
            $"INTO {targetKind} {targetName}")!;

        Assert.Equal(9, result.AffectedRecords);
        Assert.Empty(result.Rows);

        if (targetKind == "CURSOR")
        {
            var cursor = session.Execute($"SELECT expanded FROM {targetName}")!;
            Assert.Equal(20, cursor.Columns[0].Length);
            Assert.Equal(9, cursor.Rows.Count());
            Assert.All(cursor.Rows, row => Assert.Equal("xx".PadRight(20, 'x'), row[0]));
        }
        else
        {
            string path = dir.File(targetName + ".dbf");
            Assert.True(File.Exists(path));
            using var table = DbfTable.Open(path);
            Assert.Equal(20, table.Columns.Single().Length);
            Assert.All(table.EnumerateAll(includeDeleted: false),
                row => Assert.Equal("xx".PadRight(20, 'x'), row["EXPANDED"]));
        }
    }

    [Theory]
    [InlineData("CURSOR", "strtran_too_wide_cursor")]
    [InlineData("TABLE", "strtran_too_wide_table")]
    public void MaterializationOverVfpCharacterLimit_FailsBeforeTargetCreation(
        string targetKind, string targetName)
    {
        using var dir = new SqlTestSupport.TempDir();
        SqlTestSupport.CreatePersonTable(dir.File("person.dbf"));
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);

        var error = Assert.Throws<FoxDbfSqlException>(() => session.Execute(
            $"SELECT STRTRAN(REPLICATE('a',300),'a','bb') AS expanded FROM person " +
            $"INTO {targetKind} {targetName}"));

        Assert.Contains("254", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(dir.File(targetName + ".dbf")));
        if (targetKind == "CURSOR")
            Assert.ThrowsAny<Exception>(() => session.Execute($"SELECT * FROM {targetName}"));
    }

    [Fact]
    public void MemoSourceIntoMaterialization_FailsLoudlyWithoutC254OrMemoPromotion()
    {
        using var dir = new SqlTestSupport.TempDir();
        string source = dir.File("memo_source.dbf");
        using (var writer = DbfWriter.Create(source, new[]
        {
            new DbfColumnDef("ID", 'I'),
            new DbfColumnDef("NOTE", 'M', 4),
        }))
        {
            writer.AppendRecord(1, "x memo value");
            writer.Flush();
        }

        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);

        var direct = session.Execute("SELECT STRTRAN(note,'x','y') AS expanded FROM memo_source")!;
        Assert.Equal(int.MaxValue, direct.Columns[0].Length);
        Assert.Equal("y memo value", direct.Rows.Single()[0]);

        var error = Assert.Throws<FoxDbfSqlException>(() => session.Execute(
            "SELECT STRTRAN(note,'x','y') AS expanded FROM memo_source INTO CURSOR memo_out"));
        Assert.Contains("254", error.Message, StringComparison.Ordinal);
        Assert.ThrowsAny<Exception>(() => session.Execute("SELECT * FROM memo_out"));
    }

    [Theory]
    [InlineData("note+note", "memo_concat")]
    [InlineData("REPLICATE(note,2)", "memo_replicate")]
    public void MemoCharacterExpansion_DirectSelectKeepsValueAndUnboundedInference(
        string expression, string alias)
    {
        using var dir = new SqlTestSupport.TempDir();
        CreateMemoSource(dir.File("memo_source.dbf"));

        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);

        var result = session.Execute(
            $"SELECT {expression} AS {alias} FROM memo_source")!;

        Assert.Equal(int.MaxValue, result.Columns[0].Length);
        Assert.Equal("x memo valuex memo value", result.Rows.Single()[0]);
    }

    [Theory]
    [InlineData("note+note", "memo_concat_cursor", "CURSOR")]
    [InlineData("note+note", "memo_concat_table", "TABLE")]
    [InlineData("REPLICATE(note,2)", "memo_replicate_cursor", "CURSOR")]
    [InlineData("REPLICATE(note,2)", "memo_replicate_table", "TABLE")]
    public void MemoCharacterExpansion_IntoMaterializationFailsBeforeTargetCreation(
        string expression, string targetName, string targetKind)
    {
        using var dir = new SqlTestSupport.TempDir();
        CreateMemoSource(dir.File("memo_source.dbf"));

        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);

        var error = Assert.Throws<FoxDbfSqlException>(() => session.Execute(
            $"SELECT {expression} AS expanded FROM memo_source INTO {targetKind} {targetName}"));

        Assert.Contains("254", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(dir.File(targetName + ".dbf")));
        if (targetKind == "CURSOR")
            Assert.ThrowsAny<Exception>(() => session.Execute($"SELECT * FROM {targetName}"));
    }

    [Theory]
    [InlineData("'ab' + 'cde'", 5)]
    [InlineData("'ab' - 'cde'", 5)]
    public void FiniteCharacterBinaryInference_RemainsExact(string expression, int expectedLength)
    {
        var type = VfpExpression.Parse(expression).InferType(EmptySchema.Instance);

        Assert.Equal(VfpType.Character, type.Type);
        Assert.Equal(expectedLength, type.Length);
    }

    [Theory]
    [InlineData("REPLICATE(NAME, ID)")]
    [InlineData("PADL(NAME, ID)")]
    [InlineData("PADR(NAME, ID)")]
    [InlineData("PADC(NAME, ID)")]
    [InlineData("SPACE(ID)")]
    [InlineData("STR(ID, ID)")]
    [InlineData("STRZERO(ID, ID)")]
    public void DynamicStringWidthArguments_InferAsUnbounded(string expression)
    {
        var type = VfpExpression.Parse(expression).InferType(DynamicWidthSchema.Instance);

        Assert.Equal(VfpType.Character, type.Type);
        Assert.Equal(int.MaxValue, type.Length);
    }

    [Fact]
    public void NestedStrtran_WithDynamicPadWidth_PropagatesUnboundedLength()
    {
        var type = VfpExpression.Parse("STRTRAN(PADR(NAME, ID),'S','XYZ')")
            .InferType(DynamicWidthSchema.Instance);

        Assert.Equal(VfpType.Character, type.Type);
        Assert.Equal(int.MaxValue, type.Length);
    }

    [Fact]
    public void Stuff_WithC20SourceAndReplacement_UsesSaturatingCombinedWidth()
    {
        var type = VfpExpression.Parse("STUFF(NAME,1,1,NAME)")
            .InferType(DynamicWidthSchema.Instance);

        Assert.Equal(VfpType.Character, type.Type);
        Assert.Equal(40, type.Length);
    }

    [Theory]
    [InlineData("REPLICATE(NAME, ID)", "dynamic_replicate")]
    [InlineData("PADL(NAME, ID)", "dynamic_padl")]
    [InlineData("PADR(NAME, ID)", "dynamic_padr")]
    [InlineData("PADC(NAME, ID)", "dynamic_padc")]
    [InlineData("SPACE(ID)", "dynamic_space")]
    [InlineData("STR(ID, ID)", "dynamic_str")]
    [InlineData("STRZERO(ID, ID)", "dynamic_strzero")]
    [InlineData("STRTRAN(PADR(NAME, ID),'S','XYZ')", "dynamic_nested_strtran")]
    public void DynamicStringWidthIntoMaterialization_FailsBeforeTargetCreation(
        string expression, string targetName)
    {
        using var dir = new SqlTestSupport.TempDir();
        SqlTestSupport.CreatePersonTable(dir.File("person.dbf"));
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);

        foreach (string targetKind in new[] { "CURSOR", "TABLE" })
        {
            string scopedTarget = targetName + "_" + targetKind.ToLowerInvariant();
            var error = Assert.Throws<FoxDbfSqlException>(() => session.Execute(
                $"SELECT {expression} AS expanded FROM person " +
                $"INTO {targetKind} {scopedTarget}"));

            Assert.Contains("254", error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(dir.File(scopedTarget + ".dbf")));
            if (targetKind == "CURSOR")
                Assert.ThrowsAny<Exception>(() => session.Execute($"SELECT * FROM {scopedTarget}"));
        }
    }

    [Fact]
    public void Stuff_WithMemoSource_PropagatesUnboundedLengthAndRejectsMaterialization()
    {
        using var dir = new SqlTestSupport.TempDir();
        CreateMemoSource(dir.File("memo_source.dbf"));
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);

        var direct = session.Execute(
            "SELECT STUFF(note,1,1,'x') AS expanded FROM memo_source")!;
        Assert.Equal(int.MaxValue, direct.Columns[0].Length);
        Assert.Equal("x memo value", direct.Rows.Single()[0]);

        foreach (string targetKind in new[] { "CURSOR", "TABLE" })
        {
            string targetName = "stuff_memo_" + targetKind.ToLowerInvariant();
            var error = Assert.Throws<FoxDbfSqlException>(() => session.Execute(
                $"SELECT STUFF(note,1,1,'x') AS expanded FROM memo_source " +
                $"INTO {targetKind} {targetName}"));

            Assert.Contains("254", error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(dir.File(targetName + ".dbf")));
            if (targetKind == "CURSOR")
                Assert.ThrowsAny<Exception>(() => session.Execute($"SELECT * FROM {targetName}"));
        }
    }

    [Theory]
    [InlineData("REPLICATE('ab', 3)", 6)]
    [InlineData("REPLICATE('ab', -3)", 0)]
    [InlineData("PADL('abcdef', 3)", 3)]
    [InlineData("PADL('abcdef', -3)", 0)]
    [InlineData("PADR('abcdef', 3)", 3)]
    [InlineData("PADR('abcdef', -3)", 0)]
    [InlineData("PADC('abcdef', 3)", 3)]
    [InlineData("PADC('abcdef', -3)", 0)]
    [InlineData("SPACE(-3)", 0)]
    [InlineData("STR(12, 4)", 4)]
    [InlineData("STR(12, -4)", 0)]
    [InlineData("STRZERO(12, 4)", 4)]
    [InlineData("STRZERO(12, -4)", 0)]
    [InlineData("STUFF('abc', 2, 1, 'xyz')", 5)]
    public void FiniteStringWidths_AreNonNegativeAndExact(string expression, int expectedLength)
    {
        var type = VfpExpression.Parse(expression).InferType(EmptySchema.Instance);

        Assert.Equal(VfpType.Character, type.Type);
        Assert.Equal(expectedLength, type.Length);
    }

    [Fact]
    public void HugeFiniteStringWidth_SaturatesAtIntMax()
    {
        var type = VfpExpression.Parse("REPLICATE('ab', 99999999999)")
            .InferType(EmptySchema.Instance);

        Assert.Equal(VfpType.Character, type.Type);
        Assert.Equal(int.MaxValue, type.Length);
    }

    private static void CreateMemoSource(string path)
    {
        using var writer = DbfWriter.Create(path, new[]
        {
            new DbfColumnDef("ID", 'I'),
            new DbfColumnDef("NOTE", 'M', 4),
        });
        writer.AppendRecord(1, "x memo value");
        writer.Flush();
    }

    private sealed class DynamicWidthSchema : ISchema
    {
        public static readonly DynamicWidthSchema Instance = new();

        public bool TryGetColumn(string name, out char type, out int length, out int decimals)
        {
            switch (name.ToUpperInvariant())
            {
                case "NAME":
                    type = 'C'; length = 20; decimals = 0; return true;
                case "ID":
                    type = 'N'; length = 10; decimals = 0; return true;
                default:
                    type = '\0'; length = 0; decimals = 0; return false;
            }
        }
    }

    private static VfpType ToVfpType(char type) => char.ToUpperInvariant(type) switch
    {
        'C' => VfpType.Character,
        _ => VfpType.Unknown,
    };

    private sealed class EmptyRow : IRowContext
    {
        public static readonly EmptyRow Instance = new();
        public int RecNo => 0;
        public int RecCount => 0;
        public bool Deleted => false;
        public object? GetField(string name) => null;
    }

    private sealed class EmptySchema : ISchema
    {
        public static readonly EmptySchema Instance = new();
        public bool TryGetColumn(string name, out char type, out int length, out int decimals)
        {
            type = '\0';
            length = 0;
            decimals = 0;
            return false;
        }
    }
}
