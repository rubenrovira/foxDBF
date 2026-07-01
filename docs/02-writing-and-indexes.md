# 2. Writing and indexes

**What you'll do:** create a table from scratch, append/update/delete records, build a `.cdx`
index, and reclaim space with `PACK`.

## Create a table and append records

```csharp
using CrossVault.FoxDbf.Write;

using var w = DbfWriter.Create("people.dbf", new[]
{
    new DbfColumnDef("NAME", 'C', 30),
    new DbfColumnDef("AGE",  'I', 4),
}, new DbfCreateOptions { Overwrite = true });

int recNo1 = w.AppendRecord(new Dictionary<string, object?> { ["NAME"] = "Ada", ["AGE"] = 36 });
int recNo2 = w.AppendRecord("Grace", 38);   // positional overload, columns in declared order
```

`DbfWriter.Create` picks the on-disk version byte based on the features you use (Memo/Varchar
columns bump it up automatically); `DbfCreateOptions.Overwrite` controls whether an existing file
at that path is replaced.

## Build a `.cdx` index

```csharp
w.CreateTag(new CdxTagDefinition("NAME_IDX", "NAME"));                             // simple key
w.CreateTag(new CdxTagDefinition("AGE_DESC", "AGE", descending: true));            // descending
w.CreateTag(new CdxTagDefinition("ADULTS", "NAME", forExpression: "AGE >= 18"));   // filtered (FOR clause)
```

Each `CreateTag` call bulk-builds a compact B-tree for that key expression over the table's
**current** contents — the same structural `.cdx` format Visual FoxPro reads, byte-for-byte.

## Update / delete / recall

```csharp
w.Delete(recNo1);     // flags the record (0x2A) — doesn't remove it yet
w.Recall(recNo1);     // un-flags it
w.Flush();            // ensure everything is on disk
```

## Reclaim space: `PACK`

```csharp
w.Pack();   // physically removes all flagged records, rebuilds the memo file, keeps indexes valid
```

`PACK` is a real structural rewrite (like Visual FoxPro's own `PACK`) — record numbers of surviving
records can shift. Only call it when you actually want that; most workloads just leave deleted
records flagged and `PACK` occasionally (or never), exactly like a real VFP application would.

## Alter an existing table's structure

```csharp
w.Alter(new[]
{
    new DbfColumnDef("NAME",  'C', 40),          // widen an existing column
    new DbfColumnDef("AGE",   'I', 4),
    new DbfColumnDef("EMAIL", 'C', 60),          // add a new column
});
```
`Alter` rewrites the whole file atomically (temp file + rename) — safe to call on a live table.

## Create a `.dbc` database container with member tables

```csharp
DbfDatabaseBuilder.Create("shop.dbc", new[]
{
    new DbcTableSpec("customers", "customers.dbf", new[]
    {
        new DbfColumnDef("CUST_ID", 'I', 4),
        new DbfColumnDef("COMPANY", 'C', 30),
    }),
    new DbcTableSpec("orders", "orders.dbf", new[]
    {
        new DbfColumnDef("ORDER_ID",  'I', 4),
        new DbfColumnDef("CUST_ID",   'I', 4),
    }),
}, new DbcCreateOptions { Overwrite = true });
```

This produces a real `.dbc` + `.DCT` + `.DCX` with correct member-table backlinks — open it with
`DbfDatabase.OpenFoxpro` (see [1. Reading tables](01-reading-tables.md)) just like a VFP9-created one.

## VFP-compatible locking

`DbfWriter` brackets mutations with VFP-compatible byte-range locks (`DbfOptions.LockMode`, default
`Shared`) — a real, running Visual FoxPro process sharing the same file sees the same lock
semantics (`RLOCK()`/`FLOCK()` byte ranges match exactly), so this library can coexist with a live
VFP9 install on the same table.

## Next

[3. SQL and the ADO.NET provider →](03-sql-and-ado-net.md)
