namespace CrossVault.FoxDbf;

public sealed partial class DbfDatabase
{
    /// <summary>
    /// Convenience: export every member table to <c>&lt;dir&gt;/&lt;OBJECTNAME&gt;.csv</c>
    /// (plan §A9), opening each via <see cref="OpenTable(string)"/> and writing it with
    /// <see cref="CsvExporter"/>. Returns the number of tables exported.
    /// </summary>
    public int ExportAllCsv(string dir, CsvExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dir);
        Directory.CreateDirectory(dir);

        int count = 0;
        foreach (var name in TableNames)
        {
            using var table = OpenTable(name);
            table.ExportCsv(Path.Combine(dir, name + ".csv"), options);
            count++;
        }
        return count;
    }
}
