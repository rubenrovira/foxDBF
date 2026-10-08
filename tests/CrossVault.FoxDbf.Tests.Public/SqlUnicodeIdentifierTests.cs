using System;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Sql;
using Xunit;
using Xunit.Abstractions;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// EXTERNAL-DATASET TEST — <b>NOT part of the regular run.</b>
/// <para>
/// Runs a real SQL statement with Unicode identifiers against the same external production
/// dataset used by <see cref="SqlJoinBenchmarkTests"/>. It is tagged
/// <c>Category=ExternalDataset</c> so CI can exclude it with
/// <c>--filter "Category!=ExternalDataset"</c>, and short-circuits when the dataset is absent.
/// </para>
/// <para>
/// Regression: <c>ExpressionLexer</c> used to reject 'ó' with <c>Unexpected character 'ó'</c>.
/// </para>
/// </summary>
[Trait("Category", "ExternalDataset")]
public sealed class SqlUnicodeIdentifierTests
{
    private const string DataPath = @"d:\JavierBorrajo\MiLaudusSQL\Data\empresa.dbc";

    /// <summary>Unicode identifiers in a real SQL statement against the real dataset.</summary>
    private const string UnicodeIdentifierSql = """
        SELECT clientes.razónSocial, clientes.VATId, clientes.email
          FROM clientes
        """;

    private readonly ITestOutputHelper _output;
    public SqlUnicodeIdentifierTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Regression: the ExpressionLexer used to reject 'ó' with
    /// <c>Unexpected character 'ó'</c>. This real query must now parse, execute and return rows.
    /// </summary>
    [Fact]
    public void Select_UnicodeIdentifier_RazonSocial()
    {
        if (!File.Exists(DataPath))
        {
            _output.WriteLine($"SKIPPED — external dataset not present: {DataPath}");
            return;
        }

        using var session = new VfpSession();
        session.OpenDatabase(DataPath);

        var result = session.Execute(UnicodeIdentifierSql);

        var rows = result!.Rows.ToList();
        _output.WriteLine($" Rows    : {rows.Count}");
        _output.WriteLine($" Columns : {result.Columns.Count}");

        Assert.Equal(3, result.Columns.Count);
        Assert.NotEmpty(rows);

        foreach (var r in rows.Take(5))
            _output.WriteLine($"   row: {string.Join(" | ", r.Select(Show))}");
    }

    private static string Show(object? v) => v switch
    {
        null => "<null>",
        string s => $"'{s.TrimEnd()}'",
        _ => v.ToString() ?? "",
    };
}
