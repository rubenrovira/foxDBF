using System;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace CrossVault.FoxDbf.Data;

/// <summary>
/// One command in a <see cref="FoxDbfBatch"/> (.NET 6+): it carries its own
/// <see cref="DbBatchCommand.CommandText"/>, <see cref="DbBatchCommand.CommandType"/> and
/// <see cref="DbBatchCommand.Parameters"/>, exactly like a <see cref="FoxDbfCommand"/> minus the
/// connection (the owning <see cref="FoxDbfBatch"/> supplies the connection + transaction). After the
/// batch executes, <see cref="DbBatchCommand.RecordsAffected"/> reports this command's own affected count.
/// </summary>
public sealed class FoxDbfBatchCommand : DbBatchCommand
{
    private readonly FoxDbfParameterCollection _parameters = new();
    private string _commandText = string.Empty;
    private int _recordsAffected = -1;

    public FoxDbfBatchCommand() { }

    public FoxDbfBatchCommand(string? commandText) => _commandText = commandText ?? string.Empty;

    public FoxDbfBatchCommand(string? commandText, CommandType commandType)
    {
        _commandText = commandText ?? string.Empty;
        CommandType = commandType;
    }

    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set => _commandText = value ?? string.Empty;
    }

    public override CommandType CommandType { get; set; } = CommandType.Text;

    public override int RecordsAffected => _recordsAffected;

    /// <summary>Set by <see cref="FoxDbfBatch"/> as each command executes.</summary>
    internal void SetRecordsAffected(int value) => _recordsAffected = value;

    public override bool CanCreateParameter => true;

    public override DbParameter CreateParameter() => new FoxDbfParameter();

    protected override DbParameterCollection DbParameterCollection => _parameters;
}
