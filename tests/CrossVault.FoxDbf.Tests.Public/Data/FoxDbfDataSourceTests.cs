using System;
using System.Data;
using System.Data.Common;
using System.Threading.Tasks;
using CrossVault.FoxDbf.Data;
using Xunit;

namespace CrossVault.FoxDbf.Tests.Data;

/// <summary>
/// Acceptance tests (TDD RED) for <see cref="FoxDbfDataSource"/> — the modern <see cref="DbDataSource"/>
/// connection-factory abstraction (.NET 7+) for DI ergonomics. Each test drives the public
/// <see cref="DbDataSource"/> surface against a temp COPY of the canonical PERSON table. They fail until
/// the data source is implemented.
/// </summary>
public sealed class FoxDbfDataSourceTests
{
    // ---- (1) ConnectionString is exposed + CreateConnection yields a CLOSED FoxDbfConnection ----

    [Fact]
    public void ConnectionString_Exposed_And_CreateConnection_ReturnsClosed()
    {
        using var db = new PersonDb();
        using var ds = new FoxDbfDataSource(db.ConnectionString());

        Assert.Equal(db.ConnectionString(), ds.ConnectionString);

        using var c = ds.CreateConnection();
        Assert.IsType<FoxDbfConnection>(c);
        Assert.Equal(ConnectionState.Closed, c.State);   // a factory hands back a CLOSED connection
    }

    // ---- (2) OpenConnection yields an OPEN, usable connection -----------------------------------

    [Fact]
    public void OpenConnection_ReturnsOpen_AndUsable()
    {
        using var db = new PersonDb();
        using var ds = new FoxDbfDataSource(db.ConnectionString());

        using var c = ds.OpenConnection();
        Assert.IsType<FoxDbfConnection>(c);
        Assert.Equal(ConnectionState.Open, c.State);

        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT name FROM person WHERE id = 3";
        Assert.Equal("Smithson", ((string)cmd.ExecuteScalar()!).TrimEnd());
    }

    [Fact]
    public async Task OpenConnectionAsync_ReturnsOpen()
    {
        using var db = new PersonDb();
        await using var ds = new FoxDbfDataSource(db.ConnectionString());

        await using var c = await ds.OpenConnectionAsync();
        Assert.Equal(ConnectionState.Open, c.State);
    }

    // ---- (3) CreateCommand(sql) returns a runnable command --------------------------------------

    [Fact]
    public void CreateCommand_WithSql_Executes()
    {
        using var db = new PersonDb();
        using var ds = new FoxDbfDataSource(db.ConnectionString());

        using var cmd = ds.CreateCommand("SELECT name FROM person WHERE id = 3");
        Assert.Equal("Smithson", ((string)cmd.ExecuteScalar()!).TrimEnd());
    }

    // ---- (4) Two CreateConnection() calls are INDEPENDENT instances on the same source ----------

    [Fact]
    public void TwoConnections_AreIndependent()
    {
        using var db = new PersonDb();
        using var ds = new FoxDbfDataSource(db.ConnectionString());

        using var c1 = ds.CreateConnection();
        using var c2 = ds.CreateConnection();

        Assert.NotSame(c1, c2);

        c1.Open();
        Assert.Equal(ConnectionState.Open, c1.State);
        Assert.Equal(ConnectionState.Closed, c2.State);   // opening one must not open the other
    }

    // ---- (5) Dispose / DisposeAsync ------------------------------------------------------------

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        using var db = new PersonDb();
        var ds = new FoxDbfDataSource(db.ConnectionString());
        using (var c = ds.OpenConnection()) { /* exercise it */ }
        ds.Dispose();
        ds.Dispose();   // idempotent
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        using var db = new PersonDb();
        var ds = new FoxDbfDataSource(db.ConnectionString());
        await using (var c = await ds.OpenConnectionAsync()) { /* exercise it */ }
        await ds.DisposeAsync();
    }

    // ---- (6) The provider factory exposes CreateDataSource --------------------------------------

    [Fact]
    public void Factory_CreateDataSource_ReturnsType_AndWorks()
    {
        using var db = new PersonDb();
        var ds = FoxDbfProviderFactory.Instance.CreateDataSource(db.ConnectionString());

        Assert.IsType<FoxDbfDataSource>(ds);

        using var c = ds.CreateConnection();
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM person WHERE id = 1";
        Assert.Equal(1, Convert.ToInt32(cmd.ExecuteScalar()));
    }
}
