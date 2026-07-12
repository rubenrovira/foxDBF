using System.Text;

namespace CrossVault.FoxDbf;

/// <summary>
/// Options for <see cref="CsvExporter"/> / <see cref="DbfTable.ExportCsv(string, CsvExportOptions?)"/>
/// (plan §A9). Defaults reproduce the Ruby <c>dbf</c> gem's <c>to_csv(force_quotes: true)</c>
/// golden master used to produce <c>exportdata/*.csv</c>: UTF-8 <em>with</em> a BOM, a comma
/// delimiter, every field force-quoted, deleted records skipped, and system columns hidden.
/// </summary>
/// <remarks>
/// The UTF-8 BOM is carried as the <see cref="Encoding"/>'s preamble
/// (<c>new UTF8Encoding(encoderShouldEmitBOM: true)</c>). The path export's
/// <see cref="StreamWriter"/> writes that preamble automatically before streaming the body;
/// the <see cref="TextWriter"/> overload uses the caller-owned writer and does not consult this
/// option. To export without a BOM, set <see cref="Encoding"/> to <c>new UTF8Encoding(false)</c>.
/// </remarks>
public sealed record CsvExportOptions
{
    /// <summary>
    /// Output text encoding. Default: UTF-8 <em>with</em> a BOM
    /// (<c>new UTF8Encoding(encoderShouldEmitBOM: true)</c>), whose preamble (<c>EF BB BF</c>)
    /// the path export's <see cref="StreamWriter"/> writes automatically. Ignored by the
    /// <see cref="TextWriter"/> overload, whose own encoding/BOM the caller owns.
    /// </summary>
    public Encoding Encoding { get; init; } = new UTF8Encoding(true);

    /// <summary>The field delimiter. Default: comma (<c>,</c>), matching the Ruby gem.</summary>
    public char Delimiter { get; init; } = ',';

    /// <summary>
    /// When true (the default, matching the Ruby <c>to_csv(force_quotes: true)</c>) every field
    /// is wrapped in double quotes; when false only fields containing the delimiter, a double
    /// quote, CR or LF are quoted (RFC-4180 minimal quoting).
    /// </summary>
    public bool ForceQuotes { get; init; } = true;

    /// <summary>When false (the default) deleted records are skipped; when true they are exported too.</summary>
    public bool IncludeDeleted { get; init; } = false;

    /// <summary>
    /// When false (the default) <see cref="DbfColumn.IsSystem"/> columns (e.g. the hidden
    /// <c>_NullFlags</c>) are dropped from the output even if present in
    /// <see cref="DbfTable.Columns"/>; when true they are exported. To reproduce the Ruby
    /// golden master (whose header includes <c>_NullFlags</c>), open the table with
    /// <see cref="DbfOptions.ExposeSystemColumns"/> = true AND set this to true.
    /// </summary>
    public bool ExposeSystemColumns { get; init; } = false;
}
