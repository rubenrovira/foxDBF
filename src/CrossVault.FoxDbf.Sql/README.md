# CrossVault.FoxDbf.Sql

The VFP-SQL engine for [CrossVault.FoxDbf](https://www.nuget.org/packages/CrossVault.FoxDbf): a
Visual FoxPro SQL parser + AST, a VFP **work-area data session**, and an executor over the Core
Rushmore optimizer and writer. It is reused by the
[CrossVault.FoxDbf.Data](https://www.nuget.org/packages/CrossVault.FoxDbf.Data) ADO.NET provider.

Most users want **CrossVault.FoxDbf.Data** (ADO.NET) instead of using this layer directly.

## What it does

- **`SqlParser.Parse(sql)`** → an AST. Every scalar/predicate fragment is parsed by the shared VFP
  expression engine (`VfpExpression`) — one expression grammar, used by indexes, filters, and SQL.
- **`VfpSession`** — the per-connection VFP work-area model: `USE` / `SELECT` work areas, open a
  `.dbc` (long field names) or a directory of free `.dbf` or a single `.dbf`, `alias.field`
  resolution, auto-open of tables named in a query.
- **Executor** — `SELECT` (projection, `WHERE` pushed to Rushmore, `GROUP BY`/`HAVING`, aggregates,
  `ORDER BY`, `DISTINCT`, `TOP`, `INNER`/`LEFT JOIN`) and DML (`INSERT … VALUES`, `UPDATE … SET`,
  `DELETE` = VFP soft-delete). Returns a `SqlResult` (column schema + streamed rows, or affected count).

## Semantics

SQL `=` follows **`SET ANSI`** (not `SET EXACT`); `DELETE` is a soft delete; `UPDATE`/`DELETE`
without a `WHERE` affect all rows. Results are verified against a brute-force oracle and the real
Visual FoxPro 9 runtime.

MIT licensed.
