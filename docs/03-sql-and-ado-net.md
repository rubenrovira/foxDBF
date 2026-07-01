# 3. SQL and the ADO.NET provider

**What you'll do:** run SQL against FoxPro/dBase tables two ways — directly through the VFP work-area
session (`CrossVault.FoxDbf.Sql`), or through the ADO.NET provider (`CrossVault.FoxDbf.Data`) from
Dapper/raw `DbCommand`/reporting tools. Most applications want the ADO.NET route.

## Direct: `VfpSession` + `SqlParser`

```csharp
using CrossVault.FoxDbf.Sql;

using var session = new VfpSession();
session.OpenDirectory(@"C:\data");          // a folder of free .dbf tables (or session.OpenDatabase("shop.dbc"))

var result = session.Execute(
    "SELECT company_name, country FROM customer WHERE country = 'Germany' ORDER BY company_name");

foreach (var row in result!.Rows)
    Console.WriteLine(row[0]);
```

`VfpSession` models VFP's **work-area** concept: `USE table IN 0`, `SELECT alias`, and plain table
names in a query auto-open the matching `.dbf`/`.dbc` member the first time they're referenced —
the same mental model as a VFP9 program, minus the language runtime around it.

## Via ADO.NET: `CrossVault.FoxDbf.Data`

The more common path — plug FoxPro/dBase data into anything that speaks `System.Data.Common`:

```csharp
using CrossVault.FoxDbf.Data;

using var cn = new FoxDbfConnection(@"Data Source=C:\data\shop.dbc;Deleted=on");
cn.Open();

using var cmd = cn.CreateCommand();
cmd.CommandText = "SELECT company_name, country FROM customer WHERE country = @c ORDER BY company_name";
var p = cmd.CreateParameter();
p.ParameterName = "@c";
p.Value = "Germany";
cmd.Parameters.Add(p);

using var reader = cmd.ExecuteReader();
while (reader.Read())
    Console.WriteLine(reader.GetString(reader.GetOrdinal("company_name")));
```

### With Dapper

```csharp
var customers = cn.Query<Customer>(
    "SELECT * FROM customer WHERE country = @c", new { c = "Germany" });

int updated = cn.Execute(
    "UPDATE customer SET region = @r WHERE customer_id = @id", new { r = "DE", id = 42 });
```

### Connection string keywords

| Keyword | Values | Meaning |
|---|---|---|
| `Data Source` | a `.dbc`, a directory of free tables, or a `.dbf` | what to open |
| `Collate` | `machine` (default) / `general` | collation for compares + `ORDER BY` |
| `Exclusive` | `true` / `false` | open mode |
| `Deleted` | `on` / `off` | exclude soft-deleted rows |
| `Ansi` | `on` / `off` | SQL `=` comparison (`SET ANSI`) |
| `ReadOnly` | `true` / `false` | |
| `Accelerator` | `Highlike` / `None` | opt-in cost-based query acceleration ([5. Highlike](05-highlike-accelerator.md)) |

### What SQL is supported

`SELECT` with projection, `WHERE`, `INNER`/`LEFT`/`RIGHT`/`FULL JOIN`, `UNION [ALL]`, correlated
subqueries, `GROUP BY`/`HAVING`, aggregates, `ORDER BY`, `DISTINCT`, `TOP`; `INSERT … VALUES`,
`UPDATE`, `DELETE` (VFP soft-delete — see [1. Reading tables](01-reading-tables.md#deleted-records));
`CREATE`/`ALTER`/`DROP TABLE`. Positional `?` and named `@`/`:` parameters. SQL `=` follows
**`SET ANSI`** (the `Ansi` connection-string keyword above), not `SET EXACT`.

### Register as a named provider

```csharp
DbProviderFactories.RegisterFactory("CrossVault.FoxDbf", FoxDbfProviderFactory.Instance);
```

Useful for tools that look up providers by name (config-driven data layers, some reporting tools)
rather than `new`-ing `FoxDbfConnection` directly.

## Next

[4. The microVFP runtime →](04-microvfp-runtime.md)
