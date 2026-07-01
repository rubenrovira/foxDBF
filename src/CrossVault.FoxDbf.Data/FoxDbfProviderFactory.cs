using System.Data.Common;

namespace CrossVault.FoxDbf.Data;

/// <summary>
/// The provider factory for the FoxDbf ADO.NET provider. Register it once with the ADO.NET factory
/// registry so generic, provider-agnostic code can resolve it by invariant name:
/// <code>
/// DbProviderFactories.RegisterFactory("CrossVault.FoxDbf", FoxDbfProviderFactory.Instance);
/// var factory = DbProviderFactories.GetFactory("CrossVault.FoxDbf");
/// using var conn = factory.CreateConnection();
/// </code>
/// </summary>
public sealed class FoxDbfProviderFactory : DbProviderFactory
{
    /// <summary>The singleton instance to register / resolve.</summary>
    public static readonly FoxDbfProviderFactory Instance = new();

    private FoxDbfProviderFactory() { }

    public override DbConnection CreateConnection() => new FoxDbfConnection();

    public override DbCommand CreateCommand() => new FoxDbfCommand();

    public override DbParameter CreateParameter() => new FoxDbfParameter();

    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new FoxDbfConnectionStringBuilder();

    public override DbDataAdapter CreateDataAdapter() => new FoxDbfDataAdapter();

    public override DbCommandBuilder CreateCommandBuilder() => new FoxDbfCommandBuilder();

    public override bool CanCreateDataAdapter => true;

    public override bool CanCreateCommandBuilder => true;

    public override bool CanCreateBatch => true;

    public override DbBatch CreateBatch() => new FoxDbfBatch();

    public override DbBatchCommand CreateBatchCommand() => new FoxDbfBatchCommand();

    /// <summary>Create a <see cref="FoxDbfDataSource"/> over the given connection string (the modern
    /// DI-friendly connection factory).</summary>
    public override DbDataSource CreateDataSource(string connectionString)
        => new FoxDbfDataSource(connectionString);
}
