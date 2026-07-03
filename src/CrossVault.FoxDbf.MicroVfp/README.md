# CrossVault.FoxDbf.MicroVfp

Published as **[CrossVault.FoxDbf.MicroVfp](https://www.nuget.org/packages/CrossVault.FoxDbf.MicroVfp)** — a
minimal, embeddable **Visual FoxPro 9 stored-procedure interpreter**. It runs real VFP9 `.prg`
source (the kind you'd extract from a database container's stored procedures, or write by hand)
directly in .NET, with no Visual FoxPro runtime installed.

It's a **tree-walking interpreter**, not a compiler: parse once (`PrgParser.Parse`), then execute
against a live [CrossVault.FoxDbf](https://www.nuget.org/packages/CrossVault.FoxDbf) `VfpSession` —
so `.prg` code that opens tables, seeks, scans, and writes records operates on the same engine as
the rest of this project (byte-compatible reads/writes, the same Rushmore-optimized queries).

## What it implements

- **Control flow**: `IF/ENDIF`, `DO CASE/ENDCASE`, `DO WHILE/ENDDO`, `FOR/ENDFOR`,
  `FOR EACH … IN <array>/ENDFOR`, `SCAN/ENDSCAN`, `EXIT`/`LOOP`, `RETURN`.
- **Scoping & preprocessing**: `LOCAL`/`PRIVATE`/`PUBLIC` with VFP's dynamic-scoping rules (a
  `PRIVATE` hides a same-named variable from an outer caller for the rest of the call chain),
  `DIMENSION`/`REDIMENSION` arrays, `CLEAR MEMORY`/`ALL`, `#DEFINE`/`#IF`/`#IFDEF`, and generalised
  `&var` / `&var.` macro substitution **executed at runtime**.
- **Procedures/functions**: `PROCEDURE`/`FUNCTION` definitions, `DO proc [WITH args]` and
  `=func(args)` call forms, `PARAMETERS`, by-value vs. by-reference (`DO … WITH`) passing, loading
  stored procedures straight from a `.dbc`.
- **Data access**: `USE`/`SELECT` work areas, `SEEK`/`GO`/`SKIP`, `REPLACE`/`DELETE`/`RECALL`/
  `INSERT`, `GATHER`/`SCATTER`, `APPEND FROM`/`COPY TO` (`.dbf`), `PACK`, `SUM`/`TOTAL`,
  `BEGIN`/`END TRANSACTION`/`ROLLBACK` (copy-on-write), and an embedded VFP-SQL `SELECT`.
- **Indexes**: `INDEX ON … TAG` (structural `.cdx`), `… TAG … OF <cdx>` (non-structural),
  `INDEX ON … TO <idx>` (standalone `.idx`), `SET ORDER`/`SET INDEX`/`USE … INDEX`, `REINDEX`,
  `DELETE TAG`, and tag introspection (`TAG()`/`TAGCOUNT()`/`KEY()`/`ORDER()`/`CDX()`/…).
- **Buffering & relations**: real optimistic buffering (`CURSORSETPROP`/`CURSORGETPROP('Buffering')`
  1-5, `TABLEUPDATE()`/`TABLEREVERT()`, `OLDVAL()`/`CURVAL()`/`GETFLDSTATE()`), plus
  `SET RELATION`/`SET SKIP` parent-child navigation (`RELATION()`/`TARGET()`).
- **Referential integrity**: the DBC's `RULE`s and generated insert/update/delete RI triggers
  auto-fire on writes, cascading exactly as VFP9.
- **Error handling**: `ON ERROR`, `AERROR()` (full 7-column result-array contract), `MESSAGE()`/`ERROR()`/`LINENO()`.
- A broad runtime function library: string/date/math built-ins (`TRANSFORM`, `PROPER`, `STREXTRACT`,
  `GETWORDNUM`, `SOUNDEX`, `STRCONV`, `TEXTMERGE`, …), array functions (`ACOPY`/`ADEL`/`AINS`/`ASORT`/
  `AELEMENT`/`AFIELDS`/…), and session/database state (`RECNO()`, `RECCOUNT()`, `ALIAS()`, `SELECT()`,
  `DBF()`, `DBC()`, `VARTYPE()`, `EVALUATE()`, …).

See **[the full function reference and VFP deviations](https://github.com/crossvault/foxDBF/blob/main/docs/04-microvfp-runtime.md)**
in `docs/04-microvfp-runtime.md`. `SET DATASESSION` is a **real** multi-data-session model: the host API
`VfpInterpreter.CreateDataSession()`/`ReleaseDataSession(n)` is the headless equivalent of a form with
`DataSession=2` (a `.prg` cannot create a private session, matching VFP), and `SET DATASESSION TO n`
switches among existing sessions — isolating work areas, record pointers, orders, relations, buffering,
byte-range locks and the session-scoped SETs (`DELETED`/`EXACT`/…) exactly as VFP9.
`RLOCK()`/`LOCK()`/`FLOCK()`/`UNLOCK`/`ISRLOCKED()`/`ISFLOCKED()` take **real** VFP-byte-compatible
byte-range locks on the live `.dbf` (honouring `SET REPROCESS` and `SET MULTILOCKS`), so a stored proc
that coordinates via `RLOCK` is mutually exclusive with a concurrent VFP client — only the `SET REPROCESS
TO … SECONDS` wait timing is approximated (a headless library cannot block a UI thread indefinitely).

The rest of the VFP9 command surface (low-level file I/O, the form/class model) is being built out
incrementally — this targets running real-world VFP9 business-logic stored procedures correctly, not 100%
language coverage on day one.

## Quick start

```csharp
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Expressions;   // VfpValue lives here

using var session = new VfpSession();
session.OpenDatabase(@"C:\data\shop.dbc");

var interp = new VfpInterpreter(session);
interp.LoadFile(@"C:\data\business_logic.prg");   // PROCEDURE/FUNCTION definitions

VfpValue result = interp.Call("CalcOrderTotal", VfpValue.Integer(1138));
Console.WriteLine(result.AsNumber);
```

Or run a self-contained snippet with no `.dbc` at all. There's no `?`/`??` console output, so return
the value from a `FUNCTION` and read it back with `Call(...)`:

```csharp
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Expressions;

var interp = new VfpInterpreter(new VfpSession());
interp.Execute("FUNCTION AddUp(a, b)\n RETURN a + b\nENDFUNC");
Console.WriteLine(interp.Call("AddUp", VfpValue.Integer(1), VfpValue.Integer(2)).AsNumber);   // 3
```

Targets `net10.0`.
