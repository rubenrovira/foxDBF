# CrossVault.FoxDbf.Highlike

The **opt-in high-performance accelerator** for [CrossVault.FoxDbf](https://www.nuget.org/packages/CrossVault.FoxDbf).
It speeds up Rushmore-style queries on VFP/dBase tables **without ever changing the result** — the
answer is always identical to the Core optimizer and to a full table scan. Statistics and caches are
**hints only**: stale, missing, or even wrong stats can only change the *plan* (speed), never the
result set (the residual always confirms every candidate).

> Named after the **Hochgern** (Hoch→High, gern→like). Dependency direction is strictly
> `Highlike → Core` — referencing this package never changes Core behaviour unless you opt in.

## Features
- **`.stx` statistics sidecar** — per-tag NDV, min/max, null/deleted counts, **equi-depth histograms**
  and **most-common-values**, harvested in one sorted index walk. `Analyze(table)` (an explicit
  UPDATE STATISTICS) or lazy on first use. Staleness via reccount + last-update stamp.
- **Cost-based planner** — `EstimateRows` drives AND-ordering (selective-first), an index-vs-scan
  threshold, and cost-driven lifting of the low-selectivity guards.
- **Cross-query index cache** — decode each CDX tag once, reuse across queries; change-token +
  `FileSystemWatcher` (local) / token-poll (network) invalidation; thread-safe, bounded, disposable.

## Quick start

```csharp
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Highlike;

using var table = DbfTable.Open("orders.dbf").UseHighlike(new HighlikeOptions { EnableStatistics = true });

// Same API, same results as Core — just faster on repeated / large queries:
foreach (var rec in table.Query("AMOUNT > 1000 AND UPPER(NAME) = \"ACME\"").GetRecords(table))
{
    /* ... */
}
```

Targets `net10.0`. Pure managed (no native dependencies).
