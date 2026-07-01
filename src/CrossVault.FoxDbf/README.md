# CrossVault.FoxDbf

An idiomatic .NET library for **reading and writing** Visual FoxPro and dBase database
files — `.dbf` tables, `.fpt`/`.dbt` memos, `.cdx`/`.idx` indexes, and `.dbc` database
containers. The primary target is Visual FoxPro, with dBase III/IV/V supported alongside.
Written output is **byte-compatible with the real Visual FoxPro 9 runtime** (verified against
it: created tables, indexes, and databases open in VFP9).

## Features

### Reading
- Header / version / column parsing for all xBase layouts (incl. FoxBase v0x02).
- Streaming record iteration and random access; deleted-record handling.
- Field decoding for `C N F I Y B D T L + G` (Currency/Numeric-with-decimals as `decimal`).
- Visual FoxPro **NULL** semantics via the hidden `_NullFlags` column, plus Varchar/Varbinary.
- Full code-page map with encoding selection (UTF-8 short-circuit, NOCPTRANS binary bypass).
- Memo files: FoxPro `.fpt` (big-endian) and dBase III/IV `.dbt` (little-endian).
- `.dbc` containers: resolves member tables via the `PROPERTY`/`.DCT` path and applies
  **long field names**.
- Header **recovery** for corrupted/zeroed headers, plus a force-version open.
- **Index reading** (`.cdx` compound / `.idx` single): tag enumeration in key order,
  typed key decode (numeric/date/datetime/integer/character), and `Seek`.
- Export helpers: CSV (RFC-4180, BOM) and JSON / Markdown / SQL-DDL schema.

### Writing
- Append / update / delete / recall records; field + record encoders (the exact inverse of
  the decoders), header maintenance, autoincrement, and `.fpt` memo append.
- `PACK` (physical delete + memo rebuild) and `ZAP`; **VFP-compatible byte-range locking**.
- **Create** a `.dbf` from scratch (≥ VFP6, version selected by feature) and `ALTER`
  (add/drop/modify columns) via an atomic whole-file rewrite.
- **Create** a `.dbc` database container (`.dbc` + `.DCT` + `.DCX` + member backlinks).
- **CDX writing / REINDEX**: bulk-build a compact B-tree index for a tag (key + FOR
  expressions, MACHINE/GENERAL collation).

### Expressions & query
- A VFP **expression engine** (`CrossVault.FoxDbf.Expressions`): hand-written lexer + Pratt
  parser compiled via `System.Linq.Expressions`, a broad function library, and byte-exact
  **MACHINE / GENERAL** collation.
- A **Rushmore-style** query optimizer: index-driven record bitmaps with a residual scan, honoring
  `SET EXACT` / `SET DELETED` / `SET OPTIMIZE`; index-optimizes Character/Date/DateTime as well as
  numeric keys; **late materialization** (reads only the surviving records); an adaptive sparse/dense
  bitmap; and a **SYS(3054)-style ShowPlan** (`DbfTable.ExplainQuery`). The result is always identical
  to a full scan.
- An optional **memory-mapped read backend** (`DbfOptions.ReadBackend`) for local files (falls back
  to `FileStream` on network shares).

For a cost-based planner with persisted statistics (NDV / histograms / most-common-values) and a
cross-query index cache, add the opt-in **[CrossVault.FoxDbf.Highlike](https://www.nuget.org/packages/CrossVault.FoxDbf.Highlike)**
package and call `table.UseHighlike()` — same results, faster on repeated / large queries.

## Quick start

```csharp
using CrossVault.FoxDbf;

// A single table
using var table = DbfTable.Open("customers.dbf");
foreach (var rec in table.Records)
{
    string? name = rec.GetString("name");
    decimal? bal = rec.GetDecimal("balance");
}

// A Visual FoxPro database container (long field names)
using var db = DbfDatabase.OpenFoxpro("app.dbc");
foreach (var t in db.TableNames)
    using (var member = db.OpenTable(t)) { /* ... */ }

// Export
table.ExportCsv("customers.csv");
```

```csharp
using CrossVault.FoxDbf.Write;

// Create a table, append a row, build an index
using (var w = DbfWriter.Create("people.dbf", new[]
{
    new DbfColumnDef("NAME", 'C', 30),
    new DbfColumnDef("AGE",  'I', 4),
}, new DbfCreateOptions { Overwrite = true }))
{
    w.AppendRecord(new Dictionary<string, object?> { ["NAME"] = "Ada", ["AGE"] = 36 });
    w.CreateTag(new CdxTagDefinition("NAMEIDX", "NAME"));   // structural .cdx
}

// Create a Visual FoxPro database container with member tables
DbfDatabaseBuilder.Create("shop.dbc", new[]
{
    new DbcTableSpec("customers", "customers.dbf", new[]
    {
        new DbfColumnDef("CUST_ID", 'I', 4),
        new DbfColumnDef("COMPANY", 'C', 30),
    }),
}, new DbcCreateOptions { Overwrite = true });
```

Targets `net10.0`.
