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

### Indexes stay current on every write

Once a structural `.cdx` tag exists, the writer **maintains it incrementally** — every
`AppendRecord`/`UpdateRecord` edits each open tag in place (an `O(log n)` B-tree insert/remove),
exactly as Visual FoxPro does. You do **not** need a `REINDEX` after a write: a `REPLACE` (update)
followed by a `SEEK`/index-optimized query finds the row immediately. (`Delete`/`Recall` deliberately
do **no** index work — VFP soft-deletes leave a record's key in the index until you `PACK`.)

```csharp
w.AppendRecord(new Dictionary<string, object?> { ["NAME"] = "Grace", ["AGE"] = 38 });  // after CreateTag
// The NAME_IDX tag already reflects "Grace" — no REINDEX. An index-optimized query finds it:
using var table = DbfTable.Open("people.dbf");
foreach (var rec in table.Query("NAME = \"Grace\"").GetRecords(table))
    Console.WriteLine(rec.GetInt32("age"));   // 38
```

(`REINDEX` is still available for a full rebuild — e.g. after loading a table whose `.cdx` was
deleted or is stale — but ordinary appends/updates no longer require it.)

### Add or drop a single tag without touching the others

Adding a further tag to an existing `.cdx` appends it as its **own** pages with a single
tag-directory splice — the sibling tags are not re-read or rewritten, so only the new tag costs a
table scan. Dropping a tag unlinks just that one (the file itself is deleted only when its **last**
tag goes, matching VFP's `DELETE TAG ALL`):

```csharp
w.CreateTag(new CdxTagDefinition("NAME_UP", "UPPER(NAME)"));  // appended to the existing people.cdx
int remaining = w.DeleteTagsIn("people.cdx", structural: true, new[] { "AGE_DESC" });   // drop one tag
```

## Update / delete / recall

```csharp
w.Delete(recNo1);     // flags the record (0x2A) — doesn't remove it yet
w.Recall(recNo1);     // un-flags it
w.Flush();            // ensure everything is on disk
```

## Reclaim space: `PACK` / `ZAP`

```csharp
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Write;

using var w = DbfWriter.Open(
    "people.dbf",
    new DbfOptions { LockMode = LockMode.Exclusive });

w.Pack();   // physically removes all flagged records and rebuilds the memo file
// w.Zap(); // removes all records while keeping the table structure
```

If you create the table in the same session, create the writer with exclusive locking before
compaction:

```csharp
using var w = DbfWriter.Create("scratch.dbf", new[]
{
    new DbfColumnDef("NAME", 'C', 30),
}, new DbfCreateOptions
{
    Overwrite = true,
    LockMode = LockMode.Exclusive,
});

w.Zap();
```

`PACK` and `ZAP` are real structural rewrites (like Visual FoxPro's own commands) and require
`LockMode.Exclusive`. A default/shared writer refuses before touching the file and throws
`DbfWriteException` with VFP error number `110` ("File must be opened exclusively.").

With `PACK`, record numbers of surviving records can shift. `PACK`/`ZAP` also invalidate the
structural `.cdx` sidecar because record-number keys can become stale; rebuild indexes afterward
with VFP `REINDEX`, `DbfWriter.Reindex`, or tag creation as appropriate. Most workloads just leave
deleted records flagged and `PACK` occasionally (or never), exactly like a real VFP application
would.

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
