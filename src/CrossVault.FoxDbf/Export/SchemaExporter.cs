using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CrossVault.FoxDbf;

/// <summary>
/// Emits a schema description of a <see cref="DbfTable"/> in three neutral formats (plan §A9):
/// a JSON array (<see cref="ToJson"/>), a Markdown structure table (<see cref="ToMarkdown"/>,
/// the shape produced by the Ruby <c>export_dbc.rb</c> <c>STRUCTURE.md</c>), and a SQL
/// <c>CREATE TABLE</c> DDL (<see cref="ToSqlDdl"/>). All three iterate <see cref="DbfTable.Columns"/>,
/// so the hidden type-<c>0</c> <c>_NullFlags</c> system column is included only when the table was
/// opened with <see cref="DbfOptions.ExposeSystemColumns"/>.
/// </summary>
/// <remarks>
/// The Ruby-specific ActiveRecord/Sequel formats are intentionally omitted (plan §A9, YAGNI).
/// <see cref="UnderscoredName"/> replicates the gem's <c>Column#underscored_name</c> exactly for
/// stable, language-neutral column keys.
/// </remarks>
public static partial class SchemaExporter
{
    /// <summary>
    /// Serialize the visible columns of <paramref name="table"/> as a JSON array of
    /// <c>{ name, type, length, decimal }</c> objects (one per <see cref="DbfTable.Columns"/>
    /// entry), via <c>System.Text.Json</c>. Mirrors the gem's <c>json_schema</c>
    /// (<c>columns.map(&amp;:to_hash).to_json</c>). Column names are JSON-escaped.
    /// </summary>
    public static string ToJson(DbfTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        using var ms = new MemoryStream();
        // RELAXED escaping reproduces the Ruby gem's to_json byte-for-byte: the default
        // JavaScriptEncoder would escape the autoincrement type byte '+' (+), '<' '>' '&',
        // and every non-ASCII char (e.g. German 'ö' -> ö) — none of which the gem escapes.
        // Safe here: output is consumed programmatically, never injected into HTML.
        using (var w = new Utf8JsonWriter(ms,
            new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartArray();
            foreach (var col in table.Columns)
            {
                w.WriteStartObject();
                w.WriteString("name", col.Name);
                w.WriteString("type", col.Type.ToString());
                w.WriteNumber("length", col.Length);
                w.WriteNumber("decimal", col.Decimal);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Render <paramref name="table"/> as a Markdown structure block (plan §A9): the table name,
    /// its record count, then a table with one row per visible column carrying the 1-based index,
    /// name, type, length and decimal — the layout of the Ruby export's <c>STRUCTURE.md</c>.
    /// </summary>
    public static string ToMarkdown(DbfTable table, string? tableName = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        string name = tableName ?? "table";

        var sb = new StringBuilder();
        sb.Append("# ").Append(name).Append('\n');
        sb.Append('\n');
        sb.Append("Records: ").Append(table.RecordCount).Append('\n');
        sb.Append('\n');
        sb.Append("| # | Name | Type | Length | Decimal |\n");
        sb.Append("| --- | --- | --- | --- | --- |\n");
        int i = 1;
        foreach (var col in table.Columns)
        {
            sb.Append("| ").Append(i).Append(" | ")
              .Append(col.Name).Append(" | ")
              .Append(col.Type).Append(" | ")
              .Append(col.Length).Append(" | ")
              .Append(col.Decimal).Append(" |\n");
            i++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Build a SQL <c>CREATE TABLE</c> statement for <paramref name="table"/> (plan §A9), mapping
    /// each xBase type to a SQL type: C→VARCHAR(len), N(dec=0)→INTEGER else DECIMAL(len,dec),
    /// F/B→DOUBLE, I→INTEGER, Y→DECIMAL(15,4), D→DATE, T/@→TIMESTAMP, L→BOOLEAN, M→TEXT,
    /// anything else→VARCHAR.
    /// </summary>
    public static string ToSqlDdl(DbfTable table, string? tableName = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        string name = tableName ?? "table";

        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(name).Append(" (\n");
        var cols = table.Columns;
        for (int i = 0; i < cols.Count; i++)
        {
            var col = cols[i];
            sb.Append("  ").Append(col.Name).Append(' ').Append(SqlType(col));
            if (i < cols.Count - 1) sb.Append(',');
            sb.Append('\n');
        }
        sb.Append(");\n");
        return sb.ToString();
    }

    private static string SqlType(DbfColumn col) => col.Type switch
    {
        'C' => $"VARCHAR({col.Length})",
        'N' => col.Decimal > 0 ? $"DECIMAL({col.Length},{col.Decimal})" : "INTEGER",
        'F' => "DOUBLE",
        'B' => "DOUBLE",
        'I' => "INTEGER",
        'Y' => "DECIMAL(15,4)",
        'D' => "DATE",
        'T' => "TIMESTAMP",
        '@' => "TIMESTAMP",
        'L' => "BOOLEAN",
        'M' => "TEXT",
        _ => "VARCHAR",
    };

    /// <summary>
    /// Replicate the Ruby <c>dbf</c> gem's <c>Column#underscored_name</c> exactly: apply the
    /// regex <c>([a-z\d])([A-Z]) → $1_$2</c>, replace <c>'-'</c> with <c>'_'</c>, then
    /// <see cref="string.ToLowerInvariant"/>. E.g. <c>MyColumn → my_column</c>, <c>PER_ID → per_id</c>.
    /// </summary>
    public static string UnderscoredName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return CamelBoundary().Replace(name, "$1_$2").Replace('-', '_').ToLowerInvariant();
    }

    [GeneratedRegex(@"([a-z\d])([A-Z])")]
    private static partial Regex CamelBoundary();
}
