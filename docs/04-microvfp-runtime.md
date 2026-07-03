# 4. The microVFP runtime

**What you'll do:** parse and run real VFP9 `.prg` source — a self-contained snippet, a file of
`PROCEDURE`/`FUNCTION` definitions, or the stored procedures embedded in a `.dbc`.

`CrossVault.FoxDbf.MicroVfp` is a **tree-walking interpreter**: it parses `.prg` text into an AST once,
then executes it against a live [`VfpSession`](03-sql-and-ado-net.md) — so table access inside your
`.prg` code (`USE`, `SEEK`, `SCAN`, `REPLACE`, …) runs on the exact same engine as everything else
in this project.

## Run a self-contained snippet

No table, no `.dbc` — just VFP language constructs. Define a `FUNCTION`, then read its result back in
C# with `Call(...)` (there is no `?`/`??` console output — see [Deviations](#deviations-from-vfp)):

```csharp
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Expressions;   // VfpValue lives here

var interp = new VfpInterpreter(new VfpSession());
interp.Execute(@"
    FUNCTION SumTo(n)
        LOCAL i, running
        running = 0
        FOR i = 1 TO n
            running = running + i
        ENDFOR
        RETURN running
    ENDFUNC
");

VfpValue result = interp.Call("SumTo", VfpValue.Integer(5));
Console.WriteLine(result.AsNumber);   // 15
```

## Load and call a procedure/function

```csharp
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Expressions;   // VfpValue lives here

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

- **Control flow**: `IF/ENDIF`, `DO CASE/ENDCASE`, `DO WHILE/ENDDO`, `FOR/ENDFOR`,
  `FOR EACH … IN <array>/ENDFOR`, `SCAN/ENDSCAN`, `EXIT`/`LOOP`, `RETURN`.
- **Scoping**: `LOCAL`/`PRIVATE`/`PUBLIC` with VFP's dynamic-scoping rules — a `PRIVATE` hides a
  same-named variable from an outer caller for the rest of the call chain (not lexical scoping).
  `DIMENSION`/`REDIMENSION` memory arrays (preserve existing elements on resize; new elements
  `.F.`-fill), `RELEASE`, `CLEAR MEMORY`/`CLEAR ALL`.
- **Preprocessing / macros**: `#DEFINE`/`#UNDEF`, `#IF`/`#IFDEF`/`#IFNDEF`, and generalised
  `&var` / `&var.` **macro substitution executed at runtime** (a `&`-line is expanded to the
  variable's current text and run).
- **Parameters**: `PARAMETERS`/`LPARAMETERS`, by-value (`=Func(args)`/`(...)`) vs. by-reference
  (`DO proc WITH args`) passing.
- **Data access**: `USE`/`SELECT` work areas, `SEEK`/`GO`/`SKIP`, `REPLACE`/`DELETE`/`RECALL`/
  `INSERT`, `GATHER`/`SCATTER`, `APPEND FROM`/`COPY TO` (`.dbf`), `PACK`, `SUM`/`TOTAL`,
  `BEGIN`/`END TRANSACTION`/`ROLLBACK` (copy-on-write), and an embedded VFP-SQL `SELECT` (routed
  through the same [SQL engine](03-sql-and-ado-net.md) as everything else).
- **Parent-child navigation**: `SET RELATION TO <key> INTO <alias>` and `SET SKIP TO` — moving the
  parent's record pointer repositions each related child; `RELATION()`/`TARGET()` read the links back.
- **Table / row buffering**: `CURSORSETPROP('Buffering', 1-5)` / `CURSORGETPROP`, `TABLEUPDATE()` /
  `TABLEREVERT()`, and `OLDVAL()`/`CURVAL()`/`GETFLDSTATE()` — a real optimistic-buffering model, not
  constants.
- **Indexing**: `INDEX ON … TAG` (structural `.cdx`), `INDEX ON … TAG … OF <cdx>` (non-structural
  `.cdx`), `INDEX ON … TO <idx>` (standalone `.idx`), `SET ORDER TO`, `SET INDEX TO` / `USE … INDEX`
  (multiple indexes per work area), `REINDEX`, `DELETE TAG`, plus introspection
  (`TAG()`/`TAGCOUNT()`/`TAGNO()`/`KEY()`/`ORDER()`/`CANDIDATE()`/`PRIMARY()`/`DESCENDING()`/`FOR()`/
  `CDX()`/`SYS(14)`/`ATAGINFO()`).
- **Error handling**: `ON ERROR`, `AERROR()` (the full 7-column result-array contract), `MESSAGE()`/
  `ERROR()`/`LINENO()`.
- **Referential integrity**: the DBC's stored `RULE`s and the generated insert/update/delete RI
  triggers auto-fire on `INSERT`/`REPLACE`/`DELETE` through the interpreter, cascading exactly as VFP9.

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
| `ISDIGIT()`, `ISALPHA()`, `ISUPPER()`, `ISLOWER()` | first-character checks, as in VFP |
| `PROPER()` | title-case each word |
| `TRANSFORM()` | format a value with a VFP `@`/picture format string |
| `STREXTRACT()` | text between two delimiters |
| `GETWORDCOUNT()`, `GETWORDNUM()` | word count / N-th word |
| `SOUNDEX()`, `DIFFERENCE()` | phonetic key / similarity |
| `STRCONV()`, `CPCONVERT()` | string / code-page conversions |
| `TEXTMERGE()` | expand `<< >>` expression bits in a template |
| `ALINES()` | split text into a memory array of lines |

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
| `TYPE()` | returns a VFP type code (`C`, `N`, `L`, `D`, …) from an expression *string* |
| `VARTYPE()` | returns the type code of a *value* (no re-evaluation) |
| `EVALUATE()` / `EVAL()` | evaluates a text expression at runtime |

**Arrays**

| Function | Notes |
|---|---|
| `ALEN()` | total elements (no dimension arg), rows (dim `1`), or columns (dim `2`) |
| `AELEMENT()`, `ASUBSCRIPT()` | element number ↔ (row, col) subscript conversion |
| `ACOPY()`, `ADEL()`, `AINS()` | copy / delete / insert array elements |
| `ASORT()` | sort a memory array |
| `AFIELDS()` | fill an array with the current table's field structure |
| `ADATABASES()`, `AUSED()`, `ASESSIONS()` | open databases / work areas / data sessions into an array |

**Work area / record state** — these are the *session-aware* versions (they read the live cursor,
not a context-free default), and take precedence over any same-named generic function above:

| Function | Notes |
|---|---|
| `SELECT()`, `USED()`, `ALIAS()` | |
| `DBF()`, `DBC()` | current table's / current database's source path |
| `INDBC()`, `ISEXCLUSIVE()`, `ISREADONLY()` | membership / open-mode of a table or database |
| `HEADER()`, `RECNO()`, `RECCOUNT()`, `LUPDATE()` | header size / record pointer / count / last-update date |
| `EOF()`, `BOF()`, `FOUND()`, `DELETED()` | |
| `SEEK()`, `LOOKUP()` | function-form seek / seek-and-return-a-field |
| `RELATION()`, `TARGET()` | read back a work area's `SET RELATION` links |
| `CURSORGETPROP()`, `CURSORSETPROP()`, `GETFLDSTATE()` | the buffering model (`Buffering` 1-5, field/row state) |
| `OLDVAL()`, `CURVAL()`, `TABLEUPDATE()`, `TABLEREVERT()` | buffer snapshots / commit / discard |
| `PCOUNT()` / `PARAMETERS()` | count of arguments actually passed to the current call |
| `PROGRAM()` | current procedure name, or `n` levels up the call stack |

**Index introspection** — read the open indexes of the current work area:

| Function | Notes |
|---|---|
| `TAG()`, `TAGCOUNT()`, `TAGNO()` | tag name by position / count / number of a named tag |
| `KEY()`, `FOR()`, `ORDER()` | a tag's key / `FOR` filter / the controlling order |
| `CANDIDATE()`, `PRIMARY()`, `UNIQUE()`, `DESCENDING()` | a tag's flags |
| `CDX()`, `IDXCOLLATE()`, `ATAGINFO()` | `.cdx` path / collation / all tag info into an array |

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
| `RLOCK()`/`LOCK()`, `FLOCK()`, `ISRLOCKED()`/`ISFLOCKED()` | see deviations — a single in-process interpreter never sees lock contention, so a lock is always granted |
| `MESSAGEBOX()` | stub — see deviations |

## Deviations from VFP

microVFP runs real VFP9 `.prg` source, but it is a **headless, single-session interpreter for
business-logic stored procedures** — not a drop-in replacement for the full VFP9 IDE/runtime. The
differences below are deliberate, verified simplifications, not bugs to be worked around:

- **`?`/`??` display output is not implemented.** There is no console to print to, so a `? expr` /
  `?? expr` line parses but produces nothing (it falls into the "unrecognized command" bucket below —
  no output, no exception). To observe a value, return it from a `FUNCTION`/`PROCEDURE` and read the
  `Call(...)` result in C# (as the snippets above do), or evaluate it with
  `interp.EvalExpression("expr")`.
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
- **An unrecognized command is captured, not rejected — and not executed either.** Any command verb
  the parser doesn't model yet still parses (so a whole `.prg` file continues to load even if it uses
  a handful of exotic commands), but it silently does nothing at runtime. A `.prg` parsing cleanly is
  therefore not proof that every line in it actually ran — check
  [What's implemented](#whats-implemented) and the [function reference](#function-reference) above
  for what's real.
- **A few functions are intentional stubs**, always returning the same constant: `MESSAGEBOX()`
  always returns `6` (`IDYES`) since there is no UI to show; unimplemented `SYS(n)` codes return `""`
  rather than their real per-code VFP9 behavior. (`DBC()`, `CURSORGETPROP()`/`GETFLDSTATE()` used to
  be on this list — they now return real values; see [What's implemented](#whats-implemented).)

The remaining VFP9 command surface (low-level file I/O, `LOCATE`/`CONTINUE` movement, multi-session
`SET DATASESSION`, contested locking) is being built out incrementally, prioritized by what real
`.prg` business-logic corpora actually use — the goal is correctly running real-world stored
procedures, not 100% language coverage on day one.

## Next

[5. The Highlike accelerator →](05-highlike-accelerator.md)
