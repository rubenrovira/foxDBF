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
- **Parameters**: `PARAMETERS`, by-value (`=Func(args)`/`(...)`) vs. by-reference (`DO proc WITH args`)
  passing.
- **Data access**: `USE`/`SELECT` work areas, `SEEK`/`LOCATE`/`GO`/`SKIP`, `REPLACE`/`DELETE`/
  `RECALL`/`INSERT`, `BEGIN`/`END TRANSACTION`/`ROLLBACK` (copy-on-write), `RLOCK`/`FLOCK`
  (byte-range-compatible with real VFP9 — see [2. Writing and indexes](02-writing-and-indexes.md#vfp-compatible-locking)).
- **Error handling**: `ON ERROR`, `AERROR()` (the full result-array contract), `MESSAGE()`/`ERROR()`/`LINENO()`.
- Runtime-state functions: `RECNO()`, `RECCOUNT()`, `ALIAS()`, `SELECT()`, `EOF()`/`BOF()`, `TYPE()`,
  `EVALUATE()`, and more.

Referential-integrity trigger auto-firing and the full VFP9 command surface (arrays, low-level file
I/O, indexing commands, relations, …) are being built out incrementally, prioritized by what real
`.prg` business-logic corpora actually use — the goal is correctly running real-world stored
procedures, not 100% language coverage on day one.

## Next

[5. The Highlike accelerator →](05-highlike-accelerator.md)
