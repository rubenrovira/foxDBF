# CrossVault.FoxDbf.Data

An **ADO.NET data provider** for Visual FoxPro / dBase tables and database containers, built on
[CrossVault.FoxDbf](https://www.nuget.org/packages/CrossVault.FoxDbf). Query FoxPro/dBase data from
Dapper, raw ADO.NET, reporting tools and LINQPad — using SQL.

## Quick start

```csharp
using CrossVault.FoxDbf.Data;

using var cn = new FoxDbfConnection(@"Data Source=C:\data\tastrade.dbc;Deleted=on");
cn.Open();

using var cmd = cn.CreateCommand();
cmd.CommandText = "SELECT company_name, country FROM customer WHERE country = @c ORDER BY company_name";
var p = cmd.CreateParameter(); p.ParameterName = "@c"; p.Value = "Germany"; cmd.Parameters.Add(p);

using var r = cmd.ExecuteReader();
while (r.Read())
    Console.WriteLine(r.GetString(r.GetOrdinal("company_name")));
```

With Dapper:

```csharp
var rows = cn.Query<Customer>("SELECT * FROM customer WHERE country = @c", new { c = "Germany" });
int n = cn.Execute("UPDATE customer SET region = @r WHERE customer_id = @id", new { r = "DE", id });
```

## Connection string (mirrors the VFP OLE DB Provider)

| Keyword | Values | Meaning |
|---|---|---|
| `Data Source` | a `.dbc`, a directory of free tables, or a `.dbf` | what to open |
| `Collate` | `machine` (default) / `general` | collation for compares + `ORDER BY` |
| `Exclusive` | `true` / `false` | open mode |
| `Deleted` | `on` / `off` | exclude soft-deleted rows |
| `Ansi` | `on` / `off` | SQL `=` comparison (`SET ANSI`) |
| `ReadOnly` | `true` / `false` | |
| `Accelerator` | `Highlike` / `None` | opt-in cost-based query acceleration |

## Supported SQL

`SELECT` with projection, `WHERE`, `INNER`/`LEFT JOIN`, `GROUP BY`/`HAVING`, aggregates,
`ORDER BY`, `DISTINCT`, `TOP`; `INSERT … VALUES`, `UPDATE`, `DELETE` (VFP soft-delete); the VFP
work-area model via `USE` / `SELECT` commands. Positional `?` and named `@`/`:` parameters.

## Register with `DbProviderFactories`

```csharp
DbProviderFactories.RegisterFactory("CrossVault.FoxDbf", FoxDbfProviderFactory.Instance);
```

Output is byte-compatible with the real Visual FoxPro 9 runtime. MIT licensed.
