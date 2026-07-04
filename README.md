# CrossVault.FoxDbf

A .NET 10 toolkit for **Visual FoxPro and dBase data**: read, write, index, and query `.dbf`/`.dbc`
files with byte-compatible output (verified against the real Visual FoxPro 9 runtime), an ADO.NET
provider so any .NET data tool can speak FoxPro/dBase over SQL, and a minimal embeddable VFP9
stored-procedure interpreter for running real `.prg` business logic without a Visual FoxPro
installation.

No native dependencies, no COM/OLE DB interop, no Visual FoxPro IDE required — pure managed .NET.

## Packages

| Package | What it's for |
|---|---|
| **[CrossVault.FoxDbf](src/CrossVault.FoxDbf/README.md)** | The core: read/write `.dbf`/`.fpt`/`.cdx`/`.dbc`, the VFP expression engine, a Rushmore-style query optimizer, a SQL parser + executor, and the opt-in Highlike performance accelerator. Everything below builds on this. |
| **[CrossVault.FoxDbf.Data](src/CrossVault.FoxDbf.Data/README.md)** | An ADO.NET data provider (`DbConnection`/`DbCommand`/`DbDataReader`, plus `DbDataSource` and `DbBatch`) — use FoxPro/dBase data from Dapper, raw ADO.NET, reporting tools, LINQPad. Runs a `.dbc`'s stored procedures/UDFs, and can opt in (`EnforceRules`) to the full VFP write model (DEFAULTs/RULEs/RI triggers) on writes. |
| **[CrossVault.FoxDbf.MicroVfp](src/CrossVault.FoxDbf.MicroVfp/README.md)** | A minimal, embeddable interpreter for real VFP9 `.prg` stored-procedure/business-logic code — for when you just need to *run* FoxPro logic, not a full data provider. |

`CrossVault.FoxDbf.Expressions` (the shared expression engine) has [its own README](src/CrossVault.FoxDbf.Expressions/README.md)
but isn't published separately — it ships bundled inside `CrossVault.FoxDbf`.

## Why

Visual FoxPro has been out of mainstream support for years, but a lot of real, working line-of-business
data still lives in `.dbf`/`.dbc` files — and a lot of that data's *behavior* (defaults, validation
rules, referential integrity, business logic) lives in VFP9 stored procedures, not just the data
itself. This project's goal is to let that data — and that logic — keep running on modern .NET,
byte-compatibly with what a real VFP9 install would produce, without requiring VFP9 itself.

## Architecture at a glance

```
                     ┌─────────────────────────────┐
                     │      CrossVault.FoxDbf       │   core read/write engine
                     │  (+ Expressions, SQL, opt-in │   + expression engine
                     │   Highlike accelerator)      │   + SQL parser/executor
                     └───────────────┬─────────────┘   + opt-in accelerator
                                     │
                ┌────────────────────┼────────────────────┐
                │                                          │
   ┌────────────▼─────────────┐               ┌────────────▼─────────────┐
   │  CrossVault.FoxDbf.Data   │               │     CrossVault.FoxDbf.MicroVfp   │
   │   (ADO.NET provider)      │──────────────▶│  (VFP9 .prg interpreter)  │
   └───────────────────────────┘   uses for     └───────────────────────────┘
                                  stored procs
```

## Quick start

```csharp
using CrossVault.FoxDbf;

using var table = DbfTable.Open("customers.dbf");
foreach (var rec in table.Records)
    Console.WriteLine(rec.GetString("name"));
```

```csharp
using CrossVault.FoxDbf.Data;

using var cn = new FoxDbfConnection(@"Data Source=C:\data\shop.dbc;Deleted=on");
cn.Open();
using var cmd = cn.CreateCommand();
cmd.CommandText = "SELECT company_name FROM customer WHERE country = 'Germany'";
using var r = cmd.ExecuteReader();
while (r.Read()) Console.WriteLine(r.GetString(0));
```

```csharp
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Expressions;   // VfpValue

var interp = new VfpInterpreter(new VfpSession());
interp.Execute("FUNCTION AddUp(a, b)\n RETURN a + b\nENDFUNC");
VfpValue sum = interp.Call("AddUp", VfpValue.Integer(1), VfpValue.Integer(2));
Console.WriteLine(sum.AsNumber);   // 3
```

See [`docs/`](docs/) for a step-by-step walkthrough of each area (reading, writing/indexing,
SQL/ADO.NET, the microVFP runtime, the Highlike accelerator), and each package's own README for
full API detail.

## Status

Actively developed. Read/write/index/SQL/ADO.NET are exercised against real Visual FoxPro 9 as an
oracle (byte-for-byte comparisons, not just "looks right"), including **real byte-range record
locking** proven against a live VFP9 client and a **multi-datasession** model. The microVFP
interpreter runs the data side of real stored-procedure business logic — control flow, buffering,
transactions, `LOCATE`/`CONTINUE`, indexing, referential integrity, low-level file I/O and a broad
VFP9 function library — with the remaining gaps largely confined to the IDE-facing surface
(forms/classes, `.mem` interop, non-DBF import/export, GUI-bound functions), plus view buffering and
multi-user optimistic conflict detection.

## License

[MIT](LICENSE).
