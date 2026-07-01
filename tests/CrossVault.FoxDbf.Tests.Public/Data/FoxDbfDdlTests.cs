using System;
using System.Data;
using System.IO;
using CrossVault.FoxDbf.Data;
using Xunit;

namespace CrossVault.FoxDbf.Tests.Data;

/// <summary>
/// ADO.NET DDL acceptance tests: DDL (CREATE / ALTER / DROP TABLE) runs through
/// <see cref="FoxDbfCommand.ExecuteNonQuery"/> against a connection whose <c>Data Source</c> is a
/// throwaway temp directory (free-table mode). ExecuteNonQuery returns a non-positive count
/// (-1 / 0) for a DDL command, and the resulting table is immediately usable for INSERT / SELECT.
///
/// SAFETY: temp directory only — DDL creates AND deletes files; no committed fixture is touched.
/// RED until the DDL executor is implemented.
/// </summary>
public sealed class FoxDbfDdlTests : IDisposable
{
    private readonly SqlTestSupport.TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private FoxDbfConnection Open()
    {
        var c = new FoxDbfConnection($"Data Source={_dir.Path}");
        c.Open();
        return c;
    }

    [Fact]
    public void ExecuteNonQuery_CreateTable_ThenInsertAndSelect()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE gadget (ID I NOT NULL, NAME C(20) NULL, PRICE N(10,2) NOT NULL)";
        int rc = cmd.ExecuteNonQuery();
        Assert.True(rc <= 0, "DDL ExecuteNonQuery returns -1 or 0.");

        Assert.True(File.Exists(Path.Combine(_dir.Path, "gadget.dbf")));

        cmd.CommandText = "INSERT INTO gadget (ID, NAME, PRICE) VALUES (1, 'Widget', 12.50)";
        Assert.Equal(1, cmd.ExecuteNonQuery());

        cmd.CommandText = "SELECT ID, NAME, PRICE FROM gadget";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(1, Convert.ToInt32(r.GetValue(0)));
        Assert.Equal("Widget", ((string)r.GetValue(1)).TrimEnd());
        Assert.Equal(12.50m, Convert.ToDecimal(r.GetValue(2)));
        Assert.False(r.Read());
    }

    [Fact]
    public void ExecuteNonQuery_AlterTable_AddColumn()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE t (ID I NOT NULL)";
        cmd.ExecuteNonQuery();

        cmd.CommandText = "ALTER TABLE t ADD COLUMN NOTE C(30) NULL";
        int rc = cmd.ExecuteNonQuery();
        Assert.True(rc <= 0);

        cmd.CommandText = "SELECT ID, NOTE FROM t";
        using var r = cmd.ExecuteReader();
        Assert.Equal(2, r.FieldCount);
    }

    [Fact]
    public void ExecuteNonQuery_DropTable_RemovesFile()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();

        cmd.CommandText = "CREATE TABLE t (ID I NOT NULL)";
        cmd.ExecuteNonQuery();
        Assert.True(File.Exists(Path.Combine(_dir.Path, "t.dbf")));

        cmd.CommandText = "DROP TABLE t";
        int rc = cmd.ExecuteNonQuery();
        Assert.True(rc <= 0);

        Assert.False(File.Exists(Path.Combine(_dir.Path, "t.dbf")));
    }
}
