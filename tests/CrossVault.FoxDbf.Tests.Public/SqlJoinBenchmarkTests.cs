using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Query;
using CrossVault.FoxDbf.Sql;
using Xunit;
using Xunit.Abstractions;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// EXTERNAL-DATASET BENCHMARK — <b>NOT part of the regular run.</b>
/// <para>
/// This fixture measures one real production query against one real production dataset that lives
/// OUTSIDE this repository. It therefore:
/// </para>
/// <list type="bullet">
/// <item>is tagged <c>Category=ExternalDataset</c> so CI can exclude it with
/// <c>--filter "Category!=ExternalDataset"</c>;</item>
/// <item>short-circuits (and only reports) when the dataset is not on this machine, so an ordinary
/// <c>dotnet test</c> anywhere else neither fails nor touches anything.</item>
/// </list>
/// <para>
/// The dataset path is the ORIGINAL benchmark path, kept verbatim. Nothing is assumed about it and no
/// substitute dataset is ever built: every table is resolved by NAME through
/// <see cref="VfpSession.OpenDatabase"/>, and the CDX handle is derived by the engine itself from the
/// table's own source path.
/// </para>
/// <para>
/// Methodology matches the ORIGINAL measurement so the numbers stay comparable: one session, one
/// cold <see cref="VfpSession.Execute(string)"/> inside a <see cref="Stopwatch"/>. Time is REPORTED,
/// never asserted (the threshold decision is deliberately deferred). Every other phase runs AFTER the
/// stopwatch stops.
/// </para>
/// </summary>
[Trait("Category", "ExternalDataset")]
public sealed class SqlJoinBenchmarkTests
{
    private const string DataPath = @"d:\JavierBorrajo\MiLaudusSQL\Data\empresa.dbc";

    /// <summary>The figure measured on this dataset BEFORE the driving-source WHERE pushdown: 4 m 43 s.</summary>
    private static readonly TimeSpan OriginalLeftJoin = new(0, 4, 43);   // (h, m, s)

    /// <summary>The original benchmark — LEFT JOIN is the reference case.</summary>
    private const string LeftJoinSql = """
        SELECT facturas.idFactura,
               facturas.docNumber,
               clientes.idCliente,
               clientes.nombre
        FROM facturas
        LEFT JOIN clientes
          ON facturas.idCliente == clientes.idCliente
        WHERE facturas.idFactura == 'I00002057'
        """;

    /// <summary>The same query with an INNER JOIN — measured for comparison.</summary>
    private const string InnerJoinSql = """
        SELECT facturas.idFactura,
               facturas.docNumber,
               clientes.idCliente,
               clientes.nombre
        FROM facturas
        INNER JOIN clientes
          ON facturas.idCliente == clientes.idCliente
        WHERE facturas.idFactura == 'I00002057'
        """;

    private readonly ITestOutputHelper _output;
    public SqlJoinBenchmarkTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void LeftJoin_Facturas_Clientes() => RunBenchmark("LEFT JOIN", LeftJoinSql, reference: true);

    [Fact]
    public void InnerJoin_Facturas_Clientes() => RunBenchmark("INNER JOIN", InnerJoinSql, reference: false);

    // =====================================================================================

    private void RunBenchmark(string label, string sql, bool reference)
    {
        if (!File.Exists(DataPath))
        {
            _output.WriteLine($"SKIPPED — external dataset not present: {DataPath}");
            return;
        }

        _output.WriteLine("========================================================================");
        _output.WriteLine($" BENCHMARK  {label}   (external dataset: {DataPath})");
        _output.WriteLine("========================================================================");

        using var session = new VfpSession();
        session.OpenDatabase(DataPath);

        // ---- phase 1: the timed run (identical methodology to the original benchmark) ----------
        var sw = Stopwatch.StartNew();
        var result = session.Execute(sql);
        sw.Stop();

        // SqlResult.Rows "may be a lazily streamed sequence; enumerate once" → materialize ONCE.
        var rows = result!.Rows.ToList();

        _output.WriteLine($" Elapsed           : {sw.Elapsed}   ({sw.Elapsed.TotalSeconds:F3} s)");
        _output.WriteLine($" Rows              : {rows.Count}");
        _output.WriteLine($" Columns           : {result.Columns.Count}");

        if (reference)
        {
            _output.WriteLine($" Original (before) : {OriginalLeftJoin}   ({OriginalLeftJoin.TotalSeconds:F0} s)");
            _output.WriteLine($" Speed-up vs orig  : {OriginalLeftJoin.TotalSeconds / Math.Max(sw.Elapsed.TotalSeconds, 1e-6):F1}x");
        }

        foreach (var r in rows)
            _output.WriteLine($"   row: {string.Join(" | ", r.Select(Show))}");

        // The one hard correctness assertion: the query must return exactly one factura.
        Assert.Single(rows);

        // ---- phase 2: was the WHERE pushed onto the driving source? (AFTER the timer) ----------
        var ex = new SelectExecutor(session);
        ex.Run((SelectStatement)SqlParser.Parse(sql));

        _output.WriteLine($" PushdownFilters   : {ex.PushdownFilters.Count}");
        foreach (var f in ex.PushdownFilters)
            _output.WriteLine($"   pushed: {f}");

        Assert.Contains(ex.PushdownFilters,
            f => f.Contains("idFactura", StringComparison.OrdinalIgnoreCase));

        // ---- phase 3: does that pushed filter ride the CDX? (report only — no tag assertion) ---
        ReportIndex(label, session, ex.PushdownFilters);

        // ---- context for the timing ------------------------------------------------------------
        ReportTableSizes(session);
    }

    /// <summary>Reports (never asserts) whether the alias-stripped pushed filter is Rushmore-indexable
    /// against the driving table's own structural CDX. A missing tag on <c>idFactura</c> is a valid
    /// outcome of this benchmark, so it is reported rather than asserted.</summary>
    private void ReportIndex(string label, VfpSession session, System.Collections.Generic.IReadOnlyList<string> pushed)
    {
        var conjunct = pushed.FirstOrDefault(
            f => f.Contains("idFactura", StringComparison.OrdinalIgnoreCase));
        if (conjunct is null)
        {
            _output.WriteLine(" [cdx] no idFactura conjunct was pushed — nothing to explain.");
            return;
        }

        session.Use("facturas", again: true, noUpdate: true);
        var wa = session.FindAreaByAlias("facturas");
        if (wa is null)
        {
            _output.WriteLine(" [cdx] facturas could not be opened in a work area.");
            return;
        }

        _output.WriteLine($" [cdx] table path  : {wa.Table.SourcePath ?? "(none)"}");
        _output.WriteLine($" [cdx] cdx opened  : {wa.Cdx is not null}");
        _output.WriteLine($" [cdx] records     : {wa.Table.RecordCount}");

        // Exactly what RestrictDriving hands to FindRecords: the conjunct with the driving alias
        // stripped, wrapped in parens.
        string filter = "(" + SelectExecutor.StripDrivingQualifier(conjunct, "facturas") + ")";
        _output.WriteLine($" [cdx] pushed text : {filter}");

        var s = session.Context;
        var ctx = new EvaluationContext
        {
            Deleted = s.Deleted,
            Exact = s.Exact,
            Ansi = s.Ansi,
            Optimize = s.Optimize,
            Collation = s.Collation,
            SqlSemantics = true,
        };

        var plan = QueryOptimizer.Explain(wa.Table, wa.Cdx, filter, ctx);
        _output.WriteLine($" [cdx] plan.Overall  : {plan.Overall}");
        _output.WriteLine($" [cdx] plan.UsedTags : {(plan.UsedTags.Count == 0 ? "(none)" : string.Join(", ", plan.UsedTags))}");
        foreach (var c in plan.Conditions)
            _output.WriteLine($"       leaf: \"{c.Condition}\"  optimizable={c.Optimizable}  tag={c.TagName ?? "-"}  reason={c.Reason}");

        var qr = QueryOptimizer.FindRecords(wa.Table, wa.Cdx, filter, ctx);
        _output.WriteLine($" [cdx] Optimized      : {qr.Optimized}");
        _output.WriteLine($" [cdx] FullyOptimized : {qr.FullyOptimized}");
        _output.WriteLine($" [cdx] RecordsScanned : {qr.RecordsScanned} / {wa.Table.RecordCount}");
        _output.WriteLine($" [cdx] matched recnos : [{string.Join(", ", qr.RecordNumbers)}]");
    }

    /// <summary>Row counts of both sides — the scale that made the un-pushed join cost minutes.</summary>
    private void ReportTableSizes(VfpSession session)
    {
        foreach (var name in new[] { "facturas", "clientes" })
        {
            try
            {
                session.Use(name, again: true, noUpdate: true);
                var wa = session.FindAreaByAlias(name);
                if (wa is not null)
                    _output.WriteLine($" [size] {name,-10}: {wa.Table.RecordCount} rows, cdx={(wa.Cdx is not null)}");
            }
            catch (Exception e)
            {
                _output.WriteLine($" [size] {name}: unavailable ({e.Message})");
            }
        }
    }

    private static string Show(object? v) => v switch
    {
        null => "<null>",
        string s => $"'{s.TrimEnd()}'",
        _ => v.ToString() ?? "",
    };
}
