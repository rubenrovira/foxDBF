using System.Globalization;
using System.Text;

namespace CrossVault.FoxDbf;

/// <summary>
/// An RFC-4180 CSV writer for <see cref="DbfTable"/> records (plan §A9), with NO external
/// dependency. Reproduces the Ruby <c>dbf</c> gem's <c>to_csv(force_quotes: true)</c> output
/// byte-for-byte for a real-world customer golden master (<c>exportdata/*.csv</c>): an explicit UTF-8
/// BOM, force-quoted fields, <c>CRLF</c> row terminators, and one header row of the visible
/// column names followed by one row per non-deleted record from <see cref="DbfRecord.ToArray()"/>.
/// </summary>
/// <remarks>
/// Value formatting (§A9): <see cref="string"/> emitted verbatim (normalization happens at decode —
/// <c>'C'</c> is trimmed in <see cref="FieldDecoder"/>, and the hidden type-<c>'0'</c> <c>_NullFlags</c>
/// fallback is trimmed there too so it renders as the empty cell the gem emits, while text memo /
/// Varchar values keep their leading/trailing layout); integral / <see cref="decimal"/> /
/// <see cref="double"/> via <see cref="CultureInfo.InvariantCulture"/>; <see cref="DateOnly"/> as the
/// Ruby gem renders it (<c>yyyy-MM-dd</c>); <see cref="bool"/> as <c>"true"</c>/<c>"false"</c>;
/// <see langword="null"/> as the empty string. RFC-4180 escaping doubles embedded quotes and
/// (per <see cref="CsvExportOptions.ForceQuotes"/>) wraps fields; the quote trigger follows the
/// ACTIVE <see cref="CsvExportOptions.Delimiter"/>, not a hard-coded comma.
/// Known divergences from the gem (documented, not papered over): <c>N</c> with decimals renders
/// as decimal text (vs the gem's float text), and a VFP NULL <c>L</c> may render empty (vs the
/// gem's <c>false</c>).
/// </remarks>
public static class CsvExporter
{
    private const string RowTerminator = "\r\n";

    /// <summary>
    /// Export <paramref name="table"/> to the file at <paramref name="path"/> (plan §A9). The
    /// internally owned <see cref="StreamWriter"/> emits the
    /// <see cref="CsvExportOptions.Encoding"/>'s preamble (e.g. the UTF-8 BOM <c>EF BB BF</c>)
    /// before streaming the body.
    /// </summary>
    public static void Export(DbfTable table, string path, CsvExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(path);
        options ??= new CsvExportOptions();

        string fullPath = Path.GetFullPath(path);
        string publishPath = ResolvePublishPath(fullPath);
        string directory = Path.GetDirectoryName(publishPath)!;
        string tempPath = Path.Combine(directory,
            $".{Path.GetFileName(publishPath)}.{Guid.NewGuid():N}.tmp");
        bool tempCreated = false;

        try
        {
            using (var fs = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                tempCreated = true;
                using var writer = new StreamWriter(fs, options.Encoding);
                WriteTo(table, writer, options);
            }

            File.Move(tempPath, publishPath, overwrite: true);
        }
        catch
        {
            if (tempCreated)
            {
                try { File.Delete(tempPath); } catch { /* best effort */ }
            }
            throw;
        }
    }

    private static string ResolvePublishPath(string fullPath)
    {
        try
        {
            var file = new FileInfo(fullPath);
            if (file.LinkTarget is null)
                return fullPath;

            var target = file.ResolveLinkTarget(returnFinalTarget: true);
            if (target is FileInfo)
                return target.FullName;

            System.Diagnostics.Debug.WriteLine(
                $"CsvExporter could not safely resolve file symlink '{fullPath}'; using the original path.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"CsvExporter could not resolve file symlink '{fullPath}'; using the original path: {ex.Message}");
        }

        // Broken or unsupported links retain the r1 behavior rather than turning resolution into a new error.
        return fullPath;
    }

    /// <summary>
    /// Export <paramref name="table"/> to <paramref name="writer"/> (plan §A9). The writer owns
    /// its own encoding/BOM, so <see cref="CsvExportOptions.Encoding"/> is NOT consulted and no
    /// BOM is injected here.
    /// </summary>
    public static void Export(DbfTable table, TextWriter writer, CsvExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(writer);
        WriteTo(table, writer, options ?? new CsvExportOptions());
    }

    private static void WriteTo(DbfTable table, TextWriter writer, CsvExportOptions options)
    {
        // Visible columns: drop IsSystem columns (e.g. _NullFlags) unless explicitly exposed.
        var columns = table.Columns;
        var indices = new List<int>(columns.Count);
        for (int i = 0; i < columns.Count; i++)
            if (options.ExposeSystemColumns || !columns[i].IsSystem)
                indices.Add(i);

        char delimiter = options.Delimiter;
        bool force = options.ForceQuotes;

        // Header row = the visible column names.
        for (int k = 0; k < indices.Count; k++)
        {
            if (k > 0) writer.Write(delimiter);
            WriteField(writer, columns[indices[k]].Name, delimiter, force);
        }
        writer.Write(RowTerminator);

        // One row per record (deleted skipped unless IncludeDeleted), each value formatted to text.
        foreach (var record in table.EnumerateAll(includeDeleted: options.IncludeDeleted))
        {
            var values = record.ToArray();
            for (int k = 0; k < indices.Count; k++)
            {
                if (k > 0) writer.Write(delimiter);
                WriteField(writer, FormatValue(values[indices[k]]), delimiter, force);
            }
            writer.Write(RowTerminator);
        }
    }

    /// <summary>Format one decoded value to its CSV text per §A9 (null → empty string).</summary>
    private static string FormatValue(object? value) => value switch
    {
        null => "",
        // Strings arrive already normalized by FieldDecoder ('C' trimmed at decode; the
        // type-'0' _NullFlags fallback trimmed to ""); text memo/Varchar values are emitted
        // verbatim so their leading/trailing layout is NOT silently stripped (oracle-faithful).
        string s => s,
        bool b => b ? "true" : "false",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        byte[] => "", // binary memo / Q varbinary: no faithful text form; emit empty (never throw).
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>
    /// Write one RFC-4180 field. Quotes when <paramref name="force"/> is set, or when the field
    /// contains the active <paramref name="delimiter"/>, a double quote, CR or LF; embedded double
    /// quotes are doubled.
    /// </summary>
    private static void WriteField(TextWriter writer, string field, char delimiter, bool force)
    {
        bool needsQuote = force
            || field.IndexOf('"') >= 0
            || field.IndexOf(delimiter) >= 0
            || field.IndexOf('\r') >= 0
            || field.IndexOf('\n') >= 0;

        if (!needsQuote)
        {
            writer.Write(field);
            return;
        }

        writer.Write('"');
        writer.Write(field.IndexOf('"') >= 0 ? field.Replace("\"", "\"\"") : field);
        writer.Write('"');
    }
}
