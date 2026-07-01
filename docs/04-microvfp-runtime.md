# 4. The microVFP runtime

**What you'll do:** parse and run real VFP9 `.prg` source — a self-contained snippet, a file of
`PROCEDURE`/`FUNCTION` definitions, or the stored procedures embedded in a `.dbc`.

`CrossVault.microVFP` is a **tree-walking interpreter**: it parses `.prg` text into an AST once,
then executes it against a live [`VfpSession`](03-sql-and-ado-net.md) — so table access inside your
`.prg` code (`USE`, `SEEK`, `SCAN`, `REPLACE`, …) runs on the exact same engine as everything else
in this project.

## Run a self-contained snippet

No table, no `.dbc` — just VFP language constructs:

```csharp
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

var interp = new VfpInterpreter(new VfpSession());
interp.Execute(@"
    LOCAL x, i, total
    total = 0
    FOR i = 1 TO 5
        total = total + i
    ENDFOR
    ? total
");
```

## Load and call a procedure/function

```csharp
using var session = new VfpSession();
session.OpenDatabase(@"C:\data\shop.dbc");

var interp = new VfpInterpreter(session);
interp.LoadFile(@"C:\data\business_logic.prg");   // parses PROCEDURE/FUNCTION definitions

VfpValue result = interp.Call("CalcOrderTotal", VfpValue.Integer(1138));
Console.WriteLine(result.AsNumber);
```

`Call(name, args)` is a **by-value** call (matches `=Func(args)`/`(...)` call syntax in VFP — see
by-value vs. by-reference below). Parameters are `VfpValue` — VFP's tagged value type
(`VfpValue.Integer(...)`, `VfpValue.Character(...)`, `VfpValue.Logical(...)`, `VfpValue.Null`, …).

## Load stored procedures straight from a `.dbc`

Many real VFP9 applications keep their business logic as the database container's stored
procedures rather than a loose `.prg` file:

```csharp
var interp = new VfpInterpreter(session);   // session already has a database open
interp.LoadStoredProceduresFromDatabase();
VfpValue result = interp.Call("NewID", VfpValue.Character("orders"));
```

## Parse without executing

If you only need the AST (tooling, static analysis, validating that a `.prg` corpus parses cleanly
before running it):

```csharp
using CrossVault.FoxDbf.MicroVfp;

PrgProgram program = PrgParser.Parse(prgSourceText);
// or: PrgParser.ParseFile(@"C:\data\business_logic.prg");
```

## What's implemented

- **Control flow**: `IF/ENDIF`, `DO CASE/ENDCASE`, `DO WHILE/ENDDO`, `FOR/ENDFOR`, `SCAN/ENDSCAN`,
  `EXIT`/`LOOP`, `RETURN`.
- **Scoping**: `LOCAL`/`PRIVATE`/`PUBLIC` with VFP's dynamic-scoping rules — a `PRIVATE` hides a
  same-named variable from an outer caller for the rest of the call chain (not lexical scoping).
  `DIMENSION`/`REDIMENSION` memory arrays (preserve existing elements on resize; new elements
  `.F.`-fill), `RELEASE`.
- **Parameters**: `PARAMETERS`/`LPARAMETERS`, by-value (`=Func(args)`/`(...)`) vs. by-reference
  (`DO proc WITH args`) passing.
- **Data access**: `USE`/`SELECT` work areas, `SEEK`/`GO`/`SKIP`, `REPLACE`/`DELETE`/`RECALL`/
  `INSERT`, `SUM`, `BEGIN`/`END TRANSACTION`/`ROLLBACK` (copy-on-write), an embedded VFP-SQL
  `SELECT` (routed through the same [SQL engine](03-sql-and-ado-net.md) as everything else).
- **Indexing**: `INDEX ON … TAG` (into the table's structural `.cdx`), `SET ORDER TO`, `REINDEX` —
  see [Deviations from VFP](#deviations-from-vfp) for the forms that are refused rather than attempted.
- **Error handling**: `ON ERROR`, `AERROR()` (the full 7-column result-array contract), `MESSAGE()`/
  `ERROR()`/`LINENO()`.

## Function reference

Every function below runs as real VFP semantics, not a stub — see
[Deviations from VFP](#deviations-from-vfp) for the handful that are intentionally simplified.

**String**

| Function | Notes |
|---|---|
| `UPPER()`, `LOWER()` | |
| `TRIM()`, `LTRIM()`, `RTRIM()`, `ALLTRIM()` | |
| `LEFT()`, `RIGHT()`, `SUBSTR()` | |
| `LEN()` | |
| `AT()`, `ATC()` (case-insensitive `AT()`) | |
| `RAT()` | |
| `OCCURS()` | count of occurrences |
| `STUFF()`, `STRTRAN()` | |
| `PADL()`, `PADR()`, `PADC()` | |
| `SPACE()`, `REPLICATE()` | |
| `CHR()`, `ASC()` | |
| `CHRTRAN()` | |
| `STR()`, `STRZERO()`, `VAL()` | |
| `ISDIGIT()`, `ISALPHA()` | first-character checks, as in VFP |

**Date / time**

| Function | Notes |
|---|---|
| `DATE()`, `DATETIME()` | |
| `CTOD()`, `CTOT()`, `DTOC()`, `DTOS()`, `TTOC()` | |
| `YEAR()`, `MONTH()`, `DAY()`, `DOW()`, `CDOW()`, `CMONTH()` | |
| `GOMONTH()` | |

**Math / logic**

| Function | Notes |
|---|---|
| `ABS()`, `INT()`, `ROUND()`, `MOD()`, `MAX()`, `MIN()` | |
| `IIF()`, `BETWEEN()`, `INLIST()` | |
| `EMPTY()`, `ISNULL()` | |
| `TYPE()` | returns a VFP type code (`C`, `N`, `L`, `D`, …) |
| `EVALUATE()` / `EVAL()` | evaluates a text expression at runtime |

**Arrays**

| Function | Notes |
|---|---|
| `ALEN()` | total elements (no dimension arg), rows (dim `1`), or columns (dim `2`) |

**Work area / record state** — these are the *session-aware* versions (they read the live cursor,
not a context-free default), and take precedence over any same-named generic function above:

| Function | Notes |
|---|---|
| `SELECT()`, `USED()`, `ALIAS()` | |
| `DBF()` | current table's source path; `DBC()` is a stub — see deviations |
| `RECNO()`, `RECCOUNT()` | |
| `EOF()`, `BOF()`, `FOUND()`, `DELETED()` | |
| `SEEK()` | function form of the `SEEK` command, returns success as `.T.`/`.F.` |
| `OLDVAL()`, `CURVAL()` | transaction-buffer field snapshots |
| `PCOUNT()` / `PARAMETERS()` | count of arguments actually passed to the current call |
| `PROGRAM()` | current procedure name, or `n` levels up the call stack |

**Error handling**

| Function | Notes |
|---|---|
| `AERROR()` | fills the array with the full 7-column contract from the retained last error |
| `ERROR()`, `MESSAGE()`, `LINENO()` | |
| `ON("ERROR")` | reads back the installed `ON ERROR` handler text |

**System**

| Function | Notes |
|---|---|
| `SYS(0)` | `"<machine> # <user>"` — verified byte-for-byte against `vfp9.exe`'s own format |
| `SYS(1)` | today's Julian day number |
| `SYS(2007, cExpr)` | CRC-16/CCITT checksum |
| `SYS(2015)` | a unique procedure/object name (`_` + 9 hex chars) |
| other `SYS(n)` codes | return `""` — see deviations |
| `SECONDS()` | |
| `COCREATEGUID()` | |
| `TXNLEVEL()` | transaction nesting depth |
| `RLOCK()`/`LOCK()`, `FLOCK()`, `ISRLOCKED()`/`ISFLOCKED()`, `CURSORGETPROP()`, `GETFLDSTATE()` | see deviations — modelled for a single-session interpreter, not real contention |
| `MESSAGEBOX()` | stub — see deviations |

## Deviations from VFP

microVFP runs real VFP9 `.prg` source, but it is a **headless, single-session interpreter for
business-logic stored procedures** — not a drop-in replacement for the full VFP9 IDE/runtime. The
differences below are deliberate, verified simplifications, not bugs to be worked around:

- **`LOCATE`/`CONTINUE` parse but do not move the record pointer.** The syntax is recognized (a
  `.prg` containing `LOCATE FOR …` still parses cleanly), but neither command currently performs the
  scan — `LOCATE` is a no-op today, not a search. Use `SCAN FOR … / EXIT / ENDSCAN` instead, which
  *is* fully implemented and covers the same use case.
- **Locking is modelled, not contested.** `RLOCK()`/`LOCK()`/`FLOCK()` always return `.T.` and
  `ISRLOCKED()`/`ISFLOCKED()` always return `.F.` — a single in-process interpreter never sees lock
  contention, so a lock is always granted immediately. This is about the *PRG-level lock functions*
  only: the underlying `CrossVault.FoxDbf` table API has real byte-range `RLOCK()`/`FLOCK()` locking
  that interoperates with a live VFP9 process (see
  [2. Writing and indexes](02-writing-and-indexes.md#vfp-compatible-locking)) — it's just not wired
  through these interpreter-level function calls, since the interpreter itself is always the sole
  writer in its own process.
- **`INDEX ON … TAG` only targets the structural `.cdx`.** `INDEX ON … TO <idx>` (a standalone
  `.idx` file) and `INDEX … TAG … OF <cdx>` (a non-structural `.cdx`) are refused with an explicit,
  catchable `MicroVfpRuntimeException` rather than silently attempted or silently ignored.
- **`&macro` / `&macro.` substitution is parsed but not expanded.** A macro reference is captured
  verbatim so surrounding code still parses, but it is never resolved/executed at runtime — with one
  specific exception: `ON ERROR &lcHandler` *is* installed and re-evaluated, because that's the one
  macro form real-world RI/stored-procedure corpora actually rely on.
- **An unrecognized command is captured, not rejected — and not executed either.** Any command verb
  the parser doesn't model yet still parses (so a whole `.prg` file continues to load even if it uses
  a handful of exotic commands), but it silently does nothing at runtime. A `.prg` parsing cleanly is
  therefore not proof that every line in it actually ran — check
  [What's implemented](#whats-implemented) and the [function reference](#function-reference) above
  for what's real.
- **A few functions are intentional stubs**, always returning the same constant: `MESSAGEBOX()`
  always returns `6` (`IDYES`) since there is no UI to show; `DBC()` always returns `""`;
  `CURSORGETPROP()`/`GETFLDSTATE()` return constants since microVFP doesn't model VFP's buffering
  modes; unimplemented `SYS(n)` codes return `""` rather than their real per-code VFP9 behavior.

Referential-integrity trigger auto-firing and the remaining VFP9 command surface (low-level file
I/O, `SET RELATION`, …) are being built out incrementally, prioritized by what real `.prg`
business-logic corpora actually use — the goal is correctly running real-world stored procedures,
not 100% language coverage on day one.

## Next

[5. The Highlike accelerator →](05-highlike-accelerator.md)
