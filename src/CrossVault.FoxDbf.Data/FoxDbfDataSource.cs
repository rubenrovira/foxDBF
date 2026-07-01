using System;
using System.Data.Common;

namespace CrossVault.FoxDbf.Data;

/// <summary>
/// The modern <see cref="DbDataSource"/> connection-factory abstraction (.NET 7+) over a FoxPro / dBase
/// data source — the <c>AddNpgsqlDataSource</c>-style ergonomics for dependency injection. Constructed
/// from a single connection string, it is a thin, thread-safe FACTORY: each
/// <see cref="DbDataSource.CreateConnection"/> yields a FRESH, independent <see cref="FoxDbfConnection"/>
/// on the SAME data source; <see cref="DbDataSource.OpenConnection"/> returns one already opened.
/// <para>
/// Register one as a singleton and resolve <see cref="DbConnection"/> / <see cref="DbCommand"/> /
/// <see cref="DbBatch"/> from it, instead of threading a connection string through the app.
/// </para>
/// </summary>
public sealed class FoxDbfDataSource : DbDataSource
{
    private readonly string _connectionString;

    /// <summary>Create a data source over the given FoxDbf connection string (the same string a
    /// <see cref="FoxDbfConnection"/> accepts: <c>Data Source = .dbc | directory | .dbf</c>; Collate;
    /// Exclusive; Deleted; Ansi; ReadOnly; Accelerator; EnforceRules).</summary>
    public FoxDbfDataSource(string connectionString)
        => _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));

    /// <inheritdoc />
    public override string ConnectionString => _connectionString;

    /// <inheritdoc />
    /// <remarks>Each call yields a FRESH, CLOSED <see cref="FoxDbfConnection"/> over this data source's
    /// connection string. The inherited <see cref="DbDataSource"/> machinery layers the rest on top:
    /// <see cref="DbDataSource.OpenConnection"/> opens one, <see cref="DbDataSource.CreateCommand"/> /
    /// <see cref="DbDataSource.CreateBatch"/> hand back command / batch wrappers that own a connection
    /// opened lazily on first execution, and Dispose / DisposeAsync tear everything down.</remarks>
    protected override DbConnection CreateDbConnection()
        => new FoxDbfConnection(_connectionString);
}
