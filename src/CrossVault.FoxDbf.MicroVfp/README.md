# CrossVault.FoxDbf.MicroVfp

Published as **[CrossVault.microVFP](https://www.nuget.org/packages/CrossVault.microVFP)** — a
minimal, embeddable **Visual FoxPro 9 stored-procedure interpreter**. It runs real VFP9 `.prg`
source (the kind you'd extract from a database container's stored procedures, or write by hand)
directly in .NET, with no Visual FoxPro runtime installed.

It's a **tree-walking interpreter**, not a compiler: parse once (`PrgParser.Parse`), then execute
against a live [CrossVault.FoxDbf](https://www.nuget.org/packages/CrossVault.FoxDbf) `VfpSession` —
so `.prg` code that opens tables, seeks, scans, and writes records operates on the same engine as
the rest of this project (byte-compatible reads/writes, the same Rushmore-optimized queries).

## What it implements

- **Control flow**: `IF/ENDIF`, `DO CASE/ENDCASE`, `DO WHILE/ENDDO`, `FOR/ENDFOR`, `SCAN/ENDSCAN`,
  `EXIT`/`LOOP`, `RETURN`.
- **Scoping**: `LOCAL`/`PRIVATE`/`PUBLIC` with VFP's dynamic-scoping rules (a `PRIVATE` hides a
  same-named variable from an outer caller for the rest of the call chain), `PARAMETERS`, by-value
  vs. by-reference (`DO ... WITH`) parameter passing.
- **Procedures/functions**: `PROCEDURE`/`FUNCTION` definitions, `DO proc [WITH args]` and
  `=func(args)` call forms, loading stored procedures straight from a `.dbc`.
- **Data access**: `USE`/`SELECT` work areas, `SEEK`/`GO`/`SKIP`, `REPLACE`/`DELETE`/
  `RECALL`/`INSERT`, `BEGIN`/`END TRANSACTION`/`ROLLBACK` (copy-on-write), `INDEX ON … TAG`
  (structural `.cdx`).
- **Error handling**: `ON ERROR`, `AERROR()` (full result-array contract), `MESSAGE()`/`ERROR()`/`LINENO()`.
- ~90 runtime functions: string/date/math built-ins plus session-aware state functions
  (`RECNO()`, `RECCOUNT()`, `ALIAS()`, `SELECT()`, `EOF()`/`BOF()`, `TYPE()`, `EVALUATE()`, …).

See **[the full function reference and VFP deviations](https://github.com/crossvault/foxDBF/blob/main/docs/04-microvfp-runtime.md)**
in `docs/04-microvfp-runtime.md` — notably, `LOCATE`/`CONTINUE` currently parse but don't move the
record pointer (use `SCAN`/`ENDSCAN` instead), and locking (`RLOCK()`/`FLOCK()`) is modelled for a
single-session interpreter rather than contested.

Referential-integrity trigger execution and the rest of the VFP9 command surface (low-level file
I/O, `SET RELATION`, …) are being built out incrementally — this targets running real-world VFP9
business-logic stored procedures correctly, not 100% language coverage on day one.

## Quick start

```csharp
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

using var session = new VfpSession();
session.OpenDatabase(@"C:\data\shop.dbc");

var interp = new VfpInterpreter(session);
interp.LoadFile(@"C:\data\business_logic.prg");   // PROCEDURE/FUNCTION definitions

VfpValue result = interp.Call("CalcOrderTotal", VfpValue.Integer(1138));
```

Or run a self-contained snippet with no `.dbc` at all:

```csharp
var interp = new VfpInterpreter(new VfpSession());
interp.Execute(@"
    LOCAL x
    x = 1 + 2
    ? x
");
```

Targets `net10.0`.
