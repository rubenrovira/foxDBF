using System;
using System.Data;
using System.Data.Common;

namespace CrossVault.FoxDbf.Data;

/// <summary>
/// The legacy DataSet bridge for the FoxDbf ADO.NET provider: fills a <see cref="DataTable"/> /
/// <see cref="DataSet"/> from <see cref="SelectCommand"/> (via <see cref="FoxDbfDataReader"/>) and
/// applies <see cref="InsertCommand"/> / <see cref="UpdateCommand"/> / <see cref="DeleteCommand"/>
/// for Added / Modified / Deleted <see cref="DataRow"/>s on <c>Update</c>.
/// <para>
/// The work is almost entirely inherited from <see cref="DbDataAdapter"/>: <c>Fill</c> drives the
/// SELECT command's <see cref="FoxDbfDataReader"/>, and <c>Update</c> applies the per-row commands
/// (sourced either from the typed command properties or, on demand, from a
/// <see cref="FoxDbfCommandBuilder"/> via the <see cref="RowUpdating"/> event). The four abstract
/// <c>DbDataAdapter</c> hooks (<c>CreateRowUpdating/UpdatedEvent</c>, <c>OnRowUpdating/Updated</c>) and
/// the explicit <see cref="IDbDataAdapter"/> command wiring are the only glue this layer adds.
/// </para>
/// </summary>
public sealed class FoxDbfDataAdapter : DbDataAdapter, IDbDataAdapter
{
    private static readonly object s_eventRowUpdating = new();
    private static readonly object s_eventRowUpdated = new();

    private FoxDbfCommand? _selectCommand;
    private FoxDbfCommand? _insertCommand;
    private FoxDbfCommand? _updateCommand;
    private FoxDbfCommand? _deleteCommand;

    public FoxDbfDataAdapter() { }

    public FoxDbfDataAdapter(FoxDbfCommand selectCommand)
    {
        SelectCommand = selectCommand;
    }

    public FoxDbfDataAdapter(string selectCommandText, FoxDbfConnection connection)
        : this(new FoxDbfCommand(selectCommandText, connection)) { }

    // ---- typed command properties (backed by the same fields the IDbDataAdapter view uses) ----

    /// <summary>The SELECT used by <c>Fill</c>.</summary>
    public new FoxDbfCommand? SelectCommand
    {
        get => _selectCommand;
        set => _selectCommand = value;
    }

    /// <summary>The INSERT used by <c>Update</c> for Added rows.</summary>
    public new FoxDbfCommand? InsertCommand
    {
        get => _insertCommand;
        set => _insertCommand = value;
    }

    /// <summary>The UPDATE used by <c>Update</c> for Modified rows.</summary>
    public new FoxDbfCommand? UpdateCommand
    {
        get => _updateCommand;
        set => _updateCommand = value;
    }

    /// <summary>The DELETE used by <c>Update</c> for Deleted rows.</summary>
    public new FoxDbfCommand? DeleteCommand
    {
        get => _deleteCommand;
        set => _deleteCommand = value;
    }

    // ---- IDbDataAdapter: the view the base DbDataAdapter.Fill / Update actually consume ----

    IDbCommand? IDbDataAdapter.SelectCommand
    {
        get => _selectCommand;
        set => _selectCommand = (FoxDbfCommand?)value;
    }

    IDbCommand? IDbDataAdapter.InsertCommand
    {
        get => _insertCommand;
        set => _insertCommand = (FoxDbfCommand?)value;
    }

    IDbCommand? IDbDataAdapter.UpdateCommand
    {
        get => _updateCommand;
        set => _updateCommand = (FoxDbfCommand?)value;
    }

    IDbCommand? IDbDataAdapter.DeleteCommand
    {
        get => _deleteCommand;
        set => _deleteCommand = (FoxDbfCommand?)value;
    }

    // ---- row-update events + the abstract DbDataAdapter hooks ----

    /// <summary>Raised before a row's INSERT / UPDATE / DELETE is applied. A
    /// <see cref="FoxDbfCommandBuilder"/> subscribes here to supply the generated command on demand.</summary>
    public event EventHandler<RowUpdatingEventArgs> RowUpdating
    {
        add => Events.AddHandler(s_eventRowUpdating, value);
        remove => Events.RemoveHandler(s_eventRowUpdating, value);
    }

    /// <summary>Raised after a row's INSERT / UPDATE / DELETE has been applied.</summary>
    public event EventHandler<RowUpdatedEventArgs> RowUpdated
    {
        add => Events.AddHandler(s_eventRowUpdated, value);
        remove => Events.RemoveHandler(s_eventRowUpdated, value);
    }

    protected override RowUpdatingEventArgs CreateRowUpdatingEvent(
        DataRow dataRow, IDbCommand? command, StatementType statementType, DataTableMapping tableMapping)
        => new(dataRow, command, statementType, tableMapping);

    protected override RowUpdatedEventArgs CreateRowUpdatedEvent(
        DataRow dataRow, IDbCommand? command, StatementType statementType, DataTableMapping tableMapping)
        => new(dataRow, command, statementType, tableMapping);

    protected override void OnRowUpdating(RowUpdatingEventArgs value)
        => (Events[s_eventRowUpdating] as EventHandler<RowUpdatingEventArgs>)?.Invoke(this, value);

    protected override void OnRowUpdated(RowUpdatedEventArgs value)
        => (Events[s_eventRowUpdated] as EventHandler<RowUpdatedEventArgs>)?.Invoke(this, value);
}
