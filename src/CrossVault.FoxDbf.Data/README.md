# CrossVault.FoxDbf.Data

An **ADO.NET data provider** for Visual FoxPro / dBase tables and database containers. Query
FoxPro/dBase data from Dapper, raw ADO.NET, reporting tools and LINQPad — using SQL. Depends on
[CrossVault.FoxDbf](https://www.nuget.org/packages/CrossVault.FoxDbf) (core + SQL engine) and
[CrossVault.microVFP](https://www.nuget.org/packages/CrossVault.microVFP) (stored-procedure/UDF
execution via `EnforceRules`) — both are pulled in automatically as NuGet dependencies.

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
| `EnforceRules` (alias `EnforceRI`) | `on` / `off` (default `off`) | opt in to the VFP write model on writes — see below |

## Supported SQL

`SELECT` with projection, `WHERE`, `INNER`/`LEFT`/`RIGHT`/`FULL JOIN`, `UNION [ALL]`, correlated
subqueries, `GROUP BY`/`HAVING`, aggregates, `ORDER BY`, `DISTINCT`, `TOP`; `INSERT … VALUES`,
`UPDATE`, `DELETE` (VFP soft-delete); `CREATE`/`ALTER`/`DROP TABLE`; the VFP work-area model via
`USE` / `SELECT` commands. Positional `?` and named `@`/`:` parameters. Real transactions
(`BeginTransaction` → atomic commit/rollback), `DataAdapter`/`CommandBuilder`, and `GetSchema`
metadata collections.

## Stored procedures, UDFs, and opt-in rule enforcement

With a `.dbc` open, the provider runs the container's stored procedures / UDFs on the embedded
[microVFP](https://www.nuget.org/packages/CrossVault.microVFP) interpreter (sharing the connection's
work-area session):

```csharp
using var cmd = cn.CreateCommand();
cmd.CommandType = CommandType.StoredProcedure;   // System.Data
cmd.CommandText = "NewID";                        // a stored procedure in the .dbc
var p = cmd.CreateParameter(); p.ParameterName = "alias"; p.Value = "orders"; cmd.Parameters.Add(p);
object? nextId = cmd.ExecuteScalar();             // the SP's RETURN value; side effects persist
```

An ad-hoc microVFP expression works too — a `CommandText` starting with `?` or `=` (e.g.
`"?UPPER('abc')"`, `"=1 + 2"`) is evaluated directly.

Add `EnforceRules=on` (alias `EnforceRI`) to the connection string, with a `.dbc` open, and
`INSERT`/`UPDATE`/`DELETE` run the full VFP write model — field `DEFAULT`s → field `RULE`s →
`NOT NULL` / type / width → the record `RULE` → unique / candidate / primary-key → the bound
referential-integrity trigger — atomically, exactly as VFP9 (`UPDATE` has no `DEFAULT` phase).
Default is **off** (the raw DML path, byte-for-byte unchanged), so existing code is unaffected
unless you opt in.

## Modern factories: `DbDataSource` and `DbBatch`

`FoxDbfDataSource` (a `DbDataSource`, .NET 7+) is a connection factory for dependency injection —
register one as a singleton and resolve connections/commands from it. `FoxDbfBatch` (a `DbBatch`,
.NET 6+) runs several commands over one connection (`ExecuteReader` exposes the first result set and
`NextResult()` walks the rest; `ExecuteNonQuery` sums affected counts).

```csharp
using var source = new FoxDbfDataSource(@"Data Source=C:\data\tastrade.dbc;Deleted=on");
using var conn = source.OpenConnection();

using var batch = conn.CreateBatch();
batch.BatchCommands.Add(new FoxDbfBatchCommand("SELECT company_name FROM customer WHERE country = 'Germany'"));
batch.BatchCommands.Add(new FoxDbfBatchCommand("SELECT company_name FROM customer WHERE country = 'USA'"));
using var r = batch.ExecuteReader();   // first set; r.NextResult() → second set
```

## Register with `DbProviderFactories`

```csharp
DbProviderFactories.RegisterFactory("CrossVault.FoxDbf", FoxDbfProviderFactory.Instance);
```

Output is byte-compatible with the real Visual FoxPro 9 runtime. MIT licensed.
