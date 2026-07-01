namespace CrossVault.FoxDbf;

public sealed partial class DbfTable
{
    /// <summary>
    /// Export this table to a CSV file at <paramref name="path"/> (plan §A9), delegating to
    /// <see cref="CsvExporter"/>. With default <see cref="CsvExportOptions"/> the output is
    /// UTF-8 with an explicit BOM, force-quoted, CRLF-terminated.
    /// </summary>
    public void ExportCsv(string path, CsvExportOptions? options = null)
        => CsvExporter.Export(this, path, options);

    /// <summary>
    /// Export this table as CSV to <paramref name="writer"/> (plan §A9). The writer owns its
    /// own encoding/BOM, so <see cref="CsvExportOptions.Encoding"/> is not used here.
    /// </summary>
    public void ExportCsv(TextWriter writer, CsvExportOptions? options = null)
        => CsvExporter.Export(this, writer, options);
}
