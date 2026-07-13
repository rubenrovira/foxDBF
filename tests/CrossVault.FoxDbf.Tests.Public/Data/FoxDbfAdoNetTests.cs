using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using Xunit;

namespace CrossVault.FoxDbf.Tests.Data;

/// <summary>
/// Phase-3 ADO.NET provider acceptance tests (TDD RED). Each test drives the public
/// <see cref="DbConnection"/> / <see cref="DbCommand"/> / <see cref="DbDataReader"/> surface against a
/// temp COPY of the canonical PERSON table. They fail until the provider is implemented.
/// </summary>
public sealed class FoxDbfAdoNetTests
{
    // ---- (1) Raw round-trip: reader getters + schema --------------------------------------

    [Fact]
    public void RoundTrip_Reader_TypedGetters_And_Metadata()
    {
        using var db = new PersonDb();
        using var conn = db.Open("Collate=machine");
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, city, amount, hired, active FROM person WHERE amount > 100 ORDER BY id";

        using var r = cmd.ExecuteReader();

        Assert.True(r.HasRows);
        Assert.Equal(6, r.FieldCount);

        // Column metadata is deterministic from the DBF field types.
        Assert.Equal("ID", r.GetName(0), ignoreCase: true);
        Assert.Equal(typeof(int), r.GetFieldType(0));
        Assert.Equal(typeof(string), r.GetFieldType(1));
        Assert.Equal(typeof(decimal), r.GetFieldType(3));
        Assert.Equal(typeof(DateTime), r.GetFieldType(4));   // D -> DateTime (DateOnly via GetFieldValue<DateOnly>)
        Assert.Equal(typeof(bool), r.GetFieldType(5));
        Assert.Equal("I", r.GetDataTypeName(0));
        Assert.Equal("C", r.GetDataTypeName(1));
        Assert.Equal("N", r.GetDataTypeName(3));
        Assert.Equal("D", r.GetDataTypeName(4));
        Assert.Equal("L", r.GetDataTypeName(5));

        // GetOrdinal is case-insensitive.
        Assert.Equal(1, r.GetOrdinal("NAME"));
        Assert.Equal(1, r.GetOrdinal("name"));

        var ids = new List<int>();
        while (r.Read())
        {
            int id = r.GetInt32(0);
            ids.Add(id);

            Assert.False(r.IsDBNull(1));
            Assert.IsType<string>(r.GetValue(1));
            _ = r.GetString(1);
            _ = r.GetDecimal(3);
            Assert.Equal(r.GetDateTime(4), r.GetFieldValue<DateOnly>(4).ToDateTime(TimeOnly.MinValue));
            _ = r.GetBoolean(5);

            // indexer by name + by ordinal agree.
            Assert.Equal(r.GetValue(0), r[0]);
            Assert.Equal(r[0], r["id"]);
        }

        // amount > 100, deleted row 7 excluded (SET DELETED ON default), ordered by id.
        Assert.Equal(new[] { 2, 3, 4, 5, 6, 8, 10 }, ids);
    }

    [Fact]
    public void Atc_Filter_MatchesCaseInsensitiveNamesInPersonDb()
    {
        using var db = new PersonDb();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM person WHERE ATC('sm', name) = 1 ORDER BY id";

        Assert.Equal(new[] { 1, 2, 3 }, ReadInts(cmd));
    }

    [Fact]
    public void GetSchemaTable_Describes_Columns()
    {
        using var db = new PersonDb();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name FROM person";
        using var r = cmd.ExecuteReader();

        var schema = r.GetSchemaTable();
        Assert.NotNull(schema);
        Assert.Equal(2, schema!.Rows.Count);
        Assert.Equal("ID", ((string)schema.Rows[0]["ColumnName"]).ToUpperInvariant());
    }

    [Fact]
    public void Reader_GetBytes_And_GetChars_ClampAtAndPastEndOffsets()
    {
        using var db = new PersonDb();
        using var conn = db.Open();
        var result = new SqlResult(
            new[]
            {
                new SqlColumn("blob", 'Q', 3, 0, typeof(byte[])),
                new SqlColumn("text", 'C', 3, 0, typeof(string)),
            },
            new[] { new object?[] { new byte[] { 1, 2, 3 }, "abc" } });
        using var r = new FoxDbfDataReader(result, conn, CommandBehavior.Default);
        Assert.True(r.Read());

        var bytes = new byte[] { 9, 9 };
        Assert.Equal(0, r.GetBytes(0, 3, bytes, 0, bytes.Length));
        Assert.Equal(new byte[] { 9, 9 }, bytes);
        Assert.Equal(0, r.GetBytes(0, 4, bytes, 0, bytes.Length));
        Assert.Equal(new byte[] { 9, 9 }, bytes);

        var chars = new[] { 'x', 'y' };
        Assert.Equal(0, r.GetChars(1, 3, chars, 0, chars.Length));
        Assert.Equal(new[] { 'x', 'y' }, chars);
        Assert.Equal(0, r.GetChars(1, 4, chars, 0, chars.Length));
        Assert.Equal(new[] { 'x', 'y' }, chars);
        Assert.Equal(0, r.GetChars(1, (long)int.MaxValue + 1, chars, 0, chars.Length));
        Assert.Equal(new[] { 'x', 'y' }, chars);
    }

    [Fact]
    public void Reader_Enumeration_ReturnsDbDataRecord_WithNormalizedValues()
    {
        using var db = new PersonDb();
        using var conn = db.Open();
        var result = new SqlResult(
            new[]
            {
                new SqlColumn("nullable", 'C', 3, 0, typeof(string)),
                new SqlColumn("hired", 'D', 8, 0, typeof(DateOnly)),
            },
            new[] { new object?[] { null, new DateOnly(2024, 2, 29) } });
        using var reader = new FoxDbfDataReader(result, conn, CommandBehavior.Default);

        var records = reader.Cast<DbDataRecord>().ToList();

        var dataRecord = Assert.Single(records);
        Assert.Same(DBNull.Value, dataRecord.GetValue(0));
        Assert.Equal(new DateTime(2024, 2, 29), dataRecord.GetValue(1));
        Assert.IsType<DateTime>(dataRecord.GetValue(1));
        Assert.IsNotType<object[]>(dataRecord);
    }

    // ---- (2) ExecuteScalar + ExecuteNonQuery (DML) ----------------------------------------

    [Fact]
    public void ExecuteScalar_ReturnsFirstColumnFirstRow_OrNull()
    {
        using var db = new PersonDb();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT name FROM person WHERE id = 3";
        Assert.Equal("Smithson", ((string)cmd.ExecuteScalar()!).TrimEnd());

        cmd.CommandText = "SELECT name FROM person WHERE id = 999";
        Assert.Null(cmd.ExecuteScalar());
    }

    [Fact]
    public void ExecuteScalar_DistinguishesSqlNullFromNoRows()
    {
        using var db = new PersonDb();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "SELECT MAX(amount) FROM person WHERE id = 999";
        Assert.Same(DBNull.Value, cmd.ExecuteScalar());

        cmd.CommandText = "SELECT amount FROM person WHERE id = 999";
        Assert.Null(cmd.ExecuteScalar());
    }

    [Fact]
    public void ExecuteNonQuery_Insert_Update_Delete_ReturnAffected_AndAreVisible()
    {
        using var db = new PersonDb();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "INSERT INTO person (id, name, city, amount) VALUES (11, 'Newman', 'Bonn', 500.00)";
        Assert.Equal(1, cmd.ExecuteNonQuery());

        cmd.CommandText = "SELECT amount FROM person WHERE id = 11";
        Assert.Equal(500.00m, Convert.ToDecimal(cmd.ExecuteScalar()));

        cmd.CommandText = "UPDATE person SET city = 'Cologne' WHERE id = 1";
        Assert.Equal(1, cmd.ExecuteNonQuery());

        cmd.CommandText = "SELECT city FROM person WHERE id = 1";
        Assert.Equal("Cologne", ((string)cmd.ExecuteScalar()!).TrimEnd());

        cmd.CommandText = "DELETE FROM person WHERE id = 11";
        Assert.Equal(1, cmd.ExecuteNonQuery());

        cmd.CommandText = "SELECT id FROM person WHERE id = 11";
        Assert.Null(cmd.ExecuteScalar());   // soft-deleted -> excluded under SET DELETED ON.
    }

    // ---- (3) Parameters: positional + named + AddWithValue + no injection -----------------

    [Fact]
    public void Parameters_Positional_Question_Mark()
    {
        using var db = new PersonDb();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM person WHERE city = ? AND amount >= ?";

        var p1 = cmd.CreateParameter(); p1.Value = "Berlin"; cmd.Parameters.Add(p1);
        var p2 = cmd.CreateParameter(); p2.Value = 150m; cmd.Parameters.Add(p2);

        Assert.Equal(new[] { 2, 4 }, ReadInts(cmd));   // Berlin & amount>=150: id 2 (200), id 4 (150)
    }

    [Fact]
    public void Parameters_Named_At_Sign_And_AddWithValue()
    {
        using var db = new PersonDb();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM person WHERE city = @c";
        ((FoxDbfParameterCollection)cmd.Parameters).AddWithValue("@c", "Munich");

        Assert.Equal(new[] { 3, 5, 8 }, ReadInts(cmd));   // Munich rows.
    }

    [Fact]
    public void Parameters_StringWithEmbeddedQuote_DoesNotInject()
    {
        using var db = new PersonDb();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM person WHERE name = @n";
        // A classic injection payload + an embedded quote: it must be treated as a literal value,
        // matching no row, and must NOT throw or widen the result set.
        ((FoxDbfParameterCollection)cmd.Parameters).AddWithValue("@n", "x' OR '1'='1");

        Assert.Empty(ReadInts(cmd));
    }

    [Theory]
    [InlineData("SELECT id FROM person WHERE '?' = '?' AND id = ?", 1)]
    [InlineData("SELECT id FROM person WHERE [?] = [?] AND id = ?", 2)]
    [InlineData("SELECT id FROM person WHERE id = ? && ? inside comment\n", 3)]
    public void PositionalMarkers_InsideStringBracketAndComment_AreIgnored(string sql, int id)
    {
        using var db = new PersonDb();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var parameter = cmd.CreateParameter();
        parameter.Value = id;
        cmd.Parameters.Add(parameter);

        Assert.Equal(id, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    [Fact]
    public void PositionalMarker_InsideDateLiteral_IsIgnoredByBinder()
    {
        using var db = new PersonDb();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM person WHERE hired = {^2020-?1-01}";
        var parameter = cmd.CreateParameter();
        parameter.Value = 1;
        cmd.Parameters.Add(parameter);

        var error = Assert.Throws<FoxDbfException>(() => cmd.ExecuteScalar());
        Assert.Contains("Too many positional", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PositionalParameterArityErrors_AreTypedAndActionable()
    {
        using var db = new PersonDb();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM person WHERE id = ?";
        Assert.Contains("Not enough positional",
            Assert.Throws<FoxDbfException>(() => cmd.ExecuteScalar()).Message,
            StringComparison.OrdinalIgnoreCase);

        var first = cmd.CreateParameter(); first.Value = 1; cmd.Parameters.Add(first);
        var extra = cmd.CreateParameter(); extra.Value = 2; cmd.Parameters.Add(extra);
        Assert.Contains("Too many positional",
            Assert.Throws<FoxDbfException>(() => cmd.ExecuteScalar()).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParameterLiteralEdges_AreSafeAndPreserveDateTimeType()
    {
        const string allDelimiters = "left'center\"right]tail";
        string literal = FoxDbfCommand.ToVfpLiteral(allDelimiters);
        using var session = new VfpSession();
        var interpreter = new VfpInterpreter(session);
        Assert.Contains("CHR(39)", literal, StringComparison.Ordinal);
        Assert.Equal(allDelimiters, interpreter.EvalExpression(literal).AsString);

        Assert.Throws<NotSupportedException>(() => FoxDbfCommand.ToVfpLiteral(new byte[] { 1, 2 }));
        Assert.Throws<NotSupportedException>(() => FoxDbfCommand.ToVfpLiteral("a\0b"));
        Assert.Equal("{^2020-01-02 00:00:00}",
            FoxDbfCommand.ToVfpLiteral(new DateTime(2020, 1, 2, 0, 0, 0)));
        Assert.Equal("{^2020-01-02}", FoxDbfCommand.ToVfpLiteral(new DateOnly(2020, 1, 2)));
    }

    // ---- (4) Work-area commands via ExecuteNonQuery, then a SELECT against the alias -------

    [Fact]
    public void UseCommand_OpensAlias_ThenSelectAgainstIt()
    {
        using var db = new PersonDb();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "USE person ALIAS p";
        Assert.Equal(-1, cmd.ExecuteNonQuery());   // non-row command -> -1

        cmd.CommandText = "SELECT id FROM p WHERE id = 1";
        Assert.Equal(new[] { 1 }, ReadInts(cmd));
    }

    // ---- (5) CommandBehavior ---------------------------------------------------------------

    [Fact]
    public void SchemaOnly_ReturnsColumns_NoRows()
    {
        using var db = new PersonDb();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name FROM person";

        using var r = cmd.ExecuteReader(CommandBehavior.SchemaOnly);
        Assert.Equal(2, r.FieldCount);
        Assert.False(r.Read());
    }

    [Fact]
    public void CloseConnection_Behavior_ClosesConnection_OnReaderDispose()
    {
        using var db = new PersonDb();
        var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM person";

        var r = cmd.ExecuteReader(CommandBehavior.CloseConnection);
        r.Read();
        r.Dispose();

        Assert.Equal(ConnectionState.Closed, conn.State);
    }

    [Fact]
    public void SecondReader_OnBusyConnection_Throws()
    {
        using var db = new PersonDb();
        using var conn = db.Open();
        using var cmd1 = conn.CreateCommand();
        cmd1.CommandText = "SELECT id FROM person";
        using var r1 = cmd1.ExecuteReader();
        r1.Read();

        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "SELECT id FROM person";
        Assert.Throws<InvalidOperationException>(() => cmd2.ExecuteReader());
    }

    // ---- (6) DbProviderFactories registration ---------------------------------------------

    [Fact]
    public void ProviderFactory_RegisterAndResolve_CreatesWorkingConnection()
    {
        DbProviderFactories.RegisterFactory("CrossVault.FoxDbf", FoxDbfProviderFactory.Instance);
        var factory = DbProviderFactories.GetFactory("CrossVault.FoxDbf");

        using var db = new PersonDb();
        using var conn = factory.CreateConnection();
        Assert.NotNull(conn);
        conn!.ConnectionString = db.ConnectionString();
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM person WHERE id = 3";
        Assert.Equal(3, Convert.ToInt32(cmd.ExecuteScalar()));
    }

    // ---- (8) Accelerator=Highlike yields the same rows as None ----------------------------

    [Fact]
    public void Accelerator_Highlike_SameRows_AsNone()
    {
        using var db = new PersonDb();

        int[] none, high;
        using (var conn = db.Open("Accelerator=None"))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM person WHERE amount > 100 ORDER BY id";
            none = ReadInts(cmd).ToArray();
        }
        using (var conn = db.Open("Accelerator=Highlike"))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM person WHERE amount > 100 ORDER BY id";
            high = ReadInts(cmd).ToArray();
        }

        Assert.Equal(none, high);
        Assert.NotEmpty(high);
    }

    // ---- (9) ReadOnly connection rejects writes, still reads ------------------------------

    [Fact]
    public void ReadOnly_Connection_RejectsWrites_StillReads()
    {
        using var db = new PersonDb();
        using var conn = db.Open("ReadOnly=true");
        using var cmd = conn.CreateCommand();

        // A read works.
        cmd.CommandText = "SELECT name FROM person WHERE id = 3";
        Assert.Equal("Smithson", ((string)cmd.ExecuteScalar()!).TrimEnd());

        // A write is rejected and the row is NOT mutated.
        cmd.CommandText = "UPDATE person SET city = 'Cologne' WHERE id = 1";
        Assert.ThrowsAny<Exception>(() => cmd.ExecuteNonQuery());

        cmd.CommandText = "SELECT city FROM person WHERE id = 1";
        Assert.NotEqual("Cologne", ((string)cmd.ExecuteScalar()!).TrimEnd());
    }

    // ---- helpers --------------------------------------------------------------------------

    private static List<int> ReadInts(DbCommand cmd)
    {
        var ids = new List<int>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(Convert.ToInt32(r.GetValue(0)));
        return ids;
    }
}
