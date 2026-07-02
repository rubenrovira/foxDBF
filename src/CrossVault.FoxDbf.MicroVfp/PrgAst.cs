using System.Collections.Generic;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.MicroVfp;

// ─────────────────────────────────────────────────────────────────────────────
//  microVFP — PRG (stored-procedure) Abstract Syntax Tree.
//
//  A parsed program is a list of PROCEDURE/FUNCTION definitions plus any top-level
//  ("main") statements that sit outside a procedure. Every expression fragment is a
//  PrgExpr (→ a real CrossVault.FoxDbf.Expressions.VfpExpression). A NAME-expression
//  `(expr)` (USE (lc) / SELECT (ln) / REPLACE (lcField) WITH …) is a NameExpr — it is
//  evaluated-as-a-name, NOT re-tokenised as code. A macro `&var[.]` is a MacroSubst —
//  captured verbatim (deferred; never expanded at parse time).
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Root: the procedures/functions of a .prg, plus any top-level statements.</summary>
public sealed record PrgProgram(
    IReadOnlyList<ProcDef> Procedures,
    IReadOnlyList<PrgStatement> Main);

/// <summary>Base for every statement node. <see cref="Line"/> is the 1-based source line.</summary>
public abstract record PrgStatement
{
    public int Line { get; init; }
}

// ── helpers ──────────────────────────────────────────────────────────────────

/// <summary>A name-position expression <c>(expr)</c> — evaluated to a string, then used
/// as a name (alias/table/field/tag). NOT re-parsed as code.</summary>
public sealed record NameExpr(PrgExpr Expression);

/// <summary>A macro substitution <c>&amp;var</c> / <c>&amp;var.</c> — captured verbatim and
/// deferred (the parser never expands or re-compiles it).</summary>
public sealed record MacroSubst(string Text);

/// <summary>A name where the grammar allows either a literal identifier/number or a
/// <c>(expr)</c> name-expression (the area/alias/table/order/field positions).</summary>
public sealed record NameRef
{
    public string? Name { get; }
    public NameExpr? Expr { get; }
    public bool IsNameExpr => Expr is not null;

    private NameRef(string? name, NameExpr? expr) { Name = name; Expr = expr; }

    public static NameRef OfName(string name) => new(name, null);
    public static NameRef OfExpr(NameExpr expr) => new(null, expr);

    public override string ToString() => IsNameExpr ? $"({Expr!.Expression.Text})" : Name!;
}

// ── procedures / declarations ────────────────────────────────────────────────

public enum ProcKind { Procedure, Function }

public sealed record ProcDef(
    string Name,
    ProcKind Kind,
    IReadOnlyList<string> Parameters,
    IReadOnlyList<PrgStatement> Body) : PrgStatement
{
    public bool IsFunction => Kind == ProcKind.Function;
}

public enum DeclScope { Local, Private, Public }

/// <summary>LOCAL / PRIVATE / PUBLIC declaration, incl. <c>ALL [LIKE|EXCEPT skel]</c>.
/// <see cref="Dimensions"/> is parallel to <see cref="Names"/>: the captured bracket text of an
/// array declaration (e.g. <c>"(1,12)"</c> for <c>PUBLIC gaErrors(1,12)</c>), or <c>null</c> for a
/// scalar. It distinguishes <c>PUBLIC x</c> (scalar) from <c>PUBLIC x(5)</c> (array).</summary>
public sealed record VarDecl(
    DeclScope Scope,
    IReadOnlyList<string> Names,
    bool All,
    string? Like,
    string? Except,
    IReadOnlyList<string?> Dimensions) : PrgStatement;

public sealed record ReleaseStmt(
    IReadOnlyList<string> Names,
    bool All,
    string? Like,
    string? Except) : PrgStatement;

/// <summary><c>DIMENSION</c> / <c>REDIMENSION</c> <c>name(r[,c]) [, …]</c> — create or resize memory
/// arrays. <see cref="Dimensions"/> is parallel to <see cref="Names"/> (the captured bracket text, e.g.
/// <c>"(1,12)"</c>); both DIMENSION and REDIMENSION preserve existing elements and <c>.F.</c>-fill growth.</summary>
public sealed record DimensionStmt(
    IReadOnlyList<string> Names,
    IReadOnlyList<string?> Dimensions) : PrgStatement;

/// <summary>PARAMETERS (PRIVATE binding) or LPARAMETERS (LOCAL binding).</summary>
public sealed record ParametersStmt(bool IsLocal, IReadOnlyList<string> Names) : PrgStatement;

// ── assignment ───────────────────────────────────────────────────────────────

/// <summary><c>target = expr</c>. <see cref="Target"/> is the raw lvalue text
/// (<c>var</c>, <c>alias.field</c>, <c>var(i)</c>, <c>m.var</c>).</summary>
public sealed record Assignment(string Target, PrgExpr Value) : PrgStatement;

/// <summary><c>STORE expr TO v1, v2, …</c>.</summary>
public sealed record StoreStmt(PrgExpr Value, IReadOnlyList<string> Targets) : PrgStatement;

// ── control flow ─────────────────────────────────────────────────────────────

public sealed record IfStmt(
    PrgExpr Condition,
    IReadOnlyList<PrgStatement> Then,
    IReadOnlyList<PrgStatement> Else) : PrgStatement;

public sealed record CaseClause(PrgExpr Condition, IReadOnlyList<PrgStatement> Body);

public sealed record DoCaseStmt(
    IReadOnlyList<CaseClause> Cases,
    IReadOnlyList<PrgStatement>? Otherwise) : PrgStatement;

public sealed record DoWhileStmt(PrgExpr Condition, IReadOnlyList<PrgStatement> Body) : PrgStatement;

public sealed record ForStmt(
    string Variable,
    PrgExpr From,
    PrgExpr To,
    PrgExpr? Step,
    IReadOnlyList<PrgStatement> Body) : PrgStatement;

public sealed record ScanStmt(
    string? Scope,
    PrgExpr? For,
    PrgExpr? While,
    IReadOnlyList<PrgStatement> Body) : PrgStatement;

public sealed record ReturnStmt(PrgExpr? Value) : PrgStatement;
public sealed record ExitStmt : PrgStatement;
public sealed record LoopStmt : PrgStatement;

// ── calls ────────────────────────────────────────────────────────────────────

/// <summary><c>DO Name [WITH args] [IN file]</c> (a statement; the PRG parser owns it —
/// unlike <c>=Name(args)</c>, which is an expression handled by the engine).</summary>
public sealed record DoCall(string Name, IReadOnlyList<PrgExpr> Args, NameRef? In) : PrgStatement;

/// <summary>A bare expression-statement: <c>=Func(args)</c>.</summary>
public sealed record ExprStatement(PrgExpr Expression) : PrgStatement;

// ── data / work-area ─────────────────────────────────────────────────────────

public enum UseMode { Default, Shared, Exclusive }

/// <summary>USE — open/close a table in a work area. <see cref="Index"/> is the optional
/// <c>INDEX &lt;list&gt;</c> clause (additional <c>.idx</c>/<c>.cdx</c> files opened alongside the
/// structural <c>.cdx</c>); empty when absent.</summary>
public sealed record UseStmt(
    string? Database,
    NameRef? Table,
    bool Again,
    NameRef? Alias,
    NameRef? Order,
    NameRef? In,
    UseMode Mode,
    bool NoUpdate,
    bool IsClose,
    IReadOnlyList<NameRef>? Index = null) : PrgStatement;

/// <summary>The work-area form <c>SELECT n | alias | 0 | (expr)</c> (NOT a SQL query).</summary>
public sealed record SelectAreaStmt(NameRef Area) : PrgStatement;

/// <summary>An embedded VFP-SQL <c>SELECT … FROM …</c>; routed to the existing SQL parser
/// (<see cref="Parsed"/>), with the raw text retained for round-tripping.</summary>
public sealed record SqlSelectStmt(string Sql, SqlStatement? Parsed) : PrgStatement;

public sealed record SeekStmt(PrgExpr Key, NameRef? Order, NameRef? In) : PrgStatement;
public sealed record LocateStmt(string? Scope, PrgExpr? For, PrgExpr? While) : PrgStatement;
public sealed record ContinueStmt : PrgStatement;

/// <summary><c>GO|GOTO n|TOP|BOTTOM [IN area]</c>. <see cref="Keyword"/> is TOP/BOTTOM, else
/// <see cref="Record"/> carries the record-number expression.</summary>
public sealed record GoStmt(string? Keyword, PrgExpr? Record, NameRef? In) : PrgStatement;

public sealed record SkipStmt(PrgExpr? Count, NameRef? In) : PrgStatement;
/// <summary><c>SET ORDER TO [n | cTag [OF cCdx]] [IN area] [ASCENDING|DESCENDING]</c>. A per-call
/// <see cref="Direction"/> override (VFP: the explicit clause wins for this order): <see langword="null"/> =
/// no clause (traverse in the tag's own stored direction), <see langword="true"/> = DESCENDING,
/// <see langword="false"/> = ASCENDING. microVFP P1 gap #1 — INDEX/ORDER WRITE model.</summary>
public sealed record SetOrderStmt(NameRef? Order, NameRef? In, bool? Direction) : PrgStatement;

/// <summary><c>INDEX ON eKey [TAG cTag [OF cCdx] | TO cIdx] [FOR lExpr] [ASCENDING|DESCENDING]
/// [UNIQUE|CANDIDATE] [ADDITIVE] [COMPACT]</c> — build/replace an index. <see cref="Tag"/> targets a
/// CDX tag (structural when <see cref="OfCdx"/> is null); <see cref="ToIdx"/> targets a standalone
/// <c>.idx</c> (unsupported — the interpreter throws). <see cref="Candidate"/> is CANDIDATE (a
/// duplicate-key is an error), <see cref="Unique"/> is UNIQUE (keep first per key).
/// microVFP P1 gap #1 — INDEX/ORDER WRITE model.</summary>
public sealed record IndexStmt(
    PrgExpr Key,
    NameRef? Tag,
    NameRef? OfCdx,
    NameRef? ToIdx,
    PrgExpr? For,
    bool Descending,
    bool Unique,
    bool Candidate,
    bool Additive) : PrgStatement;

/// <summary><c>REINDEX [IN area]</c> — rebuild every open tag of a work area from live data.
/// microVFP P1 gap #1 — INDEX/ORDER WRITE model.</summary>
public sealed record ReindexStmt(NameRef? In) : PrgStatement;

/// <summary><c>DELETE TAG &lt;name&gt;[, …] | ALL [OF &lt;cdx&gt;]</c> — remove tag(s) from a compound
/// <c>.cdx</c> (structural, or the named <see cref="OfCdx"/>). <see cref="All"/> removes every tag.
/// microVFP INDEX/ORDER MODEL.</summary>
public sealed record DeleteTagStmt(
    IReadOnlyList<string> Tags,
    bool All,
    NameRef? OfCdx,
    NameRef? In) : PrgStatement;

/// <summary><c>SET INDEX TO [&lt;idx/cdx list&gt;] [ORDER &lt;tag|n&gt; [ASCENDING|DESCENDING]]
/// [ADDITIVE]</c> — open additional (non-structural) index files in the current work area. An empty
/// <see cref="Files"/> list is the clear form (<c>SET INDEX TO</c>). microVFP INDEX/ORDER MODEL.</summary>
public sealed record SetIndexStmt(
    IReadOnlyList<NameRef> Files,
    NameRef? Order,
    bool? Direction,
    bool Additive) : PrgStatement;

/// <summary>Generic <c>SET &lt;setting&gt; …</c>. <see cref="Arguments"/> is the raw remainder
/// (<c>ON</c>/<c>OFF</c>/<c>TO …</c>). SET ORDER is modelled separately as
/// <see cref="SetOrderStmt"/>.</summary>
public sealed record SetStmt(string Setting, string Arguments) : PrgStatement;

// ── relations (microVFP P1 gap #2 — SET RELATION / SET SKIP) ──────────────────

/// <summary>One <c>eExpr INTO area</c> pair of a <c>SET RELATION</c> statement: the relation KEY
/// expression (evaluated in the PARENT area on each parent move) and the child work-area / alias.</summary>
public sealed record RelationTarget(PrgExpr Key, NameRef Into);

/// <summary><c>SET RELATION TO [eExpr INTO area [, …]] [ADDITIVE]</c> — link the CURRENT (parent) work
/// area to child area(s); on every parent record move an automatic SEEK repositions each child on its
/// active order (a miss leaves the child at EOF). An EMPTY <see cref="Targets"/> list is the clear form
/// (<c>SET RELATION TO</c> with no arguments). <see cref="Additive"/> ADDS to the existing relations
/// instead of replacing them. microVFP P1 gap #2 — the MULTI-TABLE RELATION model.</summary>
public sealed record SetRelationStmt(IReadOnlyList<RelationTarget> Targets, bool Additive) : PrgStatement;

/// <summary><c>SET RELATION OFF [INTO area]</c> — remove ONE child relation of the current area
/// (<see cref="Into"/>), or ALL relations of the current area when <see cref="Into"/> is
/// <see langword="null"/>. microVFP P1 gap #2.</summary>
public sealed record SetRelationOffStmt(NameRef? Into) : PrgStatement;

/// <summary><c>SET SKIP TO [alias [, …]]</c> — mark already-related child area(s) of the current area as
/// one-to-many; an EMPTY <see cref="Aliases"/> list clears all one-to-many marks. microVFP P1 gap #2.</summary>
public sealed record SetSkipStmt(IReadOnlyList<NameRef> Aliases) : PrgStatement;

public enum OnErrorKind { Clear, Command, Macro }

/// <summary><c>ON ERROR [command]</c>: clear (bare), a command (parsed sub-statement, with its raw
/// <see cref="CommandText"/> retained so <c>ON("ERROR")</c> reads back the source and a later
/// <c>ON ERROR &amp;var</c> can re-install it verbatim), or a macro body (<c>&amp;var.</c>).</summary>
public sealed record OnErrorStmt(
    OnErrorKind Kind,
    PrgStatement? Command,
    MacroSubst? Macro,
    string? CommandText = null) : PrgStatement;

// ── write ────────────────────────────────────────────────────────────────────

public sealed record ReplaceClause(NameRef Field, PrgExpr Value, bool Additive);

public sealed record ReplaceStmt(
    IReadOnlyList<ReplaceClause> Clauses,
    string? Scope,
    PrgExpr? For,
    PrgExpr? While,
    NameRef? In) : PrgStatement;

public sealed record DeleteStmt(string? Scope, PrgExpr? For, NameRef? In) : PrgStatement;
public sealed record RecallStmt(string? Scope, PrgExpr? For) : PrgStatement;

/// <summary><c>INSERT INTO …</c>; routed to the existing SQL parser when possible.</summary>
public sealed record InsertStmt(string Sql, SqlStatement? Parsed) : PrgStatement;

public sealed record SumStmt(
    IReadOnlyList<PrgExpr> Expressions,
    string? Scope,
    PrgExpr? For,
    PrgExpr? While,
    IReadOnlyList<string> To) : PrgStatement;

public sealed record UnlockStmt(bool All, NameRef? Record, NameRef? In) : PrgStatement;

// ── transactions ─────────────────────────────────────────────────────────────

public sealed record BeginTxnStmt : PrgStatement;
public sealed record EndTxnStmt : PrgStatement;
public sealed record RollbackStmt : PrgStatement;

// ── misc / fallback ──────────────────────────────────────────────────────────

/// <summary>A standalone macro line <c>&amp;var.</c> (deferred; never expanded).</summary>
public sealed record MacroSubstStmt(MacroSubst Macro) : PrgStatement;

/// <summary>A preprocessor directive line: <c>#INCLUDE</c> / <c>#DEFINE</c> / <c>#IF…</c>.</summary>
public sealed record DirectiveStmt(string Text) : PrgStatement;

/// <summary>A well-formed but un-modelled command — captured (verb + raw args) so the WHOLE
/// corpus parses without failing.</summary>
public sealed record UnknownCommand(string Verb, string Arguments) : PrgStatement;
