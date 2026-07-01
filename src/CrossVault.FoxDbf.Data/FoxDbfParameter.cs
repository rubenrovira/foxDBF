using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace CrossVault.FoxDbf.Data;

/// <summary>
/// One command parameter. Bound positionally (<c>?</c>) or by name (<c>@name</c> / <c>:name</c>) into
/// the SQL text at execute time. <see cref="DbType"/> is advisory — the actual VFP literal rendering is
/// derived from the runtime CLR type of <see cref="Value"/>.
/// </summary>
public sealed class FoxDbfParameter : DbParameter
{
    private string _parameterName = string.Empty;
    private string _sourceColumn = string.Empty;
    private DataRowVersion _sourceVersion = DataRowVersion.Current;

    public FoxDbfParameter() { }

    public FoxDbfParameter(string? parameterName, object? value)
    {
        ParameterName = parameterName ?? string.Empty;
        Value = value;
    }

    public override DbType DbType { get; set; } = DbType.Object;

    public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;

    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName
    {
        get => _parameterName;
        set => _parameterName = value ?? string.Empty;
    }

    public override int Size { get; set; }

    [AllowNull]
    public override string SourceColumn
    {
        get => _sourceColumn;
        set => _sourceColumn = value ?? string.Empty;
    }

    public override bool SourceColumnNullMapping { get; set; }

    // DbParameter.SourceVersion is virtual with a no-op setter (always returns Current), which would
    // silently drop a SourceVersion=Original assignment. The DataSet bridge relies on Original for the
    // WHERE RECNO()=? identity binding (the row's ORIGINAL recno), so store it for real here.
    public override DataRowVersion SourceVersion
    {
        get => _sourceVersion;
        set => _sourceVersion = value;
    }

    public override object? Value { get; set; }

    public override void ResetDbType() => DbType = DbType.Object;
}
