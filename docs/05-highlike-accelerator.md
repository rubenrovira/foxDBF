# 5. The Highlike accelerator

**What you'll do:** opt into cost-based query acceleration for repeated/large queries — same API,
same results as the Core optimizer, just faster once statistics/caches warm up.

Highlike (`CrossVault.FoxDbf.Highlike` namespace, bundled in the `CrossVault.FoxDbf` package) never
changes what a query returns — only how fast the engine gets there. Statistics and caches are
**hints only**: stale, missing, or even wrong stats can only change the *plan*, never the result
set (the residual scan always confirms every candidate row, same as Core).

## Turn it on

```csharp
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Highlike;

using var table = DbfTable.Open("orders.dbf")
    .UseHighlike(new HighlikeOptions { EnableStatistics = true });

// Same call as without Highlike — just faster on repeated / large queries:
var result = table.Query("AMOUNT > 1000 AND UPPER(NAME) = \"ACME\"");
foreach (var rec in result.GetRecords(table))
{
    /* ... */
}
```

Via the ADO.NET provider ([3. SQL and ADO.NET](03-sql-and-ado-net.md)), it's a connection-string
keyword instead of a code change:

```
Data Source=C:\data\shop.dbc;Accelerator=Highlike
```

## What it adds over Core

- **`.stx` statistics sidecar** — per-tag NDV (number of distinct values), min/max, null/deleted
  counts, equi-depth histograms, and most-common-values, harvested in one sorted index walk.
  Computed lazily on first use, or explicitly:
  ```csharp
  StxStatistics stats = HighlikeStatistics.Analyze(table);
  ```
  (an explicit `UPDATE STATISTICS`-equivalent — call it after a bulk load if you want fresh stats
  immediately rather than on next use). Staleness is tracked via record count + last-update stamp.
- **Cost-based planner** — uses those statistics to decide `AND`-clause ordering (most selective
  first), whether an index seek beats a full scan for a given predicate, and when to lift
  low-selectivity guard clauses.
- **Cross-query index cache** — decodes each `.cdx` tag once and reuses it across queries instead
  of re-reading/re-decoding per query; invalidated via a change-token (`FileSystemWatcher` locally,
  token-polling on a network share).

## Tuning knobs (`HighlikeOptions`)

| Property | Default | Meaning |
|---|---|---|
| `EnableStatistics` | `false` | compute/use the `.stx` sidecar at all |
| `IndexVsScanThreshold` | `0.5` | selectivity fraction above which the planner prefers a full scan over a seek |
| `EnableIndexCache` | `true` | reuse decoded `.cdx` tags across queries |
| `MaxCachedTables` | `256` | bound on the cross-query cache's tracked tables |
| `HistogramBuckets` / `McvCount` | `16` / `16` | equi-depth histogram / most-common-value list size per statistics harvest |

For most applications, `new HighlikeOptions { EnableStatistics = true }` (the defaults for
everything else) is enough — reach for the other knobs only once you've measured a specific query
pattern that needs tuning.

## Back to the start

[1. Reading tables](01-reading-tables.md) · [2. Writing and indexes](02-writing-and-indexes.md) ·
[3. SQL and ADO.NET](03-sql-and-ado-net.md) · [4. The microVFP runtime](04-microvfp-runtime.md)
