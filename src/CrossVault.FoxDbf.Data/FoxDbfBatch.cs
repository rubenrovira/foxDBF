using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Data;

/// <summary>
/// A <see cref="DbBatch"/> (.NET 6+) executing MULTIPLE commands over ONE <see cref="FoxDbfConnection"/>.
/// Because the engine is a LOCAL file engine there is no network round-trip to save; the batch is purely
/// an API-completeness convenience with these semantics:
/// <list type="bullet">
/// <item><see cref="DbBatch.ExecuteReader"/> runs the commands; the reader exposes the FIRST command's
/// result set and <see cref="DbDataReader.NextResult"/> walks to each subsequent command's set;</item>
/// <item><see cref="DbBatch.ExecuteNonQuery"/> SUMS the affected-row counts of all commands;</item>
/// <item><see cref="DbBatch.ExecuteScalar"/> is the first column of the first row of the FIRST command.</item>
/// </list>
/// It honors the connection's active transaction and its opt-in <c>EnforceRules</c> write-model.
/// </summary>
public sealed class FoxDbfBatch : DbBatch
{
    private readonly FoxDbfBatchCommandCollection _commands = new();
    private FoxDbfConnection? _connection;
    private FoxDbfTransaction? _transaction;

    public FoxDbfBatch() { }

    public FoxDbfBatch(FoxDbfConnection? connection) => _connection = connection;

    protected override DbBatchCommandCollection DbBatchCommands => _commands;

    /// <summary>Strongly-typed view of <see cref="DbBatch.BatchCommands"/>.</summary>
    public new FoxDbfBatchCommandCollection BatchCommands => _commands;

    public override int Timeout { get; set; }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = (FoxDbfConnection?)value;
    }

    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set => _transaction = (FoxDbfTransaction?)value;
    }

    protected override DbBatchCommand CreateDbBatchCommand() => new FoxDbfBatchCommand();

    public override void Cancel() { /* no-op */ }

    public override void Prepare() { /* no-op */ }

    public override Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);
        Prepare();
        return Task.CompletedTask;
    }

    // ---- execution -------------------------------------------------------------------------
    //
    // Every command is delegated to a real FoxDbfCommand on the owning connection, so it travels the
    // EXACT same path as a stand-alone command: parameter binding, the active transaction's copy-on-write
    // redirect (set up at the session level by FoxDbfConnection.BeginTransaction), and the opt-in
    // EnforceRules write-model all apply unchanged. A local file engine saves no round-trip, so the batch
    // is a pure API-completeness convenience.

    public override int ExecuteNonQuery()
    {
        EnsureReady();
        int total = 0;
        foreach (var bc in EnumerateCommands())
        {
            using var cmd = ToCommand(bc);
            int affected = cmd.ExecuteNonQuery();
            bc.SetRecordsAffected(affected);
            if (affected > 0) total += affected;   // -1 (non-row-count statements) does not subtract.
        }
        return total;
    }

    public override object? ExecuteScalar()
    {
        EnsureReady();
        object? scalar = null;
        bool first = true;
        foreach (var bc in EnumerateCommands())
        {
            using var cmd = ToCommand(bc);
            if (first)
            {
                scalar = cmd.ExecuteScalar();   // first column of the first row of the FIRST command.
                first = false;
            }
            else
            {
                // Still run the remaining commands so the whole batch executes (DbBatch semantics).
                cmd.ExecuteNonQuery();
            }
        }
        return scalar;
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        EnsureReady();
        _connection!.ReserveActiveReaderSlot();
        try
        {
            var results = new List<SqlResult>(_commands.Count);
            foreach (var bc in EnumerateCommands())
            {
                using var cmd = ToCommand(bc);
                results.Add(cmd.BuildResultSet(behavior));
            }
            // One reader over every command's result set: it exposes the first set and NextResult() walks on.
            var reader = new FoxDbfDataReader(results, _connection, behavior);
            _connection.CommitActiveReaderSlot(reader);
            return reader;
        }
        catch
        {
            _connection!.SetActiveReader(null);
            throw;
        }
    }

    // ---- helpers ---------------------------------------------------------------------------

    private void EnsureReady()
    {
        if (_connection is null)
            throw new InvalidOperationException("FoxDbfBatch requires a connection.");
        if (_connection.State != ConnectionState.Open)
            throw new InvalidOperationException("Connection is not open.");
        if (_commands.Count == 0)
            throw new InvalidOperationException("The batch contains no commands.");
    }

    private IEnumerable<FoxDbfBatchCommand> EnumerateCommands()
    {
        foreach (DbBatchCommand bc in _commands)
            yield return (FoxDbfBatchCommand)bc;
    }

    /// <summary>Project one batch command onto a real <see cref="FoxDbfCommand"/> bound to this batch's
    /// connection + transaction, carrying its CommandText, CommandType and parameters.</summary>
    private FoxDbfCommand ToCommand(FoxDbfBatchCommand bc)
    {
        var cmd = (FoxDbfCommand)_connection!.CreateCommand();
        cmd.CommandText = bc.CommandText;
        cmd.CommandType = bc.CommandType;
        cmd.Transaction = _transaction;
        foreach (DbParameter p in bc.Parameters)
            cmd.Parameters.Add(p);
        return cmd;
    }

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<int>(cancellationToken);
        try { return Task.FromResult(ExecuteNonQuery()); }
        catch (Exception ex) { return Task.FromException<int>(ex); }
    }

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<object?>(cancellationToken);
        try { return Task.FromResult(ExecuteScalar()); }
        catch (Exception ex) { return Task.FromException<object?>(ex); }
    }

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<DbDataReader>(cancellationToken);
        try { return Task.FromResult(ExecuteDbDataReader(behavior)); }
        catch (Exception ex) { return Task.FromException<DbDataReader>(ex); }
    }
}

/// <summary>
/// The <see cref="DbBatchCommandCollection"/> backing a <see cref="FoxDbfBatch"/> — an ordered list of
/// <see cref="FoxDbfBatchCommand"/>. Adding a non-FoxDbf command throws.
/// </summary>
public sealed class FoxDbfBatchCommandCollection : DbBatchCommandCollection
{
    private readonly List<DbBatchCommand> _items = new();

    public override int Count => _items.Count;

    public override bool IsReadOnly => false;

    public override IEnumerator<DbBatchCommand> GetEnumerator() => _items.GetEnumerator();

    public override void Add(DbBatchCommand item) => _items.Add(Check(item));

    public override void Clear() => _items.Clear();

    public override bool Contains(DbBatchCommand item) => _items.Contains(item);

    public override void CopyTo(DbBatchCommand[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

    public override int IndexOf(DbBatchCommand item) => _items.IndexOf(item);

    public override void Insert(int index, DbBatchCommand item) => _items.Insert(index, Check(item));

    public override bool Remove(DbBatchCommand item) => _items.Remove(item);

    public override void RemoveAt(int index) => _items.RemoveAt(index);

    protected override DbBatchCommand GetBatchCommand(int index) => _items[index];

    protected override void SetBatchCommand(int index, DbBatchCommand batchCommand) => _items[index] = Check(batchCommand);

    private static DbBatchCommand Check(DbBatchCommand item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item is not FoxDbfBatchCommand)
            throw new InvalidCastException(
                $"Only {nameof(FoxDbfBatchCommand)} instances can be added to a {nameof(FoxDbfBatch)}.");
        return item;
    }
}
