using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.MicroVfp;

// ─────────────────────────────────────────────────────────────────────────────
//  microVFP P1b — the TREE-WALKING INTERPRETER + runtime.
//
//  Runs the TasTrade sample / real-world customer business functions so their result + table side
//  effects are identical to VFP9. Reuses wholesale: VfpExpression (scalar ops +
//  functions), VfpSession (work areas, USE/SELECT, name resolution, SQL), DbfTable
//  (random-access read) + DbfWriter (REPLACE/DELETE). Adds, on top: a memory store
//  with VFP dynamic scoping, a per-area record pointer, the call mechanism with
//  by-ref / by-value parameter passing, the runtime STATE functions and the
//  statement tree-walk. See analysis/MICROVFP_SEMANTICS.md.
//
//  Char field reads are RIGHT-trimmed (trailing blanks/NULs) — this preserves the
//  leading-justified width VFP relies on (e.g. LEN(setup.value)=6) while matching
//  the library's trimmed convention; combined with SET EXACT ON it reproduces VFP's
//  field-vs-field "=" without the SET EXACT OFF short-pattern over-match.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>A tree-walking interpreter for microVFP stored procedures over a <see cref="VfpSession"/>.</summary>
/// <remarks>Not thread-safe: use one instance per session/connection; do not share across threads.</remarks>
public sealed class VfpInterpreter
{
    private readonly Dictionary<string, ProcDef> _procs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VfpValue> _defines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, AreaMeta> _meta = new();
    private readonly List<CallContext> _callStack = new();

    /// <summary>Hard cap on call nesting depth — a runaway recursive UDF throws a catchable
    /// <see cref="MicroVfpRuntimeException"/> at this depth rather than a fatal StackOverflowException.
    /// Each interpreter call spans MANY native frames (tree-walk → compiled-expression invoke →
    /// HostInvoke → back to CallProc), so one logical level costs far more stack than the tiny margin
    /// <see cref="System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack"/>
    /// guarantees — measured: the native stack overflows at ~110 nested levels in a Debug build BEFORE
    /// that probe trips. So the numeric cap (not the probe) is the real backstop and must sit safely
    /// below that native limit. 64 is well under ~110 yet far above any real VFP nesting (RI cascades
    /// and SP chains in the corpus are single-digit deep).
    /// ponytail: 64 is a fixed ceiling; raise it if a legitimate corpus case ever nests deeper AND the
    /// native stack can take it (else run the interpreter on a large-stack thread).</summary>
    private const int MaxCallDepth = 64;
    private readonly Row _row;
    private readonly EvaluationContext _ctx;

    // ── transactions (snapshot-and-restore over the live DBC tables; see §Transaktionen) ──
    private sealed class TxnFrame { public readonly Dictionary<string, FileSnapshot> Snaps = new(StringComparer.OrdinalIgnoreCase); }
    private sealed class FileSnapshot { public string Path = ""; public byte[]? Dbf; public byte[]? Cdx; public byte[]? Fpt; }
    private readonly List<TxnFrame> _txn = new();

    // ── SET NEAR (per data-session; microVFP INDEX/ORDER MODEL) ──
    // When ON, a failed SEEK leaves the pointer on the record just past where the key would sort
    // (EOF if past the end) instead of at EOF. Read back via SET("NEAR"). Scope: SEEK only.
    private bool _setNear;

    // ── SET RELATION (multi-table relation model; microVFP P1 gap #2) ──
    // Re-entrancy guard: while a parent move is repositioning its children, the child moves (SEEK/GOTO)
    // must NOT themselves re-fire the reposition hook — chaining is driven explicitly by
    // RepositionChildrenCore's recursion. A hard depth cap makes cyclic relations (a→b→a) terminate.
    private bool _inReposition;
    private const int MaxRelationDepth = 32;

    // ── ON ERROR runtime-error trap state (see §Fehlerbehandlung) ──
    private PrgStatement? _onErrorCmd;    // the installed handler command (executed on a trapped error).
    private string? _onErrorText;         // its raw source — ON("ERROR") reads it; ON ERROR &var re-installs it.
    private bool _inHandler;              // re-entrancy guard: a fault inside the handler is not re-trapped.
    private int _errNo;                   // ERROR()  — the LIVE trapped error number (0 outside a handler).
    private string _errMsg = "";          // MESSAGE() — the LIVE trapped error text (cleared on handler exit).
    private int _errLine;                 // LINENO()  — the offending source line (cleared on handler exit).

    /// <summary>Creates an interpreter bound to <paramref name="session"/>.</summary>
    public VfpInterpreter(VfpSession session)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        Memory = new MemoryStore();
        Runtime = new RuntimeState();
        _row = new Row(this);
        _ctx = session.Context;
        // VFP runtime defaults the SP corpus + oracle harness run under, plus the field-read policy
        // (see header): SET DELETED ON, MACHINE collation, and — paired with RIGHT-trimmed char reads
        // — SET EXACT ON so a field = field comparison is equality (no SET-EXACT-OFF prefix over-match).
        _ctx.Exact = true;
        _ctx.Deleted = true;
        _ctx.Encoding ??= Encoding.Latin1; // CHR()/ASC() byte-consistency for createId/newguid.
        _callStack.Add(new CallContext("(main)", Array.Empty<VfpValue>(), 0));
    }

    /// <summary>The data session the interpreter runs over (work areas, USE/SELECT, resolution, DML).</summary>
    public VfpSession Session { get; }

    /// <summary>The memory-variable store (VFP scoping: LOCAL/PRIVATE/PUBLIC + implicit-private).</summary>
    public MemoryStore Memory { get; }

    /// <summary>The interpreter runtime state (ON ERROR, SET REPROCESS, the lock fail-fast branch).</summary>
    public RuntimeState Runtime { get; }

    /// <summary>The loaded program, or <see langword="null"/> before <see cref="Load(PrgProgram)"/>.</summary>
    public PrgProgram? Program { get; private set; }

    /// <summary>
    /// P2 (RI) seam — the VFP <c>_triggerlevel</c> system variable the RI trigger procedures branch on
    /// (<c>IF _triggerlevel=1</c> gates the BEGIN TRANSACTION + SET-save + ON ERROR setup block). A test
    /// invoking a trigger as the OUTERMOST trigger sets this to 1. STUB for P2: stored + surfaced as the
    /// <c>_triggerlevel</c> name, but the nesting is NOT yet auto-managed by the cascade machinery
    /// (RIDELETE/RIUPDATE do not yet increment/decrement it), so the cascade contracts are still RED.
    /// </summary>
    public int TriggerLevel { get; set; } = 1;

    /// <summary>
    /// P3b master switch — when <see langword="true"/>, a PLAIN interpreter <c>DELETE</c>/<c>REPLACE</c>/
    /// <c>INSERT</c> on a DBC member that has a bound RI trigger AUTO-FIRES that trigger (cascade / restrict),
    /// exactly as VFP9 enforces its RI without any manual trigger call. Default <see langword="false"/> so
    /// the P2 manual-invocation behaviour (and all existing tests) is untouched.
    /// </summary>
    /// <remarks>
    /// TRIGGER BINDING — the bound trigger expression is NOT decodable from this codebase's DBC reader
    /// (the container tree decodes only the member's <c>PROPERTY</c> path, not its delete/update/insert
    /// trigger properties). So auto-fire FALLS BACK to the RI Builder NAMING CONVENTION the bindings are
    /// generated to match: <c>__RI_DELETE_&lt;table&gt;</c> / <c>__RI_UPDATE_&lt;table&gt;</c> /
    /// <c>__RI_INSERT_&lt;table&gt;</c>. A table with no such loaded proc (a free table, or no RI rule) does
    /// a plain DML with no trigger.
    /// </remarks>
    public bool EnforceReferentialIntegrity { get; set; }

    /// <summary>
    /// P3b instrumentation — the value of <c>_triggerlevel</c> captured at the entry of each AUTO-FIRED RI
    /// trigger, in firing order. The top-level fire records <c>1</c>; a nested cascade (a child DML fired
    /// from inside a running trigger) records <c>2</c>, <c>3</c>, … This lets a test observe the
    /// <c>_triggerlevel</c> deepening + restoration without reaching into the trigger procs. STUB: stays
    /// empty until the auto-fire machinery is built, so the nesting contract is RED.
    /// </summary>
    public IReadOnlyList<int> FiredTriggerLevels => _firedTriggerLevels;
    private readonly List<int> _firedTriggerLevels = new();

    /// <summary>
    /// P3b (ADO.NET enforced write-model) — set to <see langword="true"/> when the OUTERMOST auto-fired RI
    /// trigger of a DML op returns <c>.F.</c> (a RESTRICT abort, or a child-cascade restrict that propagated
    /// up to the top-level trigger). The plain <c>ExecInsert</c>/<c>ExecReplace</c>/<c>ExecDelete</c> path
    /// rolls the row back but returns NORMALLY (the trigger result is not raised), so the enforced ADO.NET
    /// write path inspects this flag after each row op to raise VFP error 1539 instead of reporting a phantom
    /// success with a wrong affected-record count. The caller resets it to <see langword="false"/> before each
    /// row op; it is never set on a successful (allowed) DML.
    /// </summary>
    public bool LastDmlTriggerAborted { get; set; }

    // P3b nesting counter — 0 outside any auto-fired trigger; 1 at the top-level fire, 2 inside a cascade
    // fired from within a running trigger, … This is the REAL backing for VFP's _triggerlevel: every
    // auto-fire increments it around the trigger Call and decrements on the way out. The cascade DML the
    // trigger itself issues (RIDELETE's DELETE / RIUPDATE's REPLACE) re-enters the interpreter's DML and
    // auto-fires the child trigger at the next depth — so the nesting deepens by construction.
    private int _triggerDepth;

    // P3b RI events a bound trigger fires for (delete current/scoped record, key-changing replace, append).
    private enum RiEvent { Delete, Update, Insert }

    /// <summary>
    /// P3b auto-fire seam — fire the table's bound RI trigger for <paramref name="ev"/> on work area
    /// <paramref name="area"/> the SAME way VFP does, managing <c>_triggerlevel</c> nesting. Returns
    /// <see langword="true"/> to ALLOW the DML, <see langword="false"/> to ABORT it (a RESTRICT rule —
    /// the trigger returned <c>.F.</c>). A table with no bound trigger (no matching <c>__RI_*</c> proc —
    /// a free table or a member with no RI rule) returns <see langword="true"/> and fires nothing, so it
    /// does a plain DML. ATOMICITY: the trigger itself wraps its cascade in <c>BEGIN/END TRANSACTION</c>
    /// (gated by <c>IF _triggerlevel=1</c>) and ROLLBACKs via <c>riend(.F.)</c> on a restrict/error — the
    /// P2 transaction machinery — so the parent op + its cascade are atomic without extra wrapping here.
    /// </summary>
    private bool FireDmlTrigger(RiEvent ev, int area)
    {
        var wa = Session.AreaAt(area);
        if (wa is null) return true;
        string? proc = ResolveTriggerProc(ev, wa);
        if (proc is null) return true;                       // no bound trigger ⇒ plain DML.

        _triggerDepth++;
        int saved = TriggerLevel;
        TriggerLevel = _triggerDepth;                        // 1 at the top fire; 2, 3, … when nested.
        _firedTriggerLevels.Add(_triggerDepth);
        try
        {
            // The trigger runs positioned on the current record of the DML's area (the parent being
            // deleted / the just-replaced parent). A .F. return = a RESTRICT violation ⇒ ABORT the DML.
            var r = Call(proc);
            bool allowed = !(r.Type == VfpType.Logical && !r.AsLogical);
            // Surface a TOP-LEVEL abort (the outermost op the enforced ADO.NET path issued) so that path can
            // raise VFP err 1539 rather than report a phantom success — a nested child-restrict propagates up
            // to this top-level trigger's .F. return by construction (the RI cascade re-enters and re-fires).
            if (!allowed && _triggerDepth == 1) LastDmlTriggerAborted = true;
            return allowed;
        }
        finally
        {
            _triggerDepth--;
            TriggerLevel = _triggerDepth == 0 ? saved : _triggerDepth;   // restore to the top level (1).
        }
    }

    /// <summary>Resolve the bound RI trigger proc for <paramref name="ev"/> on <paramref name="wa"/> via the
    /// RI Builder NAMING CONVENTION <c>__RI_&lt;event&gt;_&lt;table&gt;</c> (the binding expression is not
    /// decodable from this codebase's DBC reader — see <see cref="EnforceReferentialIntegrity"/>). Tries the
    /// work-area alias then the .dbf base name; null when no such proc is loaded.</summary>
    private string? ResolveTriggerProc(RiEvent ev, VfpSession.WorkArea wa)
    {
        string evName = ev switch { RiEvent.Delete => "DELETE", RiEvent.Update => "UPDATE", _ => "INSERT" };
        foreach (var table in TableNameCandidates(wa))
        {
            string name = "__RI_" + evName + "_" + table;
            if (_procs.ContainsKey(name)) return name;       // _procs is OrdinalIgnoreCase.
        }
        return null;
    }

    private static IEnumerable<string> TableNameCandidates(VfpSession.WorkArea wa)
    {
        yield return wa.Alias;
        var path = wa.Table.SourcePath;
        if (!string.IsNullOrEmpty(path))
        {
            string fn = Path.GetFileNameWithoutExtension(path);
            if (!string.Equals(fn, wa.Alias, StringComparison.OrdinalIgnoreCase)) yield return fn;
        }
    }

    /// <summary>
    /// P3b (point 4) — load the session DBC's <c>StoredProceduresSource</c> (the RI procs + business
    /// functions) straight from the container, so the caller need not <see cref="LoadFile(string)"/> an
    /// export for auto-fire to find the trigger procs. Reuses the P3a <see cref="DbfDatabase.StoredProcedureSource"/>
    /// reader; a no-op when the session has no open database or the container carries no SP source.
    /// </summary>
    public void LoadStoredProceduresFromDatabase()
    {
        var src = Session.Database?.StoredProcedureSource;
        if (string.IsNullOrEmpty(src)) return;
        var prog = PrgParser.Parse(src);
        foreach (var p in prog.Procedures) _procs[p.Name] = p;
        foreach (var p in prog.Procedures) ScanDefines(p.Body);
        ScanDefines(prog.Main);
    }

    /// <summary>Registers the procedures/functions of <paramref name="program"/>; last duplicate wins.</summary>
    public void Load(PrgProgram program)
    {
        Program = program ?? throw new ArgumentNullException(nameof(program));
        foreach (var p in program.Procedures) _procs[p.Name] = p;
        foreach (var p in program.Procedures) ScanDefines(p.Body);
        ScanDefines(program.Main);
    }

    /// <summary>Parses the PRG at <paramref name="path"/> and <see cref="Load(PrgProgram)"/>s it.</summary>
    public void LoadFile(string path) => Load(PrgParser.ParseFile(path));

    /// <summary>True when a procedure/function named <paramref name="name"/> is loaded.</summary>
    public bool Has(string name) => _procs.ContainsKey(name);

    /// <summary>Parses and runs <paramref name="source"/> as TOP-LEVEL statements (a setup snippet) in
    /// the global frame; state mutates the bound session/memory/runtime.</summary>
    public void Execute(string source)
    {
        var prog = PrgParser.Parse(source);
        foreach (var p in prog.Procedures) { _procs[p.Name] = p; ScanDefines(p.Body); }
        ScanDefines(prog.Main);
        try { ExecBlock(prog.Main); }
        catch (ReturnSignal) { /* RETURN at top level just ends the snippet */ }
    }

    /// <summary>Evaluates a single VFP expression string (e.g. a DBC field DEFAULT / RULE, or an ad-hoc
    /// <c>?expr</c> command) over this interpreter's session + memory, returning the typed result. Field
    /// references resolve against the CURRENT work area's record; bare names also resolve to memvars /
    /// <c>#DEFINE</c>s; function calls dispatch to loaded stored procedures (UDFs) before the engine's
    /// built-ins. A parse/eval failure yields <see cref="VfpValue.Null"/> (the same fail-soft contract as
    /// <c>EVALUATE()</c>).</summary>
    public VfpValue EvalExpression(string expression) => EvalText(expression ?? string.Empty);

    /// <summary>Like <see cref="EvalExpression"/> but REPORTS a parse/eval failure instead of the fail-soft
    /// <c>EVALUATE()</c> Null. Returns <see langword="true"/> with the evaluated value, or <see langword="false"/>
    /// (value = <see cref="VfpValue.Null"/>) when parsing or evaluation threw. The enforced write-model uses
    /// this so a DBC RULE whose expression ERRORS (an unloaded UDF, a missing field reference) surfaces as a
    /// violation rather than silently passing — exactly as VFP9 raises the error.</summary>
    public bool TryEvalExpression(string expression, out VfpValue value)
    {
        try
        {
            value = VfpExpression.Parse(MicroVfpExprRewrite.Normalize(expression ?? string.Empty)).Evaluate(_row, _ctx);
            return true;
        }
        catch
        {
            value = VfpValue.Null;
            return false;
        }
    }

    /// <summary>Invokes the loaded procedure/function <paramref name="name"/> with <paramref name="args"/>
    /// (BY VALUE) and returns its value (<c>RETURN expr</c>, default <c>.T.</c>).</summary>
    public VfpValue Call(string name, params VfpValue[] args)
    {
        if (!_procs.TryGetValue(name, out var proc))
            throw new MicroVfpRuntimeException($"Procedure '{name}' is not loaded.");
        return CallProc(proc, args ?? Array.Empty<VfpValue>(), (args?.Length ?? 0), null);
    }

    // ─────────────────────────── call mechanism ───────────────────────────

    private sealed class CallContext
    {
        public readonly string Name;
        public readonly VfpValue[] Args;
        public int PassedCount;
        public readonly List<string> ParamNames = new();
        public bool ParamsBound;
        public CallContext(string name, VfpValue[] args, int passed) { Name = name; Args = args; PassedCount = passed; }
    }

    private CallContext CurrentCall => _callStack[^1];

    /// <summary>Run <paramref name="proc"/> over a fresh memory frame, binding params, executing the
    /// body, writing BY-REF results back, and returning <c>RETURN</c>'s value (default <c>.T.</c>).</summary>
    private VfpValue CallProc(ProcDef proc, VfpValue[] args, int passedCount, string?[]? byRefNames)
    {
        // Cap call nesting so a runaway recursive UDF (no base case) throws a NORMAL, catchable runtime
        // error instead of a fatal StackOverflowException that would kill the host process. The numeric
        // limit is far above any legitimate VFP call depth (VFP itself caps at ~128 nested calls); the
        // EnsureSufficientExecutionStack probe additionally trips — throwing a catchable
        // InsufficientExecutionStackException — if a single deep call would actually exhaust the native
        // stack before the numeric cap (each interpreter frame spans many native frames).
        if (_callStack.Count >= MaxCallDepth)
            throw new MicroVfpRuntimeException(
                $"Maximum call nesting depth ({MaxCallDepth}) exceeded calling '{proc.Name}' — " +
                "probable unbounded recursion.");
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();

        var cc = new CallContext(proc.Name, args, passedCount);
        Memory.PushFrame();
        _callStack.Add(cc);

        // Header params `Name(a,b)` bind LOCAL; a leading PARAMETERS/LPARAMETERS binds when executed.
        if (proc.Parameters.Count > 0)
        {
            BindParams(proc.Parameters, isLocal: true, cc);
            cc.ParamsBound = true;
        }

        VfpValue result = VfpValue.Logical(true);
        try { ExecBlock(proc.Body); }
        catch (ReturnSignal rs) { result = rs.Value; }

        // Capture callee param finals BEFORE popping (for BY-REF write-back).
        var finals = cc.ParamNames.Select(n => Memory.Get(n)).ToArray();

        _callStack.RemoveAt(_callStack.Count - 1);
        Memory.PopFrame();

        if (byRefNames is not null)
            for (int i = 0; i < byRefNames.Length && i < finals.Length; i++)
                if (byRefNames[i] is { } caller) Memory.Set(caller, finals[i]);

        return result;
    }

    private void BindParams(IReadOnlyList<string> names, bool isLocal, CallContext cc)
    {
        for (int i = 0; i < names.Count; i++)
        {
            var v = i < cc.PassedCount && i < cc.Args.Length ? cc.Args[i] : VfpValue.Logical(false);
            Memory.BindParameter(names[i], v, isLocal ? VarKind.Local : VarKind.Private);
            cc.ParamNames.Add(names[i]);
        }
    }

    // ─────────────────────────── statement tree-walk ───────────────────────────

    private sealed class ReturnSignal : Exception { public VfpValue Value; public ReturnSignal(VfpValue v) => Value = v; }
    private sealed class ExitSignal : Exception { public static readonly ExitSignal I = new(); }
    private sealed class LoopSignal : Exception { public static readonly LoopSignal I = new(); }

    private void ExecBlock(IReadOnlyList<PrgStatement> body)
    {
        foreach (var s in body)
        {
            try { Exec(s); }
            catch (ReturnSignal) { throw; }
            catch (ExitSignal) { throw; }
            catch (LoopSignal) { throw; }
            catch (Exception ex)
            {
                // A RUNTIME error: run the installed ON ERROR handler in the error's context, then
                // RESUME after this line (default RETURN semantics). With no handler installed — or while
                // already inside a handler — the error propagates exactly as before P2.
                if (_inHandler || _onErrorCmd is null) throw;
                RunErrorHandler(ex, s.Line);
            }
        }
    }

    // ON ERROR <command>: capture ERROR()/MESSAGE()/LINENO(), run the handler (re-entrancy-guarded),
    // then CLEAR ERROR()/MESSAGE() (the RI handler captures them into pnerror/gaErrors first). RETRY is
    // not modelled — VFP's default on handler exit is RESUME AFTER the offending line, which is exactly
    // "return to ExecBlock and continue with the next statement".
    private void RunErrorHandler(Exception ex, int line)
    {
        SetErrorState(ex, line);
        _inHandler = true;
        try
        {
            if (_onErrorCmd is { } cmd) Exec(cmd);
        }
        catch { /* a fault inside the handler is swallowed (VFP would abort the handler). */ }
        finally
        {
            _inHandler = false;
            // The LIVE ERROR()/MESSAGE()/LINENO() clear on handler exit, but the RETAINED last-error
            // state (Runtime.LastError*) persists so a post-handler AERROR() can still fill its array.
            _errNo = 0; _errMsg = ""; _errLine = 0;
        }
    }

    // Capture a trapped error into BOTH the live trap functions (ERROR()/MESSAGE()/LINENO(), cleared on
    // handler exit) AND the retained last-error state that backs AERROR() (kept until the next error).
    private void SetErrorState(Exception ex, int line)
    {
        int no = ErrorNumberOf(ex);
        string msg = ex.Message ?? string.Empty;
        _errNo = no; _errMsg = msg; _errLine = line;
        Runtime.LastErrorNumber = no;
        Runtime.LastErrorMessage = msg;
        Runtime.LastErrorDetail = null;
        Runtime.LastErrorArea = Session.CurrentArea;
        Runtime.LastErrorTrigger = 0;
        Runtime.LastErrorField = 0;
    }

    private static int ErrorNumberOf(Exception ex)
    {
        string m = ex.Message ?? string.Empty;
        bool Has(string s) => m.Contains(s, StringComparison.OrdinalIgnoreCase);

        // Map the trapped exception onto the real VFP9 error numbers the RI/AERROR contract branches on.
        // The most specific patterns first so a generic "rule" message doesn't shadow the trigger case.
        if (Has("1539") || Has("trigger failed")) return 1539;   // RI trigger returned .F. (RESTRICT abort).
        if (Has("field") && Has("rule")) return 1582;            // field validation rule violated.
        if (Has("record") && Has("rule")) return 1583;           // record (table) validation rule violated.
        if (Has("rule violated") || Has("violates the rule")) return 1582; // bare rule-text → field-rule class.
        if (Has("session number is invalid")) return 1540;       // SET DATASESSION TO <non-existent session>.
        if (Has("locked") || Has("not locked") || Has("lock")) return 130; // record/file is in use / locked.
        if (Has("not found") || Has("does not exist")) return 1; // "File does not exist".
        if (Has("data type") || Has("type mismatch") || Has("not the same data type")) return 9; // type mismatch.
        return 1;   // unknown cause → a positive, non-zero VFP-style error number (all RI fallbacks need).
    }

    private void Exec(PrgStatement s)
    {
        switch (s)
        {
            case Assignment a: AssignTo(a.Target, Eval(a.Value)); break;
            case StoreStmt st: { var v = Eval(st.Value); foreach (var t in st.Targets) AssignTo(t, v); break; }
            case VarDecl vd: ExecDecl(vd); break;
            case DimensionStmt dm: ExecDimension(dm); break;
            case ReleaseStmt rl: foreach (var n in rl.Names) Memory.Release(n); break;
            case ParametersStmt ps: ExecParameters(ps); break;
            case ReturnStmt r: throw new ReturnSignal(r.Value is null ? VfpValue.Logical(true) : Eval(r.Value));
            case ExitStmt: throw ExitSignal.I;
            case LoopStmt: throw LoopSignal.I;
            case IfStmt iff: ExecIf(iff); break;
            case DoCaseStmt dc: ExecDoCase(dc); break;
            case DoWhileStmt dw: ExecDoWhile(dw); break;
            case ForStmt f: ExecFor(f); break;
            case ScanStmt sc: ExecScan(sc); break;
            case DoCall dca: ExecDoCall(dca); break;
            case ExprStatement es: Eval(es.Expression); break;
            case UseStmt us: ExecUse(us); break;
            case SelectAreaStmt sa: ExecSelectArea(sa); break;
            case SqlSelectStmt sq: ExecSqlSelect(sq); break;
            case SeekStmt sk: ExecSeek(sk); break;
            case GoStmt go: ExecGo(go); break;
            case SkipStmt sp: ExecSkip(sp); break;
            case SetOrderStmt so: ExecSetOrder(so); break;
            case IndexStmt ix: ExecIndex(ix); break;
            case ReindexStmt rix: ExecReindex(rix); break;
            case DeleteTagStmt dt: ExecDeleteTag(dt); break;
            case SetIndexStmt si: ExecSetIndex(si); break;
            case SetStmt set: ExecSet(set); break;
            case SetRelationStmt sr: ExecSetRelation(sr); break;
            case SetRelationOffStmt sro: ExecSetRelationOff(sro); break;
            case SetSkipStmt ss: ExecSetSkip(ss); break;
            case OnErrorStmt oe: ExecOnError(oe); break;
            case ReplaceStmt rp: ExecReplace(rp); break;
            case DeleteStmt del: ExecDelete(del); break;
            case RecallStmt rc: ExecRecall(rc); break;
            case SumStmt sum: ExecSum(sum); break;
            case UnlockStmt: break;                          // locks are modelled as always-held.
            case BeginTxnStmt: BeginTransaction(); break;
            case EndTxnStmt: EndTransaction(); break;
            case RollbackStmt: RollbackTransaction(); break;
            case DirectiveStmt d: ProcessDefine(d.Text); break;
            case InsertStmt ins: ExecInsert(ins); break;
            case LocateStmt or ContinueStmt or MacroSubstStmt or UnknownCommand or ProcDef: break;
            default: break;
        }
    }

    private void ExecDecl(VarDecl vd)
    {
        if (vd.All) return; // PRIVATE/LOCAL ALL [LIKE/EXCEPT] — no reservation needed for the targets.
        var kind = vd.Scope switch
        {
            DeclScope.Public => VarKind.Public,
            DeclScope.Local => VarKind.Local,
            _ => VarKind.Private,
        };
        for (int i = 0; i < vd.Names.Count; i++)
        {
            string? dim = i < vd.Dimensions.Count ? vd.Dimensions[i] : null;
            if (dim is not null && TryParseDims(dim, out int rows, out int cols))
                Memory.DeclareArray(vd.Names[i], kind, rows, cols);
            else
                Memory.Declare(vd.Names[i], kind);
        }
    }

    // DIMENSION / REDIMENSION name(r[,c]) — create or resize each named array (preserve + .F.-fill).
    private void ExecDimension(DimensionStmt dm)
    {
        for (int i = 0; i < dm.Names.Count; i++)
        {
            string? dim = i < dm.Dimensions.Count ? dm.Dimensions[i] : null;
            if (dim is not null && TryParseDims(dim, out int rows, out int cols))
                Memory.RedimOrCreateArray(dm.Names[i], rows, cols);
        }
    }

    // Parse the captured bracket text of an array declaration ("(1,12)" / "[5]" / "(lnRows+1,alen(a,2))")
    // into row/col counts; the dimension expressions are evaluated (constants or live expressions). cols=0
    // ⇒ a 1-D array. Returns false when the text is not a valid (positive-row) dimension list.
    private bool TryParseDims(string dimText, out int rows, out int cols)
    {
        rows = 0; cols = 0;
        string inner = dimText.Trim();
        if (inner.Length < 2) return false;
        inner = inner.Substring(1, inner.Length - 2);          // strip the enclosing () or [].
        var parts = PrgScan.SplitTopCommas(inner);
        if (parts.Count == 0 || parts[0].Trim().Length == 0) return false;
        rows = (int)EvalText(parts[0]).AsNumber;
        if (parts.Count > 1 && parts[1].Trim().Length > 0) cols = (int)EvalText(parts[1]).AsNumber;
        return rows > 0;
    }

    private void ExecParameters(ParametersStmt ps)
    {
        var cc = CurrentCall;
        if (cc.ParamsBound) return;
        BindParams(ps.Names, isLocal: ps.IsLocal, cc);
        cc.ParamsBound = true;
    }

    private void ExecIf(IfStmt iff)
    {
        if (Truth(Eval(iff.Condition))) ExecBlock(iff.Then);
        else ExecBlock(iff.Else);
    }

    private void ExecDoCase(DoCaseStmt dc)
    {
        foreach (var c in dc.Cases)
            if (Truth(Eval(c.Condition))) { ExecBlock(c.Body); return; }
        if (dc.Otherwise is not null) ExecBlock(dc.Otherwise);
    }

    private void ExecDoWhile(DoWhileStmt dw)
    {
        while (Truth(Eval(dw.Condition)))
        {
            try { ExecBlock(dw.Body); }
            catch (ExitSignal) { break; }
            catch (LoopSignal) { /* continue */ }
        }
    }

    private void ExecFor(ForStmt f)
    {
        double to = Eval(f.To).AsDouble;                       // burned-in once.
        double step = f.Step is null ? 1d : Eval(f.Step).AsDouble;
        if (step == 0d) step = 1d;
        double cur = Eval(f.From).AsDouble;
        Memory.Set(f.Variable, VfpValue.Number(cur));
        while (step > 0 ? cur <= to : cur >= to)
        {
            try { ExecBlock(f.Body); }
            catch (ExitSignal) { break; }
            catch (LoopSignal) { /* fall through to increment */ }
            cur = Memory.Get(f.Variable).AsDouble + step;       // body may mutate the loop var.
            Memory.Set(f.Variable, VfpValue.Number(cur));
        }
    }

    private void ExecScan(ScanStmt sc)
    {
        int area = Session.CurrentArea;
        // SCAN's default scope is ALL (an implicit GO TOP). A WHILE clause with no explicit scope is
        // REST — it processes from the CURRENT record (the canonical `SEEK key` / `SCAN WHILE key=…`
        // RI cascade idiom relies on this; a GO TOP would discard the SEEK). See MICROVFP_SEMANTICS §SCAN.
        bool fromCurrent = sc.While is not null && sc.Scope is null;
        if (!fromCurrent) GoTop(area);
        while (true)
        {
            Session.SelectArea(area);                            // body may have switched areas.
            var m = Meta(area);
            if (m.Eof) break;
            if (sc.While is not null && !Truth(Eval(sc.While))) break;
            bool match = sc.For is null || Truth(Eval(sc.For));
            if (match)
            {
                try { ExecBlock(sc.Body); }
                catch (ExitSignal) { break; }
                catch (LoopSignal) { /* implicit skip below */ }
            }
            Session.SelectArea(area);
            Skip(area, 1);
        }
    }

    private void ExecDoCall(DoCall d)
    {
        if (!_procs.TryGetValue(d.Name, out var proc)) return;  // unknown DO target → no-op.
        int n = d.Args.Count;
        var values = new VfpValue[n];
        var byRef = new string?[n];
        for (int i = 0; i < n; i++)
        {
            values[i] = Eval(d.Args[i]);
            byRef[i] = IsBareVariable(d.Args[i].Text) ? d.Args[i].Text.Trim() : null;
        }
        CallProc(proc, values, n, byRef);
    }

    private static bool IsBareVariable(string text)
    {
        string t = text.Trim();
        if (t.Length == 0 || !(char.IsAsciiLetter(t[0]) || t[0] == '_')) return false;
        foreach (var ch in t) if (!(char.IsAsciiLetterOrDigit(ch) || ch == '_')) return false;
        return true; // a single identifier ⇒ BY REFERENCE (a literal/expr/(parens)/a.b is not).
    }

    // ─────────────────────────── data / work-area commands ───────────────────────────

    private void ExecUse(UseStmt u)
    {
        if (u.IsClose)
        {
            int area = u.In is not null ? ResolveAreaRef(u.In) : Session.CurrentArea;
            if (area > 0) { Session.CloseArea(area); _meta.Remove(area); }
            return;
        }

        string table = NameOf(u.Table!);
        if (table.Contains('!')) table = table[(table.IndexOf('!') + 1)..];
        string? alias = u.Alias is null ? null : NameOf(u.Alias);

        int? inArea = null;
        if (u.In is not null)
        {
            if (u.In.IsNameExpr)
            {
                var v = Eval(u.In.Expr!.Expression);
                inArea = IsNumeric(v) ? (int)v.AsNumber : Session.FindAreaByAlias(v.AsString)?.Area;
            }
            else if (int.TryParse(u.In.Name, out var n)) inArea = n;
            else inArea = Session.FindAreaByAlias(u.In.Name!)?.Area;
        }

        Session.Use(table, inArea, alias, again: u.Again,
            exclusive: u.Mode == UseMode.Exclusive, noUpdate: u.NoUpdate);

        string aliasName = alias ?? Path.GetFileNameWithoutExtension(table);
        var wa = Session.FindAreaByAlias(aliasName);
        if (wa is not null)
        {
            var m = new AreaMeta();
            _meta[wa.Area] = m;

            // USE … INDEX <list> — open+track the named non-structural index files (was a no-op).
            if (u.Index is { Count: > 0 } && wa.Table.SourcePath is string tpath)
                foreach (var f in u.Index)
                {
                    string fp = ResolveExistingIndexPath(tpath, NameOf(f).Trim());
                    if (File.Exists(fp)) AddExtraIndex(wa.Area, fp);
                }

            // Controlling order: an explicit ORDER wins; else the FIRST listed index becomes master.
            string? orderName = u.Order is null ? null : NameOf(u.Order).Trim();
            if (!string.IsNullOrEmpty(orderName))
                m.Order = ResolveOrderName(wa.Area, orderName);
            else if (m.ExtraIndexes is { Count: > 0 })
                m.Order = FirstIndexIdentity(wa.Area);

            GoTop(wa.Area);
        }
    }

    /// <summary>The controlling-order identity of the FIRST opened non-structural index of
    /// <paramref name="area"/> — a standalone <c>.idx</c>'s stem, or the first tag of a <c>.cdx</c>.</summary>
    private string? FirstIndexIdentity(int area)
    {
        var extras = Meta(area).ExtraIndexes;
        if (extras is null || extras.Count == 0) return null;
        string p = extras[0];
        if (IsIdxPath(p)) return IdxOrderName(p);
        try
        {
            using var cdx = CdxFile.Open(p, Session.AreaAt(area)?.Table);
            return TagsInLayoutOrder(cdx).FirstOrDefault()?.Name;
        }
        catch { return null; }
    }

    private void ExecSelectArea(SelectAreaStmt sa)
    {
        var a = sa.Area;
        if (a.IsNameExpr)
        {
            var v = Eval(a.Expr!.Expression);
            if (IsNumeric(v)) Session.SelectArea((int)v.AsNumber);
            else SelectByName(v.AsString);
        }
        else SelectByName(a.Name!);
        Meta(Session.CurrentArea);
    }

    private void SelectByName(string s)
    {
        s = s.Trim();
        if (int.TryParse(s, out var n)) Session.SelectArea(n);     // SELECT 0 = lowest free; SELECT n.
        else Session.SelectArea(s);                                // SELECT alias.
    }

    private void ExecSqlSelect(SqlSelectStmt sq)
    {
        var parsed = sq.Parsed ?? SafeParseSql(sq.Sql);
        if (parsed is not SelectStatement sel) return;
        var exec = new SelectExecutor(Session, _row);
        exec.Run(sel);
        if (sel.Into is { Kind: IntoKind.Cursor } into)
        {
            Session.SelectArea(into.Name);                         // INTO CURSOR selects the cursor.
            _meta[Session.CurrentArea] = new AreaMeta();
            GoTop(Session.CurrentArea);
        }
    }

    private void ExecInsert(InsertStmt ins)
    {
        // P3b: an INSERT on a DBC member with a bound insert trigger (the RI insert RESTRICT rule) is
        // ENFORCED atomically — the row lands, the bound __RI_INSERT_<table> trigger fires positioned ON
        // the new record, and a .F. return (a missing parent key) ROLLS THE INSERTED ROW BACK, exactly as
        // VFP9 blocks such an INSERT. The target table is resolved from the PARSED statement (NOT the
        // current area — INSERT-SQL need not have selected onto the new record). When RI is off / the
        // table is free / it has no bound insert trigger, a plain best-effort insert with no enforcement.
        // BUFFERING (mode>1): defer the append into the target area's buffer (visible on the live cursor,
        // flushed at TABLEUPDATE for table buffering, or on the next pointer move for row buffering 2/3).
        // Gated on _anyBuffering so the default write-through INSERT path (and every Buffering=1 test) pays
        // only a bool check. Skipped while replaying at commit.
        if (_anyBuffering && !_inBufferCommit
            && ((ins.Parsed as InsertStatement) ?? (SafeParseSql(ins.Sql) as InsertStatement)) is { } bufStmt
            && Session.FindAreaByAlias(bufStmt.Table) is { } bufWa
            && _meta.TryGetValue(bufWa.Area, out var bufMeta) && bufMeta.Buffering > 1)
        {
            BufferAppend(bufWa, bufMeta, ins, bufStmt);
            return;
        }

        string? table = (ins.Parsed as InsertStatement)?.Table
                     ?? (SafeParseSql(ins.Sql) as InsertStatement)?.Table;

        if (!EnforceReferentialIntegrity || table is null)
        {
            try { Session.Execute(ins.Sql); } catch { /* best-effort; INSERT is not a target path */ }
            return;
        }

        // Resolve a work area for the target up front (opening one if the caller did not have it open) so
        // we can snapshot its .dbf for a revertible insert AND position the trigger on the new record.
        bool openedHere = false;
        var wa = Session.FindAreaByAlias(table);
        if (wa is null)
        {
            try { Session.Use(table, inArea: 0); wa = Session.FindAreaByAlias(table); openedHere = wa is not null; }
            catch { wa = null; }
        }

        // No resolvable target / no bound insert trigger ⇒ plain DML, no enforcement (free table or no rule).
        if (wa is null || ResolveTriggerProc(RiEvent.Insert, wa) is null)
        {
            if (openedHere && wa is not null) { Session.CloseArea(wa.Area); _meta.Remove(wa.Area); }
            try { Session.Execute(ins.Sql); } catch { }
            return;
        }

        int area = wa.Area;
        string? path = wa.Table.SourcePath;
        FileSnapshot? snap = path is not null ? CaptureSnapshot(path) : null;   // pre-image for the rollback.

        try { Session.Execute(ins.Sql); }
        catch { if (openedHere) { Session.CloseArea(area); _meta.Remove(area); } return; }

        if (path is not null) ReopenFileAreas(path);   // every open handle must see the appended row.

        int savedArea = Session.CurrentArea;
        Session.SelectArea(area);                       // the trigger reads the new record via SELECT().
        var fresh = Session.AreaAt(area);
        if (fresh is not null) GoRecord(area, fresh.Table.RecordCount);   // appended row = last physical.
        bool ok = FireDmlTrigger(RiEvent.Insert, area);
        Session.SelectArea(savedArea);

        // RESTRICT (parent key missing): the trigger rolled back nothing of ITS own (it only reads) — roll
        // the inserted row back here so VFP's "blocked INSERT" is matched and no orphan child survives.
        if (!ok && snap is not null) RestoreSnapshot(snap);
        if (openedHere && Session.AreaAt(area) is not null) { Session.CloseArea(area); _meta.Remove(area); }
    }

    private void ExecSeek(SeekStmt sk)
    {
        int area = sk.In is not null ? ResolveAreaRef(sk.In) : Session.CurrentArea;
        string? tag = sk.Order is null ? null : NameOf(sk.Order);
        DoSeek(Eval(sk.Key), area, tag);
    }

    private void ExecGo(GoStmt go)
    {
        int area = go.In is not null ? ResolveAreaRef(go.In) : Session.CurrentArea;
        if (area <= 0) return;
        if (go.Keyword == "TOP") GoTop(area);
        else if (go.Keyword == "BOTTOM") GoBottom(area);
        else if (go.Record is not null) GoRecord(area, (int)Eval(go.Record).AsNumber);
    }

    private void ExecSkip(SkipStmt sp)
    {
        int area = sp.In is not null ? ResolveAreaRef(sp.In) : Session.CurrentArea;
        int n = sp.Count is null ? 1 : (int)Eval(sp.Count).AsNumber;
        Skip(area, n);
    }

    private void ExecSetOrder(SetOrderStmt so)
    {
        int area = so.In is not null ? ResolveAreaRef(so.In) : Session.CurrentArea;
        if (area <= 0) return;
        var m = Meta(area);
        string? name = so.Order is null ? null : NameOf(so.Order).Trim();
        m.Order = ResolveOrderName(area, name);
        // Changing the controlling order turns off any active SET KEY range (hackfox s4g704) and
        // re-bases the cached index sequence so a following GO TOP / SKIP walks the NEW order.
        m.KeySet = false; m.KeyRange = false; m.KeyLow = null; m.KeyHigh = null; m.KeyVisible = null;
        m.Ordered = null; m.OrderedFor = null; m.OrderPos = -1;
        // Per-call ASCENDING|DESCENDING override (task scope): the explicit clause selects the ABSOLUTE
        // traversal direction for this order, overriding the tag's own stored Descending. We track it as
        // an OrderReversed flag = (explicit direction) XOR (tag's stored direction); ActiveOrder reverses
        // the cached recno sequence when set, so GO TOP/BOTTOM/SKIP all follow the requested direction.
        // (NB: hackfox s4g093 per-tag direction PERSISTENCE across a later clause-less SET ORDER is a
        // separate, out-of-scope refinement — here the override lasts until the next SET ORDER.)
        if (so.Direction is bool wantDescending)
        {
            bool tagDescending = MasterDescending(area);
            m.OrderReversed = wantDescending != tagDescending;
        }
        else
        {
            m.OrderReversed = false;
        }
    }

    /// <summary>Resolve a SET ORDER operand to a tag NAME (case-insensitively matched at use):
    /// a blank / <c>"0"</c> ⇒ natural (record) order (<see langword="null"/>); a positive number ⇒ the
    /// n-th tag (1-based) of the area's structural <c>.cdx</c>; otherwise the named tag. An UNKNOWN tag
    /// name or an OUT-OF-RANGE index number raises a catchable error (VFP 1683 "Tag … not found" /
    /// index-number-out-of-range) instead of silently degrading to natural order.</summary>
    private string? ResolveOrderName(int area, string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var inv = IndexInventory(area);   // full open set: idx + structural cdx tags + extra cdx tags.
        if (int.TryParse(name, out int n))
        {
            if (n <= 0) return null;   // SET ORDER TO 0 ⇒ natural/record order.
            if (n <= inv.Count) return inv[n - 1].Name;
            throw new MicroVfpRuntimeException(
                $"SET ORDER TO {n}: index number is out of range (the work area has {inv.Count} index(es)).");
        }
        foreach (var slot in inv)
            if (string.Equals(slot.Name, name, StringComparison.OrdinalIgnoreCase))
                return slot.Name;
        throw new MicroVfpRuntimeException($"SET ORDER TO {name}: tag not found in the current work area.");
    }

    // ─────────────────────────── INDEX / REINDEX (microVFP P1 gap #1) ───────────────────────────
    //
    // INDEX ON eKey TAG cTag [FOR lExpr] [ASCENDING|DESCENDING] [UNIQUE|CANDIDATE] [ADDITIVE] builds (or
    // replaces) a tag in the STRUCTURAL .cdx via the byte-exact CDX builder (DbfWriter.CreateTag), then
    // makes it the controlling order. TO <idx> (standalone) and TAG … OF <cdx> (non-structural) are
    // explicit, catchable refusals — there is no .idx writer / multi-CDX-per-area model yet. CANDIDATE
    // builds the tag then verifies key-uniqueness, rolling the files back + raising on a duplicate.
    //
    // Deleted records: the build honours the LIVE SET DELETED — SET DELETED ON (the microVFP default)
    // excludes deleted rows; SET DELETED OFF indexes them too (VFP keeps their CDX entries until PACK).
    // The live SET EXACT / SET ANSI ride along into the KEY/FOR expression evaluation (via _ctx) so a
    // character `=` in a FOR/KEY filters exactly as a VFP run would. A FOR clause is honoured by the
    // builder. SET FILTER is not modelled in microVFP, so it cannot narrow the build set — FLAG if a
    // corpus case ever needs it.
    private void ExecIndex(IndexStmt ix)
    {
        int area = Session.CurrentArea;
        var wa = Session.AreaAt(area);
        if (wa is null)
            throw new MicroVfpRuntimeException("INDEX ON: no table is open in the current work area.");

        if (wa.Table.SourcePath is not string path)
            throw new MicroVfpRuntimeException("INDEX ON: the current table has no file on disk.");

        string keyExpr = ix.Key.Text.Trim();
        string? forExpr = ix.For?.Text is { } f && f.Trim().Length > 0 ? f.Trim() : null;
        bool candidate = ix.Candidate;
        // UNIQUE clause OR the SET UNIQUE session default (candidate is a distinct constraint, not UNIQUE).
        bool unique = ix.Unique || (Runtime.Unique && !candidate);
        string collation = _ctx.Collation?.Name ?? "MACHINE";
        var m = Meta(area);

        // ── INDEX ON eExpr TO <idx> — build a standalone legacy .idx and make it the controlling order. ──
        if (ix.ToIdx is not null)
        {
            string idxPath = ResolveSidecarPath(path, NameOf(ix.ToIdx).Trim(), ".idx");
            BuildTagOnDisk(path, w => w.CreateStandaloneIdx(idxPath, keyExpr, forExpr, unique, _ctx, !_ctx.Deleted));
            AddExtraIndex(area, idxPath);
            m.Order = IdxOrderName(idxPath);
            ResetOrderState(m);
            GoTop(area);
            return;
        }

        if (ix.Tag is null)
            throw new MicroVfpRuntimeException("INDEX ON: a TAG name is required.");

        // VFP UPPERCASES the tag name in the .cdx directory (verified byte-for-byte vs VFP9); the KEY /
        // FOR expression case is PRESERVED as typed (matches the reverse-engineered DBC .dcx).
        string tagName = NameOf(ix.Tag).Trim().ToUpperInvariant();
        if (tagName.Length == 0)
            throw new MicroVfpRuntimeException("INDEX ON: a TAG name is required.");

        var def = new CdxTagDefinition(tagName, keyExpr, forExpr, ix.Descending, collation, unique);

        // ── INDEX ON eExpr TAG cTag OF <cdx> — build a tag in a NAMED (non-structural) compound index. ──
        if (ix.OfCdx is not null)
        {
            string cdxPath = ResolveSidecarPath(path, NameOf(ix.OfCdx).Trim(), ".cdx");
            BuildTagOnDisk(path, w => w.CreateTagIn(cdxPath, structural: false, def, _ctx, !_ctx.Deleted));
            AddExtraIndex(area, cdxPath);
            m.Order = tagName;
            ResetOrderState(m);
            GoTop(area);
            return;
        }

        // ── structural .cdx tag (the already-shipped path). ──
        // CANDIDATE: capture the pre-image so a uniqueness violation rolls the .cdx/.dbf back (VFP does not
        // create the tag on a duplicate).
        FileSnapshot? pre = candidate ? CaptureSnapshot(path) : null;

        BuildTagOnDisk(path, w => w.CreateTag(def, _ctx, includeDeleted: !_ctx.Deleted));

        if (candidate)
        {
            var built = Session.AreaAt(area)?.Cdx;
            var tag = built?.Tag(tagName) ?? built?.Tag(tagName.ToUpperInvariant());
            if (tag is not null && HasDuplicateKeys(tag))
            {
                RollbackFiles(pre!, path);
                throw new MicroVfpRuntimeException(
                    $"INDEX ON … TAG {tagName} CANDIDATE: uniqueness violated — a duplicate key value exists.");
            }
        }

        // The new tag becomes the controlling order (VFP behaviour) and the pointer goes to its top.
        m.Order = tagName;
        ResetOrderState(m);
        GoTop(area);
    }

    /// <summary>Reset the cached index sequence + any SET KEY range after the controlling order changes
    /// (a fresh INDEX ON / SET ORDER re-bases GO TOP / SKIP and clears any DESCENDING override).</summary>
    private static void ResetOrderState(AreaMeta m)
    {
        m.OrderReversed = false;
        m.Ordered = null; m.OrderedFor = null; m.OrderPos = -1;
        m.KeySet = false; m.KeyRange = false; m.KeyLow = null; m.KeyHigh = null; m.KeyVisible = null;
    }

    /// <summary>Resolve a sidecar index file path relative to the table's directory: an explicit
    /// extension is honoured, else <paramref name="defaultExt"/> is appended.</summary>
    private static string ResolveSidecarPath(string tablePath, string name, string defaultExt)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(tablePath)) ?? ".";
        string file = Path.HasExtension(name) ? name : name + defaultExt;
        return Path.IsPathRooted(file) ? Path.GetFullPath(file) : Path.GetFullPath(Path.Combine(dir, file));
    }

    /// <summary>The controlling-order identity a standalone <c>.idx</c> is addressed by — its file stem,
    /// uppercased (an <c>.idx</c> has no tag name).</summary>
    private static string IdxOrderName(string idxPath)
        => Path.GetFileNameWithoutExtension(idxPath).ToUpperInvariant();

    /// <summary>Track <paramref name="fullPath"/> as an open non-structural index of <paramref name="area"/>
    /// (idempotent; case-insensitive).</summary>
    private void AddExtraIndex(int area, string fullPath)
    {
        var m = Meta(area);
        m.ExtraIndexes ??= new List<string>();
        if (!m.ExtraIndexes.Any(p => SamePath(p, fullPath)))
            m.ExtraIndexes.Add(Path.GetFullPath(fullPath));
    }

    // ─────────────────────────── DELETE TAG / SET INDEX (microVFP INDEX/ORDER MODEL) ───────────────────────────

    /// <summary>DELETE TAG cTag[, …] | ALL [OF cCdx] — rebuild the target compound <c>.cdx</c> without the
    /// named tag(s) (structural by default, or the named <c>OF</c> file). ALL removes every tag (and, when
    /// the file becomes empty, VFP deletes it). Deleting the controlling order reverts to natural order.</summary>
    private void ExecDeleteTag(DeleteTagStmt dt)
    {
        int area = dt.In is not null ? ResolveAreaRef(dt.In) : Session.CurrentArea;
        var wa = Session.AreaAt(area);
        if (wa?.Table.SourcePath is not string path)
            throw new MicroVfpRuntimeException("DELETE TAG: no table is open in the work area.");

        bool structural = dt.OfCdx is null;
        string cdxPath = structural
            ? Path.ChangeExtension(path, ".cdx")
            : ResolveSidecarPath(path, NameOf(dt.OfCdx!).Trim(), ".cdx");
        IReadOnlyCollection<string>? names = dt.All ? null : dt.Tags;

        BuildTagOnDisk(path, w => w.DeleteTagsIn(cdxPath, structural, names, _ctx, includeDeleted: !_ctx.Deleted));

        // A non-structural .cdx that was emptied+deleted is no longer an open index.
        if (!structural && !File.Exists(cdxPath))
        {
            var mm = Meta(area);
            mm.ExtraIndexes?.RemoveAll(p => SamePath(p, cdxPath));
        }

        // If the controlling order was among the removed tags, VFP reverts to natural (record) order.
        var m = Meta(area);
        if (!string.IsNullOrEmpty(m.Order))
        {
            var src = OpenOrderSource(area, m.Order!);
            if (src is null) { m.Order = null; ResetOrderState(m); }
            else src.Dispose();
        }
    }

    /// <summary>SET INDEX TO [cList] [ORDER …] [ADDITIVE] — open the listed non-structural index files in
    /// the current work area. Without ADDITIVE the previously-opened non-structural indexes are closed
    /// first; <c>SET INDEX TO</c> (no args) closes them all. ORDER selects the controlling tag.</summary>
    private void ExecSetIndex(SetIndexStmt si)
    {
        int area = Session.CurrentArea;
        var wa = Session.AreaAt(area);
        if (wa?.Table.SourcePath is not string path)
            throw new MicroVfpRuntimeException("SET INDEX TO: no table is open in the current work area.");
        var m = Meta(area);

        if (!si.Additive)
            m.ExtraIndexes = null;   // replace: close previously-opened non-structural indexes.

        foreach (var f in si.Files)
        {
            string fp = ResolveExistingIndexPath(path, NameOf(f).Trim());
            if (!File.Exists(fp))
                throw new MicroVfpRuntimeException($"SET INDEX TO: index file '{NameOf(f)}' was not found.");
            AddExtraIndex(area, fp);
        }

        if (si.Order is not null)
        {
            m.Order = ResolveOrderName(area, NameOf(si.Order).Trim());
            if (si.Direction is bool wantDesc)
            {
                bool tagDesc = MasterDescending(area);
                m.OrderReversed = wantDesc != tagDesc;
            }
            else m.OrderReversed = false;
            m.Ordered = null; m.OrderedFor = null; m.OrderPos = -1;
            m.KeySet = false; m.KeyRange = false; m.KeyLow = null; m.KeyHigh = null; m.KeyVisible = null;
            GoTop(area);
        }
        else if (!string.IsNullOrEmpty(m.Order))
        {
            // Closing indexes may have invalidated the controlling order → revert to natural order.
            if (OpenOrderSource(area, m.Order!) is { } src) src.Dispose();
            else { m.Order = null; ResetOrderState(m); }
        }
    }

    // ─────────────────────────── multi-index inventory + order resolution ───────────────────────────

    /// <summary>One addressable index in a work area's OPEN SET, in tag-number order.</summary>
    private sealed class IndexSlot
    {
        public string Name = "";       // tag name (CDX) or file stem uppercased (standalone IDX).
        public string FilePath = "";   // owning index file.
        public bool IsIdx;
        public bool Structural;
        public string KeyExpr = "";
        public string ForExpr = "";
        public bool Descending;
        public bool Unique;
        public string Collation = "MACHINE";
    }

    /// <summary>The area's open index set in TAG-NUMBER order: standalone <c>.idx</c> files (open order)
    /// first, then the structural <c>.cdx</c> tags (creation/header-layout order), then each additional
    /// <c>.cdx</c>'s tags (open order). Opens the extra files on demand — never held long-term.</summary>
    private List<IndexSlot> IndexInventory(int area)
    {
        var slots = new List<IndexSlot>();
        var wa = Session.AreaAt(area);
        if (wa is null) return slots;
        var extras = _meta.TryGetValue(area, out var m) ? m.ExtraIndexes : null;

        // 1) standalone .idx files (open order).
        if (extras is not null)
            foreach (var p in extras)
                if (IsIdxPath(p))
                    try
                    {
                        using var idx = IdxFile.Open(p);
                        var h = idx.Header;
                        slots.Add(new IndexSlot
                        {
                            Name = IdxOrderName(p), FilePath = p, IsIdx = true,
                            KeyExpr = h.KeyExpression.Trim(), ForExpr = h.ForExpression.Trim(),
                            Descending = false, Unique = h.IsUnique, Collation = "MACHINE",
                        });
                    }
                    catch { /* unreadable idx → skip */ }

        // 2) structural .cdx tags (creation order = ascending root-page offset).
        if (wa.Cdx is not null)
            foreach (var tag in TagsInLayoutOrder(wa.Cdx))
                slots.Add(SlotForTag(tag, wa.Cdx.SourcePath ?? string.Empty, structural: true));

        // 3) additional .cdx tags (open order).
        if (extras is not null)
            foreach (var p in extras)
                if (!IsIdxPath(p))
                    try
                    {
                        using var cdx = CdxFile.Open(p, wa.Table);
                        foreach (var tag in TagsInLayoutOrder(cdx))
                            slots.Add(SlotForTag(tag, p, structural: false));
                    }
                    catch { /* unreadable cdx → skip */ }

        return slots;
    }

    private static IndexSlot SlotForTag(Index.CdxTag tag, string filePath, bool structural) => new()
    {
        Name = tag.Name, FilePath = filePath, IsIdx = false,
        KeyExpr = tag.KeyExpression.Trim(), ForExpr = tag.ForExpression.Trim(),
        Descending = tag.Descending, Unique = tag.IsUnique,
        Collation = string.IsNullOrEmpty(tag.Collation) ? "MACHINE" : tag.Collation,
        Structural = structural,
    };

    /// <summary>The tags of a compound index in CREATION (header-page layout) order — ascending root-page
    /// offset (the directory enumerates them by NAME, but VFP numbers by layout order).</summary>
    private static IEnumerable<Index.CdxTag> TagsInLayoutOrder(CdxFile cdx)
        => cdx.TagNames.Select(n => cdx.Tag(n)).Where(t => t is not null).Select(t => t!)
              .OrderBy(t => t.RootPageOffset);

    private static bool IsIdxPath(string p)
        => string.Equals(Path.GetExtension(p), ".idx", StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolve an index-file name for SET INDEX / USE … INDEX: an explicit extension wins, else an
    /// existing <c>.cdx</c> then <c>.idx</c> beside the table, defaulting to <c>.cdx</c>.</summary>
    private static string ResolveExistingIndexPath(string tablePath, string name)
    {
        if (Path.HasExtension(name)) return ResolveSidecarPath(tablePath, name, Path.GetExtension(name));
        foreach (var ext in new[] { ".cdx", ".idx" })
        {
            string cand = ResolveSidecarPath(tablePath, name, ext);
            if (File.Exists(cand)) return cand;
        }
        return ResolveSidecarPath(tablePath, name, ".cdx");
    }

    /// <summary>A live handle on a controlling ORDER (a CDX tag or a standalone IDX), plus the temp file
    /// handle to release when done (null for the structural <c>.cdx</c>, which the work area owns).</summary>
    private sealed class OrderSource : IDisposable
    {
        public Index.CdxTag? CdxTag;
        public IdxFile? Idx;
        public IndexKeyType IdxKeyType;
        public bool Descending;
        public string Name = "";
        private readonly IDisposable? _owner;
        public OrderSource(IDisposable? owner) => _owner = owner;
        public void Dispose() => _owner?.Dispose();

        // ── uniform key metadata across a CDX tag OR a standalone .idx (legacy .idx is always MACHINE-
        // collated raw code-page bytes, ascending) so the SET KEY / master-tag callers work regardless
        // of which open index the controlling order was sourced from. ──
        public bool IsCharacterKey => CdxTag?.IsCharacterKey ?? (IdxKeyType == IndexKeyType.Character);
        public IndexKeyType KeyType => CdxTag?.KeyType ?? IdxKeyType;
        public int KeyLength => CdxTag?.KeyLength ?? (Idx?.KeyLength ?? 0);
        public string CollationName =>
            CdxTag is { } t && !string.IsNullOrEmpty(t.Collation) ? t.Collation : "MACHINE";
        public IndexKey DecodeKey(byte[] keyBytes) =>
            CdxTag is { } t ? t.DecodeKey(keyBytes) : IndexKey.Decode(keyBytes, IdxKeyType);
    }

    /// <summary>Resolve an ORDER identity (tag name or standalone-IDX stem) to a live <see cref="OrderSource"/>
    /// across the FULL open index set (structural <c>.cdx</c>, then extra <c>.idx</c>/<c>.cdx</c>), or null
    /// when unresolved. The caller MUST dispose the result.</summary>
    private OrderSource? OpenOrderSource(int area, string identity)
    {
        var wa = Session.AreaAt(area);
        if (wa is null || string.IsNullOrEmpty(identity)) return null;

        // structural .cdx first (owner null — the work area keeps it open).
        if (wa.Cdx is not null)
        {
            var t = wa.Cdx.Tag(identity) ?? wa.Cdx.Tag(identity.ToUpperInvariant());
            if (t is not null) return new OrderSource(null) { CdxTag = t, Descending = t.Descending, Name = t.Name };
        }

        var extras = _meta.TryGetValue(area, out var m) ? m.ExtraIndexes : null;
        if (extras is null) return null;
        foreach (var p in extras)
        {
            if (IsIdxPath(p))
            {
                if (string.Equals(IdxOrderName(p), identity, StringComparison.OrdinalIgnoreCase))
                {
                    var idx = IdxFile.Open(p);
                    return new OrderSource(idx)
                    {
                        Idx = idx, Name = IdxOrderName(p),
                        IdxKeyType = IndexKey.ResolveType(idx.Header.KeyExpression.Trim(), wa.Table),
                    };
                }
            }
            else
            {
                var cdx = CdxFile.Open(p, wa.Table);
                var t = cdx.Tag(identity) ?? cdx.Tag(identity.ToUpperInvariant());
                if (t is not null)
                    return new OrderSource(cdx) { CdxTag = t, Descending = t.Descending, Name = t.Name };
                cdx.Dispose();
            }
        }
        return null;
    }

    /// <summary>The default controlling-order identity for a SEEK with no explicit tag and no active order:
    /// the current order, else the first structural tag, else the first inventory slot.</summary>
    private string? DefaultOrderIdentity(int area)
    {
        var m = Meta(area);
        if (!string.IsNullOrEmpty(m.Order)) return m.Order;
        var inv = IndexInventory(area);
        return inv.Count > 0 ? inv[0].Name : null;
    }

    /// <summary>The (key, recno) entries of an order in CONTROLLING order (a CDX tag reverses for a
    /// DESCENDING tag; a standalone IDX is ascending).</summary>
    private static IEnumerable<(byte[] Key, int Recno)> OrderedEntries(OrderSource src)
    {
        if (src.CdxTag is { } t)
            return t.EnumerateEntries().Select(e => (e.Key, (int)e.RecordNumber));
        if (src.Idx is { } idx)
            return idx.EnumerateEntries().Select(e => (e.Key, (int)e.RecordNumber));
        return Array.Empty<(byte[], int)>();
    }

    private void ExecReindex(ReindexStmt rix)
    {
        int area = rix.In is not null ? ResolveAreaRef(rix.In) : Session.CurrentArea;
        var wa = Session.AreaAt(area);
        if (wa?.Table.SourcePath is not string path)
            return;   // no open table / no file — REINDEX is a no-op (nothing to rebuild).
        BuildTagOnDisk(path, w => w.Reindex(_ctx, includeDeleted: !_ctx.Deleted));
        var m = Meta(area);
        m.Ordered = null; m.OrderedFor = null; m.OrderPos = -1; m.KeyVisible = null;
    }

    /// <summary>Run an index-mutating writer action against <paramref name="path"/>: release every work
    /// area riding the file (so the exclusive writer + the .cdx rewrite never hit a sharing conflict),
    /// perform <paramref name="action"/>, re-open the areas in place, and drop their record/order caches.</summary>
    private void BuildTagOnDisk(string path, Action<DbfWriter> action)
    {
        string full = Path.GetFullPath(path);
        var reopen = Session.CloseAreasForPath(full);
        try
        {
            using var writer = DbfWriter.Open(path, new DbfOptions { LockMode = LockMode.Exclusive });
            action(writer);
        }
        finally
        {
            Session.ReopenAreas(reopen);
        }
        ResetMetaCachesForPath(path);
    }

    /// <summary>Drop the cached record / index-order / key-range state of every open area riding
    /// <paramref name="path"/> (after its files were rewritten out-of-band by an INDEX/REINDEX).</summary>
    private void ResetMetaCachesForPath(string path)
    {
        foreach (var w in Session.OpenAreas)
            if (SamePath(w.Table.SourcePath, path) && _meta.TryGetValue(w.Area, out var mm))
            {
                mm.Cached = null; mm.Ordered = null; mm.OrderedFor = null; mm.OrderPos = -1; mm.KeyVisible = null;
            }
    }

    /// <summary>True when <paramref name="tag"/> has two entries with identical key bytes — a CANDIDATE
    /// violation. The tag stores entries in key order, so any duplicate keys are adjacent.</summary>
    private static bool HasDuplicateKeys(CdxTag tag)
    {
        byte[]? prev = null;
        foreach (var e in tag.EnumerateEntries())
        {
            if (prev is not null && prev.AsSpan().SequenceEqual(e.Key))
                return true;
            prev = e.Key;
        }
        return false;
    }

    /// <summary>Roll a table's <c>.dbf</c>/<c>.cdx</c> back to <paramref name="pre"/> (used when a CANDIDATE
    /// INDEX must not persist): close the areas, restore/delete the sidecars, then re-open. When the
    /// pre-image had NO <c>.cdx</c> the freshly written one is DELETED (not left orphaned on disk).</summary>
    private void RollbackFiles(FileSnapshot pre, string path)
    {
        var reopen = Session.CloseAreasForPath(Path.GetFullPath(path));
        try
        {
            try { if (pre.Dbf is not null) File.WriteAllBytes(pre.Path, pre.Dbf); } catch { }
            string cdx = Path.ChangeExtension(pre.Path, ".cdx");
            if (pre.Cdx is not null) { try { File.WriteAllBytes(cdx, pre.Cdx); } catch { } }
            else { try { if (File.Exists(cdx)) File.Delete(cdx); } catch { } }
        }
        finally
        {
            Session.ReopenAreas(reopen);
        }
        ResetMetaCachesForPath(path);
    }

    // ─────────────────────────── SET COLLATE / SET KEY (microVFP P1 gap #1) ───────────────────────────

    /// <summary>SET COLLATE TO cSeq — set the session collation baked into the next INDEX tag. Only
    /// MACHINE + GENERAL are supported; any other sequence is a catchable error (never a silent fallback).
    /// <c>SET COLLATE TO</c> (no arg) resets to MACHINE. Read back by <c>SET("COLLATE")</c>.</summary>
    private void SetCollate(string arg)
    {
        string rest = arg;
        if (PrgScan.FirstWord(rest).Equals("TO", StringComparison.OrdinalIgnoreCase))
            rest = PrgScan.AfterFirstWord(rest);
        string seq = rest.Trim().Trim('"', '\'').Trim();
        if (seq.Length == 0) { _ctx.Collation = VfpCollations.Machine; return; }
        _ctx.Collation = seq.ToUpperInvariant() switch
        {
            "MACHINE" => VfpCollations.Machine,
            "GENERAL" => VfpCollations.General,
            _ => throw new MicroVfpRuntimeException(
                $"SET COLLATE TO {seq}: collating sequence not supported (only MACHINE and GENERAL)."),
        };
    }

    /// <summary>SET KEY TO [eLow [, eHigh]] | RANGE eLow, eHigh — limit the current work area's visible
    /// records to those whose MASTER-index key equals eLow (single) or lies in [eLow, eHigh] (range).
    /// Requires a controlling index. <c>SET KEY TO</c> (no arg) clears the range.</summary>
    private void SetKey(string arg)
    {
        string rest = arg;
        if (PrgScan.FirstWord(rest).Equals("TO", StringComparison.OrdinalIgnoreCase))
            rest = PrgScan.AfterFirstWord(rest);
        rest = rest.Trim();

        // Optional trailing IN <area> clause selects a different work area than the current one.
        int area = Session.CurrentArea;
        int inPos = PrgScan.IndexOfKeyword(rest, "IN");
        if (inPos >= 0)
        {
            string areaTok = PrgScan.AfterFirstWord(rest.Substring(inPos)).Trim();
            rest = rest.Substring(0, inPos).Trim();
            int resolved = int.TryParse(areaTok, out var n) ? n : (Session.FindAreaByAlias(areaTok)?.Area ?? 0);
            if (resolved > 0) area = resolved;
        }
        var m = Meta(area);

        if (rest.Length == 0)   // SET KEY TO → clear.
        {
            m.KeySet = false; m.KeyRange = false; m.KeyLow = null; m.KeyHigh = null; m.KeyVisible = null;
            return;
        }

        // The controlling order may be sourced from ANY open index (structural/extra .cdx or standalone
        // .idx), so resolve the master through the full-inventory path — not just the structural .cdx.
        using (var master = OpenMasterOrder(area))
        {
            if (master is null)
                throw new MicroVfpRuntimeException(
                    "SET KEY TO: no controlling index is active in the current work area (SET ORDER first).");

            // A NON-character key whose logical type cannot be resolved (a composite expression that isn't a
            // bare numeric/date field) cannot be decoded into a comparable value, so a range over it would
            // silently mismatch. Refuse LOUDLY rather than produce a wrong (empty / full) visible set.
            // Character keys are always handled byte-wise (below) — including UPPER(name)-style expressions
            // and GENERAL-collated weights — so they never hit this guard.
            if (!master.IsCharacterKey && master.KeyType == IndexKeyType.Unknown)
                throw new MicroVfpRuntimeException(
                    "SET KEY TO: the controlling index key type cannot be decoded for a range restriction.");
        }

        bool rangeKw = PrgScan.FirstWord(rest).Equals("RANGE", StringComparison.OrdinalIgnoreCase);
        if (rangeKw) rest = PrgScan.AfterFirstWord(rest).Trim();
        var parts = PrgScan.SplitTopCommas(rest).ToList();

        if (rangeKw || parts.Count > 1)
        {
            string lo = parts.Count > 0 ? parts[0].Trim() : string.Empty;
            string hi = parts.Count > 1 ? parts[1].Trim() : string.Empty;
            m.KeyRange = true;
            m.KeyLow = lo.Length > 0 ? EvalText(lo) : null;
            m.KeyHigh = hi.Length > 0 ? EvalText(hi) : null;
        }
        else
        {
            m.KeyRange = false;
            m.KeyLow = EvalText(rest);
            m.KeyHigh = null;
        }
        m.KeySet = true;
        m.KeyVisible = null;   // rebuilt lazily by Visible().
    }

    /// <summary>Open the controlling (master) ORDER of <paramref name="area"/> across the FULL open index
    /// set (structural <c>.cdx</c>, extra <c>.cdx</c>, standalone <c>.idx</c>), or null when none is active.
    /// The caller MUST dispose the result (it may own a temp file handle).</summary>
    private OrderSource? OpenMasterOrder(int area)
    {
        var m = Meta(area);
        return string.IsNullOrEmpty(m.Order) ? null : OpenOrderSource(area, m.Order!);
    }

    /// <summary>The STORED traversal direction of the controlling order, resolved over the full open set
    /// (a standalone <c>.idx</c> is always ascending). Feeds the SET ORDER … ASCENDING|DESCENDING override.</summary>
    private bool MasterDescending(int area)
    {
        using var src = OpenMasterOrder(area);
        return src?.Descending ?? false;
    }

    /// <summary>Build the set of recnos whose master-index key falls in the area's active SET KEY range.
    /// A missing tag ⇒ no restriction (every record). CHARACTER keys are compared on the SAME collated key
    /// bytes the tag STORES (so MACHINE, GENERAL weights and UPPER()-style expression keys all compare
    /// correctly instead of against decoded weight-garbage); other keys decode to a value and compare by
    /// value order.</summary>
    private HashSet<int> BuildKeyVisible(VfpSession.WorkArea wa, AreaMeta m)
    {
        var set = new HashSet<int>();
        // Resolve the controlling order across the FULL open index set (structural/extra .cdx or
        // standalone .idx), not just the structural .cdx, so SET KEY works for any open index.
        using var src = OpenMasterOrder(wa.Area);
        if (src is null)
        {
            for (int r = 1; r <= wa.Table.RecordCount; r++) set.Add(r);
            return set;
        }

        if (src.IsCharacterKey)
        {
            var coll = VfpCollations.FromSortSequence(src.CollationName);
            // Encode each bound the SAME way the tag stores keys: collated weights padded (0x20) to the
            // tag key length. Comparing bytes then mirrors the on-disk sort order exactly (this is what
            // SEEK does), so a GENERAL tag's weight keys and a MACHINE tag's raw bytes both match.
            byte[]? loBytes = m.KeyLow is { } lo ? CollatedBoundKey(coll, lo, src.KeyLength) : null;
            byte[]? hiBytes = m.KeyRange && m.KeyHigh is { } hi ? CollatedBoundKey(coll, hi, src.KeyLength) : null;
            byte[]? loNatural = !m.KeyRange && m.KeyLow is { } lo1 ? coll.GetCollatedKey((lo1.AsString ?? string.Empty).AsSpan()) : null;
            foreach (var (key, recno) in OrderedEntries(src))
            {
                if (CharKeyInRange(m, key, loBytes, hiBytes, loNatural))
                    set.Add(recno);
            }
            return set;
        }

        foreach (var (keyBytes, recno) in OrderedEntries(src))
        {
            var key = src.DecodeKey(keyBytes);
            if (KeyInRange(m, key)) set.Add(recno);
        }
        return set;
    }

    /// <summary>The stored-key bytes for a SET KEY bound over a CHARACTER tag: the bound's collated
    /// weights, right-padded with spaces (0x20) to (or truncated at) the tag key length — byte-identical
    /// to how the CDX builder laid the tag's own keys down.</summary>
    private static byte[] CollatedBoundKey(IVfpCollation coll, VfpValue bound, int keyLen)
    {
        var natural = coll.GetCollatedKey((bound.AsString ?? string.Empty).AsSpan());
        var key = new byte[keyLen];
        Array.Fill(key, (byte)0x20);
        int copy = Math.Min(natural.Length, keyLen);
        natural.AsSpan(0, copy).CopyTo(key);
        return key;
    }

    /// <summary>Whether one stored CHARACTER key falls in the active SET KEY range, compared on collated
    /// key bytes. Single-value: SET EXACT ON ⇒ full byte-equality against the padded bound; SET EXACT OFF ⇒
    /// the (unpadded) bound weights are a byte PREFIX of the stored key (SEEK semantics).</summary>
    private bool CharKeyInRange(AreaMeta m, byte[] storedKey, byte[]? loBytes, byte[]? hiBytes, byte[]? loNatural)
    {
        if (!m.KeyRange)
        {
            if (loBytes is null) return false;   // no bound value ⇒ nothing matches (never fail-open).
            return _ctx.Exact
                ? CompareBytesUnsigned(storedKey, loBytes) == 0
                : IsBytePrefix(loNatural ?? Array.Empty<byte>(), storedKey);
        }
        if (loBytes is not null && CompareBytesUnsigned(storedKey, loBytes) < 0) return false;
        if (hiBytes is not null && CompareBytesUnsigned(storedKey, hiBytes) > 0) return false;
        return true;
    }

    /// <summary>Unsigned, shorter-sorts-first byte comparison (the CDX key sort order).</summary>
    private static int CompareBytesUnsigned(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int d = a[i] - b[i];
            if (d != 0) return d < 0 ? -1 : 1;
        }
        return a.Length.CompareTo(b.Length);
    }

    /// <summary>True when <paramref name="needle"/> is an unsigned byte prefix of <paramref name="key"/>.</summary>
    private static bool IsBytePrefix(byte[] needle, byte[] key)
    {
        if (needle.Length > key.Length) return false;
        for (int i = 0; i < needle.Length; i++)
            if (needle[i] != key[i]) return false;
        return true;
    }

    private bool KeyInRange(AreaMeta m, IndexKey key)
    {
        // An undecodable key (Value null / KeyType Unknown) must fail CLOSED — never admit every record.
        if (key.Value is null) return false;
        if (!m.KeyRange)
            return m.KeyLow is { } single && KeyMatchesSingle(key, single);
        if (m.KeyLow is { } lo && CompareKeyToBound(key, lo) < 0) return false;
        if (m.KeyHigh is { } hi && CompareKeyToBound(key, hi) > 0) return false;
        return true;
    }

    private bool KeyMatchesSingle(IndexKey key, VfpValue bound)
        // Non-character keys only (character keys are compared byte-wise in CharKeyInRange); a numeric /
        // date single-value SET KEY is exact value equality.
        => key.Value is not null && CompareKeyToBound(key, bound) == 0;

    /// <summary>Three-way compare a decoded (non-character) index key against a SET KEY bound value in
    /// numeric/date value order. Undecodable/mismatched types compare equal.</summary>
    private int CompareKeyToBound(IndexKey key, VfpValue bound)
    {
        object? kv = key.Value;
        switch (kv)
        {
            case string ks:
                return _ctx.Collation.Compare(ks, bound.AsString ?? string.Empty);
            case int or long or double or decimal:
                return Convert.ToDouble(kv, CultureInfo.InvariantCulture).CompareTo((double)bound.AsNumber);
            case DateOnly kdo:
            {
                var b = bound.ToClr();
                DateOnly? bo = b as DateOnly? ?? (b is DateTime bt ? DateOnly.FromDateTime(bt) : null);
                return bo is { } bb ? kdo.CompareTo(bb) : 0;
            }
            case DateTime kdt:
            {
                var b = bound.ToClr();
                DateTime? bo = b as DateTime? ?? (b is DateOnly bd ? bd.ToDateTime(TimeOnly.MinValue) : null);
                return bo is { } bb ? kdt.CompareTo(bb) : 0;
            }
            default:
                return 0;
        }
    }

    private void ExecSet(SetStmt set)
    {
        string arg = set.Arguments.Trim();
        switch (set.Setting)
        {
            case "REPROCESS": SetReprocess(arg); break;
            case "DELETED": _ctx.Deleted = OnOff(arg); break;
            case "EXACT": _ctx.Exact = OnOff(arg); break;
            case "ANSI": _ctx.Ansi = OnOff(arg); break;
            case "COLLATE": SetCollate(arg); break;             // baked into the next INDEX tag; SET("COLLATE").
            case "UNIQUE": Runtime.Unique = OnOff(arg); break;  // session default for a clause-less INDEX; SET("UNIQUE").
            case "KEY": SetKey(arg); break;                     // master-index visible key range; feeds Visible().
            case "NEAR": _setNear = OnOff(arg); break;          // failed-SEEK pointer parking; SET("NEAR").
            case "DATASESSION": SetDataSession(arg); break;     // single-session stub; TO 1 no-op, else err 1540.
            default: break; // TALK / COMPATIBLE / DATA / PROCEDURE / … — irrelevant to results.
        }
    }

    private static bool OnOff(string s) => s.Trim().StartsWith("ON", StringComparison.OrdinalIgnoreCase);

    // SET DATASESSION TO n — in real VFP this switches among data sessions, but private data sessions
    // only ever come from forms (DataSession=2); a bare PRG/SP interpreter has just the default public
    // session #1. So this is a faithful single-session stub: TO 1 is a no-op; ANY other id (0, 5, …) is
    // an invalid session → VFP error 1540 "Session number is invalid." (verified against vfp9.exe:
    // SET("DATASESSION") is NUMERIC 1; TO 1 ok; TO 0 and TO 5 both raise 1540). SET("DATASESSION")=1 is
    // returned by FnSet. Full multi-session work-area registry is the deferred P3 architecture item.
    private void SetDataSession(string arg)
    {
        string s = arg.Trim();
        if (s.StartsWith("TO", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2).Trim();
        var v = EvalText(s);
        int n = IsNumeric(v) ? (int)v.AsNumber : int.TryParse(v.AsString, out var p) ? p : -1;
        if (n == 1) return;   // the only session microVFP has → no-op.
        throw new MicroVfpRuntimeException("Session number is invalid.");   // VFP error 1540.
    }

    private void SetReprocess(string arg)
    {
        var parts = arg.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int idx = parts.Length > 0 && parts[0].Equals("TO", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (idx >= parts.Length) return;
        string val = parts[idx];
        if (val.Equals("AUTOMATIC", StringComparison.OrdinalIgnoreCase)) { Runtime.Reprocess = -2; return; }
        var v = EvalText(val);
        Runtime.Reprocess = IsNumeric(v) ? (int)v.AsNumber
                           : int.TryParse(v.AsString, out var n) ? n : 0;
    }

    private void ExecOnError(OnErrorStmt oe)
    {
        switch (oe.Kind)
        {
            case OnErrorKind.Clear:
                InstallOnError(null, null);
                break;
            case OnErrorKind.Macro:
                // ON ERROR &var — EXPAND the macro NOW (definition-time): the variable's string content
                // becomes the installed handler (re-parsed as a command). The RI save/restore idiom
                // `ON ERROR &pcOldError` re-installs a previously saved handler; an EMPTY string clears it.
                InstallOnError(ExpandMacro(oe.Macro?.Text ?? string.Empty), null);
                break;
            default:
                // ON ERROR <command> — install the already-parsed command + retain its raw source text so
                // ON("ERROR") reads it back and a later `ON ERROR &var` can re-install it verbatim.
                InstallOnError(oe.CommandText, oe.Command);
                break;
        }
    }

    // Install (or clear) the ON ERROR handler. An empty/blank command clears it (Runtime.OnError = null →
    // ON("ERROR") = "" and the lock fail-fast branch sees no handler). A non-empty command text with no
    // pre-parsed statement (the macro path) is parsed here into a single executable command.
    private void InstallOnError(string? commandText, PrgStatement? parsed)
    {
        commandText = commandText?.Trim();
        if (string.IsNullOrEmpty(commandText))
        {
            _onErrorCmd = null; _onErrorText = null; Runtime.OnError = null;
            return;
        }
        _onErrorCmd = parsed ?? ParseSingle(commandText);
        _onErrorText = commandText;
        Runtime.OnError = commandText;
    }

    // Expand an `ON ERROR &var[.]` macro to the variable's CURRENT string content (the handler command).
    // The dot terminator and leading '&' are stripped; an undefined variable yields "" (⇒ clear).
    private string ExpandMacro(string macroText)
    {
        string t = macroText.Trim();
        if (t.StartsWith('&')) t = t.Substring(1);
        t = t.Trim();
        if (t.EndsWith('.')) t = t.Substring(0, t.Length - 1);
        t = t.Trim();
        return t.Length == 0 ? string.Empty : Memory.Get(t).AsString;
    }

    // Parse a single command line into one PrgStatement (the ON ERROR handler body); null when it does not
    // yield exactly one top-level statement.
    private static PrgStatement? ParseSingle(string command)
    {
        try
        {
            var prog = PrgParser.Parse(command);
            return prog.Main.Count > 0 ? prog.Main[0] : null;
        }
        catch { return null; }
    }

    // ─────────────────────────── transactions (snapshot / restore) ───────────────────────────

    private void BeginTransaction() => _txn.Add(new TxnFrame());

    private void EndTransaction()
    {
        if (_txn.Count == 0) return;                      // unbalanced END is a no-op (VFP would error).
        var done = _txn[^1];
        _txn.RemoveAt(_txn.Count - 1);
        // COMMIT: the writes already landed on the live files; just hand any not-yet-owned snapshots to
        // the enclosing transaction so an OUTER rollback can still revert inner-committed changes.
        if (_txn.Count > 0)
        {
            var parent = _txn[^1];
            foreach (var kv in done.Snaps)
                if (!Snapshotted(kv.Key)) parent.Snaps[kv.Key] = kv.Value;
        }
    }

    private void RollbackTransaction()
    {
        if (_txn.Count == 0) return;
        var frame = _txn[^1];
        _txn.RemoveAt(_txn.Count - 1);
        foreach (var snap in frame.Snaps.Values) RestoreSnapshot(snap);
    }

    /// <summary>True when <paramref name="path"/> already has a snapshot in ANY active transaction frame
    /// (so the first writer per table in the whole transaction stack captures the pre-image once).</summary>
    private bool Snapshotted(string path)
    {
        foreach (var f in _txn)
            if (f.Snaps.ContainsKey(path)) return true;
        return false;
    }

    /// <summary>Before a table WRITE inside a transaction, capture the pre-image of its <c>.dbf</c>
    /// (+ sibling <c>.cdx</c>/<c>.fpt</c>) once, into the innermost frame — so a ROLLBACK restores it.</summary>
    private void SnapshotForTxn(string? path)
    {
        if (_txn.Count == 0 || path is null || Snapshotted(path)) return;
        _txn[^1].Snaps[path] = CaptureSnapshot(path);
    }

    /// <summary>Capture the pre-image of a table's <c>.dbf</c> (+ sibling <c>.cdx</c>/<c>.fpt</c>) into a
    /// standalone snapshot — independent of the transaction stack. P3b uses this to make a PLAIN (no open
    /// user transaction) parent <c>REPLACE</c>/<c>INSERT</c> revertible: the parent write lands first (so the
    /// auto-fired update/insert trigger can read the NEW key via the current field), and if that trigger
    /// returns <c>.F.</c>/errors (a RESTRICT abort), <see cref="RestoreSnapshot"/> rolls the parent write back
    /// so the parent op + its triggered cascade are atomic.</summary>
    private static FileSnapshot CaptureSnapshot(string path)
    {
        var snap = new FileSnapshot { Path = path };
        try { if (File.Exists(path)) snap.Dbf = File.ReadAllBytes(path); } catch { }
        string cdx = Path.ChangeExtension(path, ".cdx");
        string fpt = Path.ChangeExtension(path, ".fpt");
        try { if (File.Exists(cdx)) snap.Cdx = File.ReadAllBytes(cdx); } catch { }
        try { if (File.Exists(fpt)) snap.Fpt = File.ReadAllBytes(fpt); } catch { }
        return snap;
    }

    /// <summary>ROLLBACK one table: close any work area on it, rewrite its files from the pre-image, then
    /// re-open the area(s) in place (same number / alias / order) so the reverted state is visible.</summary>
    private void RestoreSnapshot(FileSnapshot snap)
    {
        // Capture the work areas currently riding this file so they can be re-opened after the restore.
        // TablePath (the actual .dbf source) is captured alongside the alias: a riopen SCRATCH area has an
        // alias like "__ri6" which is NOT a DBC member / on-disk file, so re-opening BY ALIAS would throw —
        // re-open BY PATH instead (TryDbcName re-resolves the path to its DBC member to keep long names).
        // The buffering mode + any pending buffer (and the CURSORSETPROP free-table props) must SURVIVE a
        // rollback: a TABLEUPDATE that fails half-way rolls the file(s) back but has to leave the microVFP
        // buffer intact (so the caller can keep the edits buffered and return .F.). RestoreSnapshot otherwise
        // rebuilds AreaMeta from scratch, which would silently drop the buffer + reset the mode to 1.
        var reopen = new List<(int Area, string Alias, string? Order, bool Excl, bool NoUpd, string? TablePath,
                               int Buffering, TableBuffer? Buf, string? SourceName, VfpValue? SourceType, string? DatabaseProp)>();
        foreach (var w in Session.OpenAreas)
            if (SamePath(w.Table.SourcePath, snap.Path))
            {
                _meta.TryGetValue(w.Area, out var mm);
                reopen.Add((w.Area, w.Alias, mm?.Order, w.Exclusive, w.NoUpdate, w.Table.SourcePath,
                            mm?.Buffering ?? 1, mm?.Buf, mm?.SourceName, mm?.SourceType, mm?.DatabaseProp));
            }

        foreach (var r in reopen) Session.CloseArea(r.Area);

        try { if (snap.Dbf is not null) File.WriteAllBytes(snap.Path, snap.Dbf); } catch { }
        string cdx = Path.ChangeExtension(snap.Path, ".cdx");
        string fpt = Path.ChangeExtension(snap.Path, ".fpt");
        try { if (snap.Cdx is not null) File.WriteAllBytes(cdx, snap.Cdx); } catch { }
        try { if (snap.Fpt is not null) File.WriteAllBytes(fpt, snap.Fpt); } catch { }

        int savedCur = Session.CurrentArea;
        foreach (var r in reopen)
        {
            Session.Use(r.TablePath ?? r.Alias, r.Area, r.Alias, again: false, exclusive: r.Excl, noUpdate: r.NoUpd);
            _meta[r.Area] = new AreaMeta
            {
                Order = r.Order, Buffering = r.Buffering, Buf = r.Buf,
                SourceName = r.SourceName, SourceType = r.SourceType, DatabaseProp = r.DatabaseProp,
            };
            GoTop(r.Area);
        }
        Session.SelectArea(savedCur);
    }

    private static bool SamePath(string? a, string? b)
        => a is not null && b is not null &&
           string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>After an out-of-band write to <paramref name="path"/>, refresh EVERY open work area riding
    /// that file — not just the one that wrote — and drop its record/order caches. VFP9 shares one buffer
    /// across all <c>USE..AGAIN</c> handles of a file, so a sibling handle (e.g. a riopen scratch cursor)
    /// must see the write immediately; without this it would serve stale bytes until reopened on its own.</summary>
    private void ReopenFileAreas(string path)
    {
        var areas = new List<int>();
        foreach (var w in Session.OpenAreas)
            if (SamePath(w.Table.SourcePath, path)) areas.Add(w.Area);
        foreach (var a in areas)
        {
            Session.ReopenAreaTable(a);
            if (_meta.TryGetValue(a, out var mm)) { mm.Cached = null; mm.Ordered = null; }
        }
    }

    // ─────────────────────────── REPLACE / DELETE / RECALL / SUM ───────────────────────────

    private void ExecReplace(ReplaceStmt rp)
    {
        // Default scope = the CURRENT record of the target area (risk #4 — never the whole table).
        int area = rp.In is not null ? ResolveAreaRef(rp.In) : AreaOfClause(rp.Clauses[0]);
        var wa = Session.AreaAt(area);
        if (wa is null) return;
        var m = Meta(area);

        // BUFFERING (mode>1): defer the field writes into the area's buffer instead of writing through.
        if (m.Buffering > 1) { BufferReplace(rp, area, wa, m); return; }

        int recIndex = m.RecNo - 1;
        if (recIndex < 0 || recIndex >= wa.Table.RecordCount) return;

        var path = wa.Table.SourcePath;
        if (path is null) return;

        // ATOMICITY (P3b): when a bound UPDATE trigger will auto-fire, the parent key change must be
        // REVERTIBLE — the parent write lands first (so the trigger reads the NEW key via the current
        // field) but a RESTRICT/error abort (trigger returns .F.) has to roll the parent key back too, or
        // the parent moves to the new key while its children keep the old one (orphans). Capture the
        // parent pre-image up front; restore it below if the trigger aborts.
        bool autoFireUpdate = EnforceReferentialIntegrity && ResolveTriggerProc(RiEvent.Update, wa) is not null;
        FileSnapshot? parentSnap = autoFireUpdate ? CaptureSnapshot(path) : null;

        SnapshotForTxn(path);

        using (var writer = DbfWriter.Open(path, new DbfOptions { LockMode = LockMode.Shared }))
        {
            var values = new object?[writer.Schema.Columns.Count];
            for (int i = 0; i < values.Length; i++) values[i] = DbfWriter.KeepValue;

            foreach (var clause in rp.Clauses)
            {
                string field = StripQualifier(NameOf(clause.Field));
                int idx = ColumnIndex(wa.Table, field);
                if (idx < 0 || idx >= values.Length) continue;
                // OLDVAL: capture the value at the START of THIS change (the field's CURRENT on-disk value
                // before this REPLACE), OVERWRITING any prior entry. A second key REPLACE of the same record
                // with no intervening pointer move must re-base OLDVAL to the value the first REPLACE
                // committed — else the auto-fired __RI_UPDATE_* would SCAN children FOR a key that was
                // already cascaded away (no cascade → orphans). The trigger reads OLDVAL() after the write.
                RememberOldVal(m, field, ReadField(wa, field));
                var v = Eval(clause.Value);
                char type = wa.Table.Columns[idx].Type;
                values[idx] = v.IsNull ? null : (type is 'C' or 'M' or 'V' ? v.AsString : v.ToClr());
            }
            writer.UpdateRecord(recIndex, values);
            writer.Flush();
        }
        // Refresh EVERY handle on this file (USE..AGAIN shares one buffer in VFP) so siblings see the write;
        // this also re-bases the writing area's own read handle and drops its caches (incl. index order).
        ReopenFileAreas(path);
        // P3b: a key-changing REPLACE on a parent fires its bound update trigger, cascading the new key
        // to children (the trigger reads OLDVAL() — captured above — for the OLD key). A .F./error return
        // is a RESTRICT/update abort: the trigger already rolled back its child cascade (riend(.F.) →
        // ROLLBACK) — restore the parent pre-image too so the whole op is atomic (parent key reverts).
        if (autoFireUpdate && !FireDmlTrigger(RiEvent.Update, area) && parentSnap is not null)
            RestoreSnapshot(parentSnap);
    }

    // OLDVAL() per field. ALWAYS overwrites: each entry holds the value at the start of the CURRENT change
    // (captured by the REPLACE just before it writes the field). A pointer move clears the whole map
    // (GoTop/GoBottom/GoRecord/Skip), so per-record buffering is preserved; consecutive REPLACEs of the
    // SAME record each re-base from the value the previous one committed.
    private static void RememberOldVal(AreaMeta m, string field, object? value)
    {
        m.OldVals ??= new(StringComparer.OrdinalIgnoreCase);
        m.OldVals[field] = value;
    }

    // ─────────────────────────── buffered write routing (microVFP P1 gap #4) ───────────────────────────

    /// <summary>Buffer a REPLACE for the current record instead of writing through. For an EXISTING row the
    /// pre-change on-disk value is captured once (OLDVAL's buffer-start baseline); for a buffered APPENDED
    /// row the edit lands directly on the append entry (which starts as its own buffer state).</summary>
    private void BufferReplace(ReplaceStmt rp, int area, VfpSession.WorkArea wa, AreaMeta m)
    {
        int rcTable = wa.Table.RecordCount;
        int recno = m.RecNo;
        var buf = m.Buf ??= new TableBuffer();

        RowEdit? edit = null;
        Dictionary<int, object?> target;
        if (recno > rcTable)                                   // editing a buffered appended row.
        {
            int ai = recno - rcTable - 1;
            if (ai < 0 || ai >= buf.Appends.Count) { if (buf.IsEmpty) m.Buf = null; return; }
            target = buf.Appends[ai].Fields;
        }
        else
        {
            if (recno < 1 || recno > rcTable) { if (buf.IsEmpty) m.Buf = null; return; }
            if (!buf.Rows.TryGetValue(recno, out edit)) { edit = new RowEdit(); buf.Rows[recno] = edit; }
            target = edit.Fields;
        }

        foreach (var clause in rp.Clauses)
        {
            string field = StripQualifier(NameOf(clause.Field));
            int idx = ColumnIndex(wa.Table, field);
            if (idx < 0) continue;
            if (edit is not null && !edit.Old.ContainsKey(field))
                edit.Old[field] = ReadFieldOnDisk(wa, field);   // capture the buffer-start (on-disk) value once.
            var v = Eval(clause.Value);
            char type = wa.Table.Columns[idx].Type;
            target[idx] = v.IsNull ? null : (type is 'C' or 'M' or 'V' ? v.AsString : v.ToClr());
        }
    }

    /// <summary>Buffer a DELETE/RECALL mark for the current record (existing row or buffered append).</summary>
    private void BufferDeleteFlag(int area, VfpSession.WorkArea wa, AreaMeta m, bool deleted)
    {
        int rcTable = wa.Table.RecordCount;
        int recno = m.RecNo;
        var buf = m.Buf ??= new TableBuffer();
        if (recno > rcTable)
        {
            int ai = recno - rcTable - 1;
            if (ai >= 0 && ai < buf.Appends.Count) buf.Appends[ai].Deleted = deleted;
        }
        else if (recno >= 1 && recno <= rcTable)
        {
            if (!buf.Rows.TryGetValue(recno, out var e)) { e = new RowEdit(); buf.Rows[recno] = e; }
            e.DeletedOverride = deleted;
        }
        if (buf.IsEmpty) m.Buf = null;
    }

    /// <summary>Buffer an INSERT (append) into the target area's buffer: the row is VISIBLE on the live
    /// cursor immediately (its field values are evaluated now) but is not written to disk until TABLEUPDATE
    /// replays the statement through the ordinary INSERT path (so RI fires deferred).</summary>
    private void BufferAppend(VfpSession.WorkArea wa, AreaMeta m, InsertStmt ins, InsertStatement stmt)
    {
        var ae = new AppendEntry { Stmt = ins };
        var cols = stmt.Columns;
        for (int i = 0; i < stmt.Values.Count; i++)
        {
            int idx = cols is not null && i < cols.Count ? ColumnIndex(wa.Table, cols[i]) : i;
            if (idx < 0 || idx >= wa.Table.Columns.Count) continue;
            VfpValue v;
            try { v = stmt.Values[i].Evaluate(_row, _ctx); } catch { v = VfpValue.Null; }
            char type = wa.Table.Columns[idx].Type;
            ae.Fields[idx] = v.IsNull ? null : (type is 'C' or 'M' or 'V' ? v.AsString : v.ToClr());
        }
        (m.Buf ??= new TableBuffer()).Appends.Add(ae);
    }

    /// <summary>Write ONE buffered existing-row edit (field changes + delete/recall mark) to disk, firing the
    /// bound RI trigger when enforced (deferred to commit per hackfox s4g346). Returns false when a RESTRICT
    /// trigger aborts the row (the write is rolled back). Mirrors <see cref="ExecReplace"/>'s disk tail.</summary>
    private bool CommitExistingRow(int area, VfpSession.WorkArea wa, string path, int recno, RowEdit edit)
    {
        int recIndex = recno - 1;
        if (recIndex < 0 || recIndex >= wa.Table.RecordCount) return true;
        var m = Meta(area);
        bool hasFields = edit.Fields.Count > 0;

        // A buffered DELETE fires the delete trigger BEFORE the mark (ExecDelete order); a RESTRICT aborts.
        if (EnforceReferentialIntegrity && edit.DeletedOverride == true)
        {
            GoRecordCore(area, recno);
            if (!FireDmlTrigger(RiEvent.Delete, area)) return false;
        }

        bool autoFireUpdate = EnforceReferentialIntegrity && hasFields && ResolveTriggerProc(RiEvent.Update, wa) is not null;
        FileSnapshot? parentSnap = autoFireUpdate ? CaptureSnapshot(path) : null;

        SnapshotForTxn(path);
        using (var writer = DbfWriter.Open(path, new DbfOptions { LockMode = LockMode.Shared }))
        {
            if (hasFields)
            {
                var values = new object?[writer.Schema.Columns.Count];
                for (int i = 0; i < values.Length; i++) values[i] = DbfWriter.KeepValue;
                foreach (var kv in edit.Fields) if (kv.Key >= 0 && kv.Key < values.Length) values[kv.Key] = kv.Value;
                writer.UpdateRecord(recIndex, values);
            }
            if (edit.DeletedOverride is bool del) { if (del) writer.Delete(recIndex); else writer.Recall(recIndex); }
            writer.Flush();
        }
        ReopenFileAreas(path);

        if (autoFireUpdate)
        {
            GoRecordCore(area, recno);
            m.OldVals = new Dictionary<string, object?>(edit.Old, StringComparer.OrdinalIgnoreCase);
            if (!FireDmlTrigger(RiEvent.Update, area) && parentSnap is not null) { RestoreSnapshot(parentSnap); return false; }
        }
        return true;
    }

    /// <summary>TABLEUPDATE core — commit the current row (allRows=false) or every buffered row + append
    /// (allRows=true) of <paramref name="area"/> to disk. Returns false when a RESTRICT trigger aborts a
    /// row (that row stays buffered). Committed rows/appends leave the buffer.</summary>
    private bool CommitBuffer(int area, bool allRows)
    {
        if (!_meta.TryGetValue(area, out var m)) return true;
        // TABLEUPDATE on an area whose buffering was NEVER enabled (mode 1) is a catchable ERROR in VFP9,
        // not a trivial success — distinguish it from "buffering on but nothing pending" (which returns .T.).
        if (m.Buffering <= 1)
            throw new MicroVfpRuntimeException("TABLEUPDATE(): the current work area is not buffered.");
        if (m.Buf is null) return true;                        // buffering enabled, nothing pending → .T.
        var wa = Session.AreaAt(area);
        if (wa?.Table.SourcePath is not string path) return true;
        var buf = m.Buf;
        bool ok = true;
        _inBufferCommit = true;
        try
        {
            if (allRows)
            {
                // ATOMIC (task requirement): wrap the whole multi-row commit in a transaction frame so the
                // per-file pre-images are captured (SnapshotForTxn, incl. any child tables a deferred RI
                // trigger touches). On the FIRST RESTRICT abort we stop and roll EVERYTHING back, leaving the
                // buffer INTACT and returning .F. — never a half-written table with a half-cleared buffer.
                BeginTransaction();
                var doneRows = new List<int>();
                var doneApps = new List<AppendEntry>();
                foreach (var kv in new List<KeyValuePair<int, RowEdit>>(buf.Rows))
                {
                    if (CommitExistingRow(area, wa, path, kv.Key, kv.Value)) doneRows.Add(kv.Key);
                    else { ok = false; break; }
                }
                if (ok)
                    foreach (var ae in new List<AppendEntry>(buf.Appends))
                    {
                        if (CommitAppend(ae)) doneApps.Add(ae);
                        else { ok = false; break; }
                    }
                if (ok)
                {
                    EndTransaction();                          // commit: the writes already landed on disk.
                    foreach (var r in doneRows) buf.Rows.Remove(r);
                    foreach (var ae in doneApps) buf.Appends.Remove(ae);
                }
                else
                {
                    RollbackTransaction();                     // revert every already-written row/append.
                }
            }
            else
            {
                int rec = m.RecNo, rcTable = wa.Table.RecordCount;
                if (rec >= 1 && rec <= rcTable && buf.Rows.TryGetValue(rec, out var e))
                {
                    if (CommitExistingRow(area, wa, path, rec, e)) buf.Rows.Remove(rec); else ok = false;
                }
                else if (rec > rcTable)
                {
                    int ai = rec - rcTable - 1;
                    if (ai >= 0 && ai < buf.Appends.Count)
                    {
                        if (CommitAppend(buf.Appends[ai])) buf.Appends.RemoveAt(ai); else ok = false;
                    }
                }
            }
        }
        finally { _inBufferCommit = false; }
        // A rollback rebuilds AreaMeta (buffer preserved), so re-fetch before the empty-buffer cleanup.
        if (_meta.TryGetValue(area, out var cm) && cm.Buf is { IsEmpty: true }) cm.Buf = null;
        return ok;
    }

    /// <summary>Commit ONE buffered append: write the values that were FIXED at APPEND time straight to disk
    /// (NOT a re-run of the INSERT's VALUES expressions — a memvar that changed since the APPEND cannot alter
    /// the committed row), then fire the DEFERRED bound insert trigger / RI (hackfox s4g346). A RESTRICT
    /// abort rolls the appended row back. Returns true iff the row is actually persisted.</summary>
    private bool CommitAppend(AppendEntry ae)
    {
        string? table = (ae.Stmt.Parsed as InsertStatement)?.Table
                     ?? (SafeParseSql(ae.Stmt.Sql) as InsertStatement)?.Table;
        if (table is null) return false;
        var wa = Session.FindAreaByAlias(table);
        if (wa?.Table.SourcePath is not string path) return false;
        int area = wa.Area;

        // Row built from the pre-evaluated field values; an omitted column gets the type's real blank.
        var vals = new object?[wa.Table.Columns.Count];
        for (int i = 0; i < vals.Length; i++)
            vals[i] = ae.Fields.TryGetValue(i, out var v) ? v : BlankFor(wa.Table.Columns[i]);

        FileSnapshot? snap = CaptureSnapshot(path);            // pre-image for a RESTRICT rollback.
        SnapshotForTxn(path);
        using (var writer = DbfWriter.Open(path, new DbfOptions { LockMode = LockMode.Shared }))
        {
            writer.AppendRecord(vals);
            writer.Flush();
        }
        ReopenFileAreas(path);

        // Deferred insert trigger / RI: positioned ON the new (last physical) record; .F. ⇒ RESTRICT abort.
        if (EnforceReferentialIntegrity && ResolveTriggerProc(RiEvent.Insert, wa) is not null)
        {
            int savedArea = Session.CurrentArea;
            Session.SelectArea(area);
            var fresh = Session.AreaAt(area);
            if (fresh is not null) GoRecordCore(area, fresh.Table.RecordCount);
            bool ok = FireDmlTrigger(RiEvent.Insert, area);
            Session.SelectArea(savedArea);
            if (!ok) { if (snap is not null) RestoreSnapshot(snap); return false; }
        }

        // A buffered DELETE on the appended row commits as a deleted physical record.
        if (ae.Deleted && Session.AreaAt(area) is { } after && after.Table.RecordCount > 0)
        {
            using var w2 = DbfWriter.Open(path, new DbfOptions { LockMode = LockMode.Shared });
            w2.Delete(after.Table.RecordCount - 1);
            w2.Flush();
            ReopenFileAreas(path);
        }
        return true;
    }

    private int AreaOfClause(ReplaceClause c)
    {
        string name = NameOf(c.Field);
        int dot = name.IndexOf('.');
        if (dot > 0)
        {
            var wa = Session.FindAreaByAlias(name[..dot]);
            if (wa is not null) return wa.Area;
        }
        return Session.CurrentArea;
    }

    private void ExecDelete(DeleteStmt del)
    {
        if (del.Scope is not null || del.For is not null) return; // only NEXT-1 default scope is in scope.
        int area = del.In is not null ? ResolveAreaRef(del.In) : Session.CurrentArea;
        var mdel = Meta(area);
        if (mdel.Buffering > 1 && Session.AreaAt(area) is { } wdel) { BufferDeleteFlag(area, wdel, mdel, deleted: true); return; }
        // P3b: a bound delete trigger fires BEFORE the mark; a RESTRICT rule returning .F. ABORTS it.
        if (EnforceReferentialIntegrity && !FireDmlTrigger(RiEvent.Delete, area)) return;
        WriteFlag(area, recall: false);
    }

    private void ExecRecall(RecallStmt rc)
    {
        if (rc.Scope is not null || rc.For is not null) return;
        int area = Session.CurrentArea;
        var mrc = Meta(area);
        if (mrc.Buffering > 1 && Session.AreaAt(area) is { } wrc) { BufferDeleteFlag(area, wrc, mrc, deleted: false); return; }
        WriteFlag(area, recall: true);
    }

    private void WriteFlag(int area, bool recall)
    {
        var wa = Session.AreaAt(area);
        if (wa is null) return;
        var m = Meta(area);
        int recIndex = m.RecNo - 1;
        if (recIndex < 0 || recIndex >= wa.Table.RecordCount) return;
        var path = wa.Table.SourcePath;
        if (path is null) return;
        SnapshotForTxn(path);
        using (var writer = DbfWriter.Open(path, new DbfOptions { LockMode = LockMode.Shared }))
        {
            if (recall) writer.Recall(recIndex); else writer.Delete(recIndex);
            writer.Flush();
        }
        ReopenFileAreas(path);   // refresh EVERY handle on this file (USE..AGAIN shares one buffer in VFP).
    }

    private void ExecSum(SumStmt sum)
    {
        int area = Session.CurrentArea;
        var wa = Session.AreaAt(area);
        if (wa is null) return;
        var m = Meta(area);
        int rc = wa.Table.RecordCount;
        int n = sum.Expressions.Count;
        var acc = new double[n];

        for (int rec = 1; rec <= rc; rec++)
        {
            if (!Visible(wa, rec)) continue;
            m.RecNo = rec; m.Eof = false; m.Bof = false; m.Cached = null;
            if (sum.While is not null && !Truth(Eval(sum.While))) break;
            if (sum.For is not null && !Truth(Eval(sum.For))) continue;
            for (int k = 0; k < n; k++)
            {
                var v = Eval(sum.Expressions[k]);
                if (!v.IsNull) acc[k] += v.AsDouble;
            }
        }
        m.RecNo = rc + 1; m.Eof = true; m.Bof = false; m.Cached = null;   // SUM leaves the pointer at EOF.
        for (int k = 0; k < sum.To.Count && k < n; k++)
            Memory.Set(sum.To[k], VfpValue.Number(acc[k]));
    }

    // ─────────────────────────── record-pointer model ───────────────────────────

    private sealed class AreaMeta
    {
        public int RecNo = 1;
        public bool Eof;
        public bool Bof;
        public bool Found;
        public string? Order;
        public DbfRecord? Cached;   // current-record cache (invalidated on move/write).
        public int CachedRec = -1;
        public List<int>? Ordered;  // recnos in the active index order (null ⇒ physical order).
        public string? OrderedFor;  // the tag name the Ordered cache was built for.
        public int OrderPos = -1;   // current position within Ordered (when index-ordered).
        public bool OrderReversed;  // SET ORDER … DESCENDING|ASCENDING override: traverse the tag reversed.
        public Dictionary<string, object?>? OldVals; // OLDVAL() per field (pre-change buffer values).

        // ── row/table BUFFERING (microVFP P1 gap #4) ──
        // Buffering mode 1..5 (1=none/write-through [default], 2=pess-row, 3=opt-row, 4=pess-table,
        // 5=opt-table). Single-process ⇒ pessimistic==optimistic (no conflict detection is fabricated).
        public int Buffering = 1;
        public TableBuffer? Buf;         // pending buffered edits/appends (null ⇒ nothing buffered).
        // CURSORSETPROP free-table property subset (get/set-backed per area; no view model).
        public string? SourceName;       // CURSORGETPROP/SETPROP("SourceName") — defaults to the alias.
        public VfpValue? SourceType;     // CURSORGETPROP/SETPROP("SourceType").
        public string? DatabaseProp;     // CURSORGETPROP/SETPROP("Database").

        // ── SET KEY (master-index key-range scope; microVFP P1 gap #1) ──
        public bool KeySet;             // a SET KEY range is active on this area's master index.
        public bool KeyRange;           // true ⇒ [KeyLow, KeyHigh] range; false ⇒ single-value match.
        public VfpValue? KeyLow;        // the single value / range low bound (null ⇒ open low).
        public VfpValue? KeyHigh;       // the range high bound (null ⇒ open high / single-value mode).
        public HashSet<int>? KeyVisible; // cached recnos within the key range (null ⇒ rebuild lazily).

        // ── SET RELATION (this area as PARENT; microVFP P1 gap #2) ──
        public List<Relation>? Relations; // child relations set on THIS area (null ⇒ none).

        // ── multi-index model (microVFP INDEX/ORDER MODEL) ──
        // Non-structural index files opened via SET INDEX TO / USE … INDEX, in OPEN order (full paths).
        // The structural .cdx (auto-opened on USE) is held by WorkArea.Cdx and is NOT listed here.
        public List<string>? ExtraIndexes;
    }

    /// <summary>One parent→child link of a <c>SET RELATION</c>: the key expression (evaluated in the
    /// PARENT area on each parent move) plus the child work area it re-seeks. <see cref="OneToMany"/> is
    /// set by <c>SET SKIP</c>.</summary>
    private sealed class Relation
    {
        public PrgExpr Key = null!;    // relation key expression (evaluated in the PARENT area).
        public string KeyText = "";    // its raw source — SET("RELATION") / RELATION(n) read this back.
        public int ChildArea;          // target work-area number.
        public string ChildAlias = ""; // target alias (uppercase) — TARGET(n) / SET("RELATION").
        public bool OneToMany;         // SET SKIP marked this child one-to-many.
    }

    // ─────────────────────────── buffering model (microVFP P1 gap #4) ───────────────────────────
    //
    // A per-work-area buffer that DEFERS REPLACE/DELETE/RECALL (existing rows) and INSERT (appends) when
    // the area's Buffering mode is >1, so they can be committed atomically (TABLEUPDATE) or discarded
    // (TABLEREVERT). While buffered, field reads/DELETED()/OLDVAL()/GETFLDSTATE() reflect the buffer;
    // CURVAL() reads the ON-DISK value. Single-process ⇒ CURVAL == disk and optimistic-vs-pessimistic
    // conflict is moot (no fabricated conflict errors). Buffered appends are VISIBLE on the live cursor
    // (RECCOUNT/GO n) but not on disk until TABLEUPDATE, which replays them through the ordinary INSERT
    // path (so the DBC RULE/TRIGGER/RI pipeline fires DEFERRED, per hackfox s4g346).

    private sealed class TableBuffer
    {
        // Edits to EXISTING physical rows, keyed by 1-based recno.
        public readonly Dictionary<int, RowEdit> Rows = new();
        // Buffered appended rows (table buffering only), in append order. Recno = RecordCount + 1 + index.
        public readonly List<AppendEntry> Appends = new();
        public bool IsEmpty => Rows.Count == 0 && Appends.Count == 0;
    }

    private sealed class RowEdit
    {
        public readonly Dictionary<int, object?> Fields = new();               // colIndex → buffered new value.
        public bool? DeletedOverride;                                          // null ⇒ no pending delete change.
        public readonly Dictionary<string, object?> Old = new(StringComparer.OrdinalIgnoreCase); // OLDVAL per field.
    }

    private sealed class AppendEntry
    {
        public InsertStmt Stmt = null!;                                        // replayed through ExecInsert at commit.
        public readonly Dictionary<int, object?> Fields = new();              // colIndex → value (visible pre-commit).
        public bool Deleted;                                                   // a buffered DELETE on the appended row.
    }

    // Cheap fast-path gate: true once any area has been switched to a buffering mode (2..5). Lets the
    // default (write-through) INSERT path skip the buffer pre-check entirely — never reset (a stray reparse
    // is harmless), so the ~2141 Buffering=1 tests are byte-for-byte unchanged.
    private bool _anyBuffering;
    // Re-entrancy guard: while TABLEUPDATE replays buffered appends through ExecInsert, they must WRITE
    // THROUGH (not re-buffer).
    private bool _inBufferCommit;

    /// <summary>Effective record count including any buffered appended rows (== the on-disk count when the
    /// area has no buffer, so all non-buffered navigation is unchanged).</summary>
    private int EffCount(int area, VfpSession.WorkArea wa)
    {
        int rc = wa.Table.RecordCount;
        if (_meta.TryGetValue(area, out var m) && m.Buffering > 1 && m.Buf is { } buf) rc += buf.Appends.Count;
        return rc;
    }

    private AreaMeta Meta(int area)
    {
        if (_meta.TryGetValue(area, out var m)) return m;
        m = new AreaMeta();
        _meta[area] = m;
        GoTopCore(area);   // lazy meta creation is NOT a user move → never fires the relation hook.
        return m;
    }

    private bool Visible(VfpSession.WorkArea wa, int rec)
    {
        // Buffered appended rows live past the physical count: visible unless buffered-deleted; SET KEY /
        // the on-disk deleted-flag do not apply (the row is not on disk yet).
        if (rec > wa.Table.RecordCount)
        {
            if (_meta.TryGetValue(wa.Area, out var am) && am.Buffering > 1 && am.Buf is { } ab)
            {
                int ai = rec - wa.Table.RecordCount - 1;
                if (ai >= 0 && ai < ab.Appends.Count)
                    return !(_ctx.Deleted && ab.Appends[ai].Deleted);
            }
            return false;
        }
        if (_ctx.Deleted && wa.Table.IsRecordDeleted(rec - 1))
            return false;
        // SET KEY: only records whose MASTER-index key falls in the active key range are visible.
        // The visible set is derived from the controlling tag's entries once, then cached (invalidated
        // on a data change / order change / SET KEY change). Use TryGetValue — never Meta() — to avoid
        // re-entering GoTop while a navigation loop is already inside Visible().
        if (_meta.TryGetValue(wa.Area, out var m) && m.KeySet)
        {
            m.KeyVisible ??= BuildKeyVisible(wa, m);
            if (!m.KeyVisible.Contains(rec))
                return false;
        }
        return true;
    }

    /// <summary>Resolve the active index tag for <paramref name="area"/> (from <see cref="AreaMeta.Order"/>),
    /// building/refreshing the cached recno sequence in index order; null ⇒ navigate physically.</summary>
    private List<int>? ActiveOrder(int area)
    {
        var m = Meta(area);
        var wa = Session.AreaAt(area);
        if (wa is null || string.IsNullOrEmpty(m.Order)) return null;
        if (m.Ordered is not null && m.OrderedFor == m.Order) return m.Ordered;

        using var src = OpenOrderSource(area, m.Order!);   // structural cdx, extra cdx, or standalone idx.
        if (src is null) return null;
        var ordered = OrderedEntries(src).Select(e => e.Recno).ToList();
        // A SET ORDER … DESCENDING|ASCENDING override reverses the order's own traversal direction.
        if (m.OrderReversed) ordered.Reverse();
        m.Ordered = ordered;
        m.OrderedFor = m.Order;
        m.OrderPos = -1;
        return m.Ordered;
    }

    private void GoTopCore(int area)
    {
        var m = _meta.TryGetValue(area, out var mm) ? mm : (_meta[area] = new AreaMeta());
        var wa = Session.AreaAt(area);
        if (wa is null) { m.RecNo = 0; m.Eof = true; m.Bof = true; m.Cached = null; m.OldVals = null; return; }
        m.Cached = null;
        m.OldVals = null;                 // a record move re-bases the OLDVAL() buffer (per-record buffering).
        var ord = ActiveOrder(area);
        if (ord is not null)
        {
            for (int p = 0; p < ord.Count; p++)
                if (InRange(wa, ord[p]) && Visible(wa, ord[p]))
                { m.OrderPos = p; m.RecNo = ord[p]; m.Eof = false; m.Bof = false; return; }
            m.OrderPos = ord.Count; m.RecNo = wa.Table.RecordCount + 1; m.Eof = true; m.Bof = true; return;
        }
        int rc = EffCount(area, wa);   // GO TOP may land on a buffered append when no on-disk row is visible.
        for (int i = 1; i <= rc; i++)
            if (Visible(wa, i)) { m.RecNo = i; m.Eof = false; m.Bof = false; return; }
        m.RecNo = rc + 1; m.Eof = true; m.Bof = true;
    }

    private void GoBottomCore(int area)
    {
        var m = Meta(area);
        var wa = Session.AreaAt(area);
        if (wa is null) return;
        m.Cached = null;
        m.OldVals = null;                 // a record move re-bases the OLDVAL() buffer (per-record buffering).
        var ord = ActiveOrder(area);
        if (ord is not null)
        {
            for (int p = ord.Count - 1; p >= 0; p--)
                if (InRange(wa, ord[p]) && Visible(wa, ord[p]))
                { m.OrderPos = p; m.RecNo = ord[p]; m.Eof = false; m.Bof = false; return; }
            m.OrderPos = ord.Count; m.RecNo = wa.Table.RecordCount + 1; m.Eof = true; m.Bof = true; return;
        }
        int rc = EffCount(area, wa);   // GO BOTTOM lands on the last buffered appended row when present.
        for (int i = rc; i >= 1; i--)
            if (Visible(wa, i)) { m.RecNo = i; m.Eof = false; m.Bof = false; return; }
        m.RecNo = rc + 1; m.Eof = true; m.Bof = true;
    }

    private void GoRecordCore(int area, int rec)
    {
        var m = Meta(area);
        var wa = Session.AreaAt(area);
        int rc = wa is null ? 0 : EffCount(area, wa);   // buffered appends are addressable (GO n past the disk count).
        m.RecNo = rec;
        m.Eof = rec > rc;
        m.Bof = rec < 1;
        m.Cached = null;
        m.OldVals = null;                 // a record move re-bases the OLDVAL() buffer (per-record buffering).
        // Re-anchor the index position to this physical record (so a following SKIP walks index order).
        var ord = ActiveOrder(area);
        if (ord is not null) m.OrderPos = ord.IndexOf(rec);
    }

    private static bool InRange(VfpSession.WorkArea wa, int rec) => rec >= 1 && rec <= wa.Table.RecordCount;

    private void SkipCore(int area, int count)
    {
        var m = Meta(area);
        var wa = Session.AreaAt(area);
        if (wa is null) return;
        m.Cached = null;
        m.OldVals = null;                 // a record move re-bases the OLDVAL() buffer (per-record buffering).
        if (count == 0) return;

        var ord = ActiveOrder(area);
        if (ord is not null)
        {
            int dirO = count > 0 ? 1 : -1, stepsO = Math.Abs(count);
            int pos = m.OrderPos;
            if (pos < 0) { GoTopCore(area); pos = m.OrderPos; }    // not yet anchored → start at top.
            while (stepsO > 0)
            {
                pos += dirO;
                if (pos >= ord.Count) { m.OrderPos = ord.Count; m.RecNo = wa.Table.RecordCount + 1; m.Eof = true; m.Bof = false; return; }
                if (pos < 0) { m.OrderPos = -1; m.Bof = true; m.Eof = false; GoTopCore(area); m.Bof = true; return; }
                if (InRange(wa, ord[pos]) && Visible(wa, ord[pos])) stepsO--;
            }
            m.OrderPos = pos; m.RecNo = ord[pos]; m.Eof = false; m.Bof = false;
            return;
        }

        int rc = EffCount(area, wa);   // SKIP can walk onto/through buffered appended rows.
        int dir = count > 0 ? 1 : -1, steps = Math.Abs(count), cur = m.RecNo;
        while (steps > 0)
        {
            cur += dir;
            if (cur > rc) { m.RecNo = rc + 1; m.Eof = true; m.Bof = false; return; }
            if (cur < 1) { m.RecNo = 1; m.Bof = true; m.Eof = false; GoTopCore(area); m.Bof = true; return; }
            if (Visible(wa, cur)) steps--;
        }
        m.RecNo = cur; m.Eof = false; m.Bof = false;
    }

    // ── navigation wrappers (microVFP P1 gap #2) — every USER parent move goes through one of these and,
    // after positioning THIS area, repositions any related child areas (recursively, for chained
    // relations). The *Core methods hold the unchanged navigation logic and are used for internal,
    // NON-move calls (lazy Meta creation, a mid-SKIP BOF snap, the reposition itself) so those never
    // re-fire the hook. RepositionChildren early-returns when the area has no relations, so the ~2100
    // relation-free tests pay only a dictionary lookup. ─────────────────────────────────────────────
    private void GoTop(int area) { MaybeAutoCommitRow(area); GoTopCore(area); RepositionChildren(area); }
    private void GoBottom(int area) { MaybeAutoCommitRow(area); GoBottomCore(area); RepositionChildren(area); }
    private void GoRecord(int area, int rec) { MaybeAutoCommitRow(area); GoRecordCore(area, rec); RepositionChildren(area); }

    private void Skip(int area, int count)
    {
        MaybeAutoCommitRow(area);
        SkipCore(area, count);
        ApplyOneToManyBound(area);   // SET SKIP: clamp a one-to-many child to its parent-key group.
        RepositionChildren(area);
    }

    private bool DoSeek(VfpValue key, int area, string? tag)
    {
        MaybeAutoCommitRow(area);
        bool ok = DoSeekCore(key, area, tag);
        RepositionChildren(area);
        return ok;
    }

    /// <summary>ROW buffering (mode 2/3): a USER pointer move implicitly commits the pending edit on the
    /// CURRENT record (TABLEUPDATE of that one row) before leaving it. A no-op for table buffering (4/5) —
    /// those wait for an explicit TABLEUPDATE — and for unbuffered areas. Only the current EXISTING row is
    /// committed (row buffering never defers appends). Internal (non-user) moves go through the *Core
    /// methods and so never trip this.</summary>
    private void MaybeAutoCommitRow(int area)
    {
        if (_inBufferCommit) return;   // internal moves DURING a commit must not re-trigger the auto-commit.
        if (!_meta.TryGetValue(area, out var m) || (m.Buffering != 2 && m.Buffering != 3) || m.Buf is null) return;
        var wa = Session.AreaAt(area);
        if (wa?.Table.SourcePath is not string path) return;
        int rec = m.RecNo, rcTable = wa.Table.RecordCount;
        _inBufferCommit = true;
        try
        {
            if (rec >= 1 && rec <= rcTable && m.Buf.Rows.TryGetValue(rec, out var e))
            {
                // Only DROP the buffered edit once it is actually on disk; a RESTRICT abort keeps it buffered
                // AND surfaces the failure (VFP blocks the move rather than silently losing the edit).
                if (CommitExistingRow(area, wa, path, rec, e)) m.Buf.Rows.Remove(rec);
                else throw new MicroVfpRuntimeException("Update conflict: the pending row was blocked on commit.");
            }
            else if (rec > rcTable)                            // the current record is a buffered APPEND.
            {
                int ai = rec - rcTable - 1;
                if (ai >= 0 && ai < m.Buf.Appends.Count)
                {
                    if (CommitAppend(m.Buf.Appends[ai])) m.Buf.Appends.RemoveAt(ai);
                    else throw new MicroVfpRuntimeException("Insert conflict: the pending append was blocked on commit.");
                }
            }
        }
        finally { _inBufferCommit = false; }
        if (_meta.TryGetValue(area, out var cm) && cm.Buf is { IsEmpty: true }) cm.Buf = null;
    }

    private bool DoSeekCore(VfpValue key, int area, string? tag, bool relationSeek = false)
    {
        var m = Meta(area);
        var wa = Session.AreaAt(area);
        if (wa is null) { m.Found = false; return false; }
        string? identity = tag ?? DefaultOrderIdentity(area);
        if (identity is null) { m.Found = false; return false; }

        using var src = OpenOrderSource(area, identity);
        if (src is null) { m.Found = false; return false; }

        uint? recno = null;
        if (src.CdxTag is { } t)
        {
            // Character keys seek by RAW BYTES (a prefix seek): this lets a SHORT value match a COMPOSITE
            // character tag (e.g. relate on `cust_id`, tag `cust_id+ord_id`) — the P1-gap-#2 prefix quirk —
            // which the value-typed Seek(object) cannot do (a composite expression resolves to
            // KeyType.Unknown, so its Encode returns null). A single-field character key is unaffected.
            // The RELATION reposition seek honours SET EXACT (hackfox quirk 1): under EXACT ON a prefix-only
            // hit on a composite child key is NOT a match (→ child EOF); the SEEK command path keeps its
            // always-prefix behaviour.
            recno = (t.IsCharacterKey && key.Type == VfpType.Character)
                ? t.Seek(Encoding.Latin1.GetBytes(key.AsString).AsSpan(), relationSeek && _ctx.Exact)
                : t.Seek(key.ToClr() ?? string.Empty);
        }
        else if (src.Idx is not null)
        {
            recno = SeekIdx(src, key);
        }

        m.OldVals = null;                 // a record move re-bases the OLDVAL() buffer (per-record buffering).
        if (recno is uint r && r >= 1 && r <= (uint)wa.Table.RecordCount)
        {
            m.RecNo = (int)r; m.Eof = false; m.Bof = false; m.Found = true; m.Cached = null;
            var ord = ActiveOrder(area);
            if (ord is not null) m.OrderPos = ord.IndexOf((int)r);
            return true;
        }

        // Miss: SET NEAR ON leaves the pointer on the record just past where the key would sort
        // (s4g268 — SEEK only, not relation repositioning); NEAR OFF (default) parks at EOF.
        if (_setNear && !relationSeek)
        {
            int nearRec = NearRecord(src, key, area);
            if (nearRec >= 1)
            {
                m.RecNo = nearRec; m.Eof = false; m.Bof = false; m.Found = false; m.Cached = null;
                var ord = ActiveOrder(area);
                if (ord is not null) m.OrderPos = ord.IndexOf(nearRec);
                return false;
            }
        }
        m.RecNo = wa.Table.RecordCount + 1; m.Eof = true; m.Found = false; m.Cached = null;
        return false;
    }

    /// <summary>Seek <paramref name="key"/> in a standalone <c>.idx</c> order by decoding each entry's key
    /// and comparing by VALUE (first match wins); returns the recno, or null when absent.</summary>
    private static uint? SeekIdx(OrderSource src, VfpValue key)
    {
        if (src.Idx is null) return null;
        foreach (var e in src.Idx.EnumerateEntries())
        {
            var decoded = IndexKey.Decode(e.Key, src.IdxKeyType);
            if (SeekValueMatches(decoded, src.IdxKeyType, key))
                return e.RecordNumber;
        }
        return null;
    }

    private static bool SeekValueMatches(IndexKey decoded, IndexKeyType type, VfpValue key)
    {
        switch (type)
        {
            case IndexKeyType.Character:
                string dk = decoded.AsString ?? string.Empty;
                string want = key.AsString ?? string.Empty;
                return dk.StartsWith(want.TrimEnd(), StringComparison.Ordinal);   // prefix seek.
            case IndexKeyType.Integer:
                return decoded.AsInt32 is int di && di == (int)key.AsNumber;
            case IndexKeyType.Numeric:
                return decoded.AsDouble is double dd && dd == (double)key.AsNumber;
            case IndexKeyType.Date:
            {
                var b = key.ToClr();
                DateOnly? bo = b as DateOnly? ?? (b is DateTime bt ? DateOnly.FromDateTime(bt) : null);
                return decoded.AsDate is { } dv && bo is { } bb && dv == bb;
            }
            default:
                return false;
        }
    }

    /// <summary>SET NEAR: the record the pointer parks on after a failed SEEK — the first entry that sorts
    /// AFTER the seek key in the CONTROLLING order that is in-range and visible; −1 when past the end.</summary>
    private int NearRecord(OrderSource src, VfpValue key, int area)
    {
        var wa = Session.AreaAt(area);
        if (wa is null) return -1;
        bool reversed = _meta.TryGetValue(area, out var m) && m.OrderReversed;
        bool descending = src.Descending;
        if (reversed) descending = !descending;

        // Walk the ACTUAL (possibly-overridden) traversal order — the SAME sequence ActiveOrder builds:
        // the order's stored order, reversed when a SET ORDER … ASCENDING|DESCENDING override flipped it.
        // Scanning the un-reversed base with the flipped comparison would return the FARTHEST match, not
        // the nearest (s4g268: NEAR parks on the record JUST past where the key would sort).
        var entries = OrderedEntries(src);
        if (reversed) entries = entries.Reverse();

        foreach (var (keyBytes, recno) in entries)
        {
            int cmp = CompareStoredKeyToSeek(keyBytes, key, src);
            bool after = descending ? cmp < 0 : cmp > 0;   // sorts strictly after the seek key.
            if (after && recno >= 1 && recno <= wa.Table.RecordCount && Visible(wa, recno))
                return recno;
        }
        return -1;
    }

    /// <summary>Three-way compare a STORED key (index bytes) against the SEEK value: &gt;0 when the stored
    /// key sorts after the seek value, &lt;0 before, 0 equal — in the key's own encoding.</summary>
    private static int CompareStoredKeyToSeek(byte[] keyBytes, VfpValue key, OrderSource src)
    {
        if (src.Idx is not null)
        {
            var decoded = IndexKey.Decode(keyBytes, src.IdxKeyType);
            return CompareDecodedToValue(decoded, src.IdxKeyType, key);
        }
        // CDX: compare the raw stored bytes against the seek needle (unsigned byte order).
        var t = src.CdxTag!;
        byte[] needle = t.IsCharacterKey && key.Type == VfpType.Character
            ? Encoding.Latin1.GetBytes(key.AsString)
            : (IndexKey.Decode(keyBytes, t.KeyType).Value is null ? Array.Empty<byte>() : keyBytes); // fallback
        if (t.IsCharacterKey && key.Type == VfpType.Character)
        {
            int n = Math.Min(keyBytes.Length, needle.Length);
            for (int i = 0; i < n; i++) { int d = keyBytes[i] - needle[i]; if (d != 0) return d; }
            return keyBytes.Length - needle.Length;
        }
        return CompareDecodedToValue(IndexKey.Decode(keyBytes, t.KeyType), t.KeyType, key);
    }

    private static int CompareDecodedToValue(IndexKey decoded, IndexKeyType type, VfpValue key) => type switch
    {
        IndexKeyType.Integer => (decoded.AsInt32 ?? 0).CompareTo((int)key.AsNumber),
        IndexKeyType.Numeric => (decoded.AsDouble ?? 0d).CompareTo((double)key.AsNumber),
        IndexKeyType.Character => string.CompareOrdinal(decoded.AsString ?? string.Empty, key.AsString ?? string.Empty),
        IndexKeyType.Date => (decoded.AsDate ?? default).CompareTo(
            key.ToClr() is DateOnly d ? d : (key.ToClr() is DateTime dt ? DateOnly.FromDateTime(dt) : default)),
        _ => 0,
    };

    // ─────────────────────────── SET RELATION / SET SKIP (microVFP P1 gap #2) ───────────────────────────
    //
    // SET RELATION links the CURRENT (parent) area to child area(s). Each parent move auto-SEEKs the
    // relation key on the child's ACTIVE order (miss ⇒ child at EOF); a numeric key into a child with NO
    // controlling order does an implicit GOTO (record-number relation). Relations chain (a child may itself
    // be a parent) and there can be several per parent. SET SKIP marks a related child one-to-many: a SKIP
    // in that child then stays within the current parent-key group and goes EOF past its last matching row.

    private void ExecSetRelation(SetRelationStmt sr)
    {
        int area = Session.CurrentArea;
        if (area <= 0) return;
        var m = Meta(area);
        // Without ADDITIVE a new SET RELATION replaces the parent's prior relations; ADDITIVE appends.
        m.Relations = sr.Additive ? (m.Relations ?? new List<Relation>()) : new List<Relation>();
        foreach (var t in sr.Targets)
        {
            int child = ResolveAreaRef(t.Into);
            if (child <= 0) continue;
            var cwa = Session.AreaAt(child);
            // VFP9 orders relations MOST-RECENTLY-SET FIRST, for BOTH a single multi-target statement AND
            // cumulative ADDITIVE — so RELATION(1)/TARGET(1)/SET("RELATION") reflect the latest link. Insert
            // at the front (a multi-target statement's targets thus land reversed, matching vfp9.exe).
            m.Relations.Insert(0, new Relation
            {
                Key = t.Key,
                KeyText = t.Key.Text.Trim(),
                ChildArea = child,
                ChildAlias = cwa?.Alias ?? NameOf(t.Into),
            });
        }
        RepositionChildren(area);   // VFP positions the child(ren) immediately when the relation is set.
    }

    private void ExecSetRelationOff(SetRelationOffStmt sro)
    {
        int area = Session.CurrentArea;
        if (!_meta.TryGetValue(area, out var m) || m.Relations is null) return;
        if (sro.Into is null) { m.Relations = null; return; }   // SET RELATION OFF (no target) ⇒ clear all.
        int child = ResolveAreaRef(sro.Into);
        m.Relations.RemoveAll(r => r.ChildArea == child);
    }

    private void ExecSetSkip(SetSkipStmt ss)
    {
        int area = Session.CurrentArea;
        if (!_meta.TryGetValue(area, out var m) || m.Relations is null) return;
        if (ss.Aliases.Count == 0)                              // SET SKIP TO (no args) ⇒ clear all marks.
        {
            foreach (var r in m.Relations) r.OneToMany = false;
            return;
        }
        foreach (var nr in ss.Aliases)
        {
            int child = ResolveAreaRef(nr);
            foreach (var r in m.Relations) if (r.ChildArea == child) r.OneToMany = true;
        }
    }

    /// <summary>After a parent move, re-seek every child of <paramref name="parentArea"/> (recursively, so
    /// chained relations propagate). Guarded so the child moves it issues never re-fire the hook, and depth
    /// capped so a cyclic relation terminates. A no-op when the area has no relations.</summary>
    private void RepositionChildren(int parentArea)
    {
        if (_inReposition) return;
        if (!_meta.TryGetValue(parentArea, out var pm) || pm.Relations is null || pm.Relations.Count == 0) return;
        _inReposition = true;
        try { RepositionChildrenCore(parentArea, 0); }
        finally { _inReposition = false; }
    }

    private void RepositionChildrenCore(int parentArea, int depth)
    {
        if (depth > MaxRelationDepth) return;
        if (!_meta.TryGetValue(parentArea, out var pm) || pm.Relations is null) return;
        int prev = Session.CurrentArea;
        try
        {
            foreach (var rel in pm.Relations)
            {
                if (Session.AreaAt(rel.ChildArea) is null) continue;   // child closed → skip.
                Session.SelectArea(parentArea);
                var pmeta = Meta(parentArea);
                int prc = Session.AreaAt(parentArea)?.Table.RecordCount ?? 0;
                bool parentOnRecord = !pmeta.Eof && pmeta.RecNo >= 1 && pmeta.RecNo <= prc;
                if (!parentOnRecord)
                {
                    ForceEof(rel.ChildArea);                           // no parent record ⇒ child at EOF.
                }
                else
                {
                    var key = Eval(rel.Key);                           // evaluated in the PARENT area.
                    // A numeric key into a child with NO controlling order is a record-number relation
                    // (implicit GOTO); otherwise SEEK on the child's active order (a miss lands at EOF).
                    if (ActiveOrder(rel.ChildArea) is null && IsNumeric(key))
                        GoRecordCore(rel.ChildArea, (int)key.AsNumber);
                    else
                        DoSeekCore(key, rel.ChildArea, null, relationSeek: true);
                }
                Session.SelectArea(prev);
                RepositionChildrenCore(rel.ChildArea, depth + 1);      // chain: the child may be a parent.
            }
        }
        finally { Session.SelectArea(prev); }
    }

    /// <summary>Force <paramref name="area"/> to EOF (used when a parent has no current record, so its
    /// child — and transitively the grandchildren — cannot match).</summary>
    private void ForceEof(int area)
    {
        var m = Meta(area);
        var wa = Session.AreaAt(area);
        m.RecNo = (wa?.Table.RecordCount ?? 0) + 1;
        m.Eof = true; m.Bof = false; m.Found = false; m.Cached = null; m.OldVals = null;
    }

    /// <summary>SET SKIP: when <paramref name="childArea"/> is a one-to-many child and a SKIP moved it off
    /// the record group matching the current parent key, clamp it to EOF (it walked past the last matching
    /// child row). No-op during reposition (that legitimately positions the child to its first match).</summary>
    private void ApplyOneToManyBound(int childArea)
    {
        if (_inReposition) return;
        var found = FindOneToManyParent(childArea);
        if (found is null) return;
        var (parentArea, rel) = found.Value;
        var m = Meta(childArea);
        if (m.Eof) return;
        int prev = Session.CurrentArea;
        try
        {
            Session.SelectArea(parentArea);
            var pmeta = Meta(parentArea);
            int prc = Session.AreaAt(parentArea)?.Table.RecordCount ?? 0;
            if (pmeta.Eof || pmeta.RecNo < 1 || pmeta.RecNo > prc) { ForceEof(childArea); return; }
            var parentKey = Eval(rel.Key);
            var childKey = ChildActiveKey(childArea);
            if (childKey is null || !RelationKeyMatches(parentKey, childKey.Value))
                ForceEof(childArea);
        }
        finally { Session.SelectArea(prev); }
    }

    /// <summary>The (parent area, relation) that marks <paramref name="childArea"/> one-to-many, or null.</summary>
    private (int ParentArea, Relation Rel)? FindOneToManyParent(int childArea)
    {
        foreach (var kv in _meta)
        {
            if (kv.Value.Relations is null) continue;
            foreach (var r in kv.Value.Relations)
                if (r.OneToMany && r.ChildArea == childArea) return (kv.Key, r);
        }
        return null;
    }

    /// <summary>The value of <paramref name="childArea"/>'s active-order key expression at its CURRENT
    /// record (null when the area has no controlling / first tag).</summary>
    private VfpValue? ChildActiveKey(int childArea)
    {
        var wa = Session.AreaAt(childArea);
        if (wa?.Cdx is null) return null;
        var m = Meta(childArea);
        string? tagName = string.IsNullOrEmpty(m.Order) ? wa.Cdx.TagNames.FirstOrDefault() : m.Order;
        if (tagName is null) return null;
        var tag = wa.Cdx.Tag(tagName) ?? wa.Cdx.Tag(tagName.ToUpperInvariant());
        if (tag is null) return null;
        int prev = Session.CurrentArea;
        try { Session.SelectArea(childArea); return EvalText(tag.KeyExpression); }
        finally { Session.SelectArea(prev); }
    }

    /// <summary>Whether a child key still belongs to the parent-key group: the relation SEEK is a PREFIX
    /// seek (child key STARTS WITH the parent key), so the group boundary uses the same prefix rule for
    /// character keys; non-character keys compare by value.</summary>
    private static bool RelationKeyMatches(VfpValue parentKey, VfpValue childKey)
    {
        if (parentKey.Type == VfpType.Character || childKey.Type == VfpType.Character)
            return childKey.AsString.StartsWith(parentKey.AsString.TrimEnd(), StringComparison.Ordinal);
        // Numeric/Integer/Currency/Date keys never populate the string backing, so an AsString comparison
        // was always ""=="" (true) — the one-to-many clamp then never fired. Compare by full typed value.
        return parentKey.Equals(childKey);
    }

    private string RelationSetString(int area)
    {
        if (!_meta.TryGetValue(area, out var m) || m.Relations is null || m.Relations.Count == 0)
            return string.Empty;
        return string.Join(", ", m.Relations.Select(r => $"{r.KeyText} INTO {r.ChildAlias}"));
    }

    private string SkipSetString(int area)
    {
        if (!_meta.TryGetValue(area, out var m) || m.Relations is null) return string.Empty;
        return string.Join(", ", m.Relations.Where(r => r.OneToMany).Select(r => r.ChildAlias));
    }

    private VfpValue FnRelation(VfpValue[] a)
    {
        int n = a.Length > 0 ? (int)a[0].AsNumber : 1;
        int area = a.Length > 1 ? AreaNumber(a[1]) : Session.CurrentArea;
        var rels = _meta.TryGetValue(area, out var m) ? m.Relations : null;
        if (rels is null || n < 1 || n > rels.Count) return VfpValue.Character(string.Empty);
        return VfpValue.Character(rels[n - 1].KeyText);
    }

    private VfpValue FnTarget(VfpValue[] a)
    {
        int n = a.Length > 0 ? (int)a[0].AsNumber : 1;
        int area = a.Length > 1 ? AreaNumber(a[1]) : Session.CurrentArea;
        var rels = _meta.TryGetValue(area, out var m) ? m.Relations : null;
        if (rels is null || n < 1 || n > rels.Count) return VfpValue.Character(string.Empty);
        return VfpValue.Character(rels[n - 1].ChildAlias);
    }

    // ─────────────────────────── name / field resolution ───────────────────────────

    /// <summary>Resolve a name for the expression engine: qualified <c>alias.field</c> → that area's
    /// current record; <c>m.var</c> → memvar; bare → CURRENT area field, else memvar, else #DEFINE.</summary>
    internal object? ResolveName(string name)
    {
        if (name.Equals("_triggerlevel", StringComparison.OrdinalIgnoreCase))
            return (decimal)TriggerLevel;     // P2 RI seam — the trigger gate (see TriggerLevel).

        int dot = name.IndexOf('.');
        if (dot >= 0)
        {
            string head = name[..dot], rest = name[(dot + 1)..];
            if (head.Equals("m", StringComparison.OrdinalIgnoreCase))
                return Memory.Get(rest).ToClr();
            var wa = Session.FindAreaByAlias(head);
            if (wa is not null) return ReadField(wa, rest);
            return null;                                  // object property / DBC!table.field → undefined.
        }

        var cur = Session.AreaAt(Session.CurrentArea);
        if (cur is not null && ColumnIndex(cur.Table, name) >= 0) return ReadField(cur, name);
        // A bare ARRAY name in scalar context = element (1,1)/(1), per VFP.
        if (Memory.FindArray(name) is { } arr) return arr.First.ToClr();
        if (Memory.IsDefined(name)) return Memory.Get(name).ToClr();
        if (_defines.TryGetValue(name, out var d)) return d.ToClr();
        return null;
    }

    // Field read WITH the buffer overlay: a buffered edit to the current record shadows the on-disk value,
    // and a read on a buffered APPENDED row (recno past the physical count) comes from the append buffer.
    // Falls through to the raw on-disk read when the area is unbuffered / the field is unedited.
    private object? ReadField(VfpSession.WorkArea wa, string field)
    {
        if (_meta.TryGetValue(wa.Area, out var mm) && mm.Buffering > 1 && mm.Buf is { } buf)
        {
            int idx = ColumnIndex(wa.Table, field);
            if (idx < 0) return null;
            int rcTable = wa.Table.RecordCount;
            if (mm.RecNo > rcTable)                                // a buffered appended row.
            {
                int ai = mm.RecNo - rcTable - 1;
                if (ai < 0 || ai >= buf.Appends.Count) return null;
                return buf.Appends[ai].Fields.TryGetValue(idx, out var av) ? av : BlankFor(wa.Table.Columns[idx]);
            }
            if (mm.RecNo >= 1 && mm.RecNo <= rcTable && buf.Rows.TryGetValue(mm.RecNo, out var e)
                && e.Fields.TryGetValue(idx, out var bv))
                return bv;                                          // buffered edit shadows the disk value.
        }
        return ReadFieldOnDisk(wa, field);
    }

    // The typed blank an unset appended-row field reads as / is written to disk with: the column type's real
    // EMPTY() value — "" for character, 0 for numerics, .F. for logical, an empty date for D/T — NEVER .NULL.
    // for a non-character column (matching what DbfWriter writes for a column the INSERT omitted, so the live
    // pre-commit view and the committed on-disk row agree).
    private static object? BlankFor(DbfColumn col) => col.Type switch
    {
        'C' or 'V' or 'M' => string.Empty,
        'I' => 0,
        'N' or 'F' or 'B' or 'Y' => 0m,
        'L' => false,
        _ => null,   // D/T → empty date (DbfWriter writes a blank date field); exotic types → null.
    };

    // Field read straight from the on-disk record (ignores the buffer) — the pre-change value OLDVAL is
    // seeded from and the value CURVAL() reports.
    private object? ReadFieldOnDisk(VfpSession.WorkArea wa, string field)
    {
        int idx = ColumnIndex(wa.Table, field);
        if (idx < 0) return null;
        var m = Meta(wa.Area);
        int rc = wa.Table.RecordCount;
        if (m.RecNo < 1 || m.RecNo > rc) return null;

        DbfRecord rec;
        if (m.Cached is { } c && m.CachedRec == m.RecNo) rec = c;
        else
        {
            var got = wa.Table.GetRecord(m.RecNo - 1);
            if (got is not DbfRecord dr) return null;     // deleted / unreadable.
            rec = dr; m.Cached = dr; m.CachedRec = m.RecNo;
        }

        var col = wa.Table.Columns[idx];
        if (col.Type is 'C' or 'V')
        {
            var raw = rec.GetRawField(col);
            return wa.Table.Encoding.GetString(raw).TrimEnd(' ', '\0');   // RIGHT-trim only (see header).
        }
        return rec[idx];
    }

    private static int ColumnIndex(DbfTable t, string name)
    {
        var cols = t.Columns;
        for (int i = 0; i < cols.Count; i++)
            if (string.Equals(cols[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string StripQualifier(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    // ─────────────────────────── host (state) functions ───────────────────────────

    /// <summary>The expression engine's hook (<see cref="IVfpFunctionHost"/>): owns user-procedure calls
    /// + the runtime STATE functions; returns <see langword="false"/> for the engine's scalar built-ins.</summary>
    internal bool HostInvoke(string name, VfpValue[] a, EvaluationContext ctx, out VfpValue r)
    {
        // An array-element reference `arr(i)` / `arr(i,j)` arrives here as a "call" whose name IS the
        // array variable. A visible array binding takes precedence (VFP resolves the subscript, not a UDF).
        if (Memory.FindArray(name) is { } arr)
        {
            int s1 = a.Length > 0 ? (int)a[0].AsNumber : 1;
            int? s2 = a.Length > 1 ? (int)a[1].AsNumber : (int?)null;
            r = arr.Get(s1, s2);
            return true;
        }

        if (_procs.TryGetValue(name, out var proc))
        {
            r = CallProc(proc, a, a.Length, null);          // user function ⇒ BY VALUE.
            return true;
        }

        switch (name)
        {
            case "SELECT": r = FnSelect(a); return true;
            case "USED": r = VfpValue.Logical(a.Length > 0 && Session.FindAreaByAlias(a[0].AsString) is not null); return true;
            case "ALIAS": r = FnAlias(a); return true;
            case "DBF": r = VfpValue.Character(AreaArg(a, 0)?.Table.SourcePath ?? string.Empty); return true;
            case "DBC": r = VfpValue.Character(string.Empty); return true;
            case "RECNO": r = VfpValue.Integer(FnRecno(a)); return true;
            case "RECCOUNT": { var rcw = AreaArg(a, 0); r = VfpValue.Integer(rcw is null ? 0 : EffCount(rcw.Area, rcw)); return true; }
            case "EOF": r = VfpValue.Logical(MetaArg(a, 0)?.Eof ?? true); return true;
            case "BOF": r = VfpValue.Logical(MetaArg(a, 0)?.Bof ?? true); return true;
            case "FOUND": r = VfpValue.Logical(MetaArg(a, 0)?.Found ?? false); return true;
            case "DELETED": r = VfpValue.Logical(FnDeleted(a)); return true;
            case "SEEK": r = VfpValue.Logical(FnSeek(a)); return true;
            case "PCOUNT": case "PARAMETERS": r = VfpValue.Integer(CurrentCall.PassedCount); return true;
            case "PROGRAM": r = FnProgram(a); return true;
            case "TYPE": r = VfpValue.Character(TypeOf(a.Length > 0 ? a[0].AsString : string.Empty)); return true;
            case "EVALUATE": case "EVAL": r = a.Length > 0 ? EvalText(a[0].AsString) : VfpValue.Null; return true;
            case "SET": r = FnSet(a); return true;
            case "RELATION": r = FnRelation(a); return true;
            case "TARGET": r = FnTarget(a); return true;
            case "SYS": r = FnSys(a); return true;
            // ── index introspection (microVFP INDEX/ORDER MODEL) ──
            case "TAGCOUNT": r = VfpValue.Integer(FnTagCount(a)); return true;
            case "TAG": r = VfpValue.Character(FnTag(a)); return true;
            case "TAGNO": r = VfpValue.Integer(FnTagNo(a)); return true;
            case "CDX": case "MDX": r = VfpValue.Character(FnCdx(a)); return true;
            case "NDX": r = VfpValue.Character(FnNdx(a)); return true;
            case "ORDER": r = VfpValue.Character(FnOrder(a)); return true;
            // KEY()/FOR() return the key/filter expression ALL-CAPS (hackfox s4g266 — same quirk SYS(14)
            // honours), so KEY(n) and SYS(14,n) stay internally consistent for any lowercase-typed expr.
            case "KEY": r = VfpValue.Character(ResolveSlot(a)?.KeyExpr.ToUpperInvariant() ?? string.Empty); return true;
            case "FOR": r = VfpValue.Character(ResolveSlot(a)?.ForExpr.ToUpperInvariant() ?? string.Empty); return true;
            case "UNIQUE": r = VfpValue.Logical(ResolveSlot(a)?.Unique ?? false); return true;
            case "DESCENDING": r = VfpValue.Logical(ResolveSlot(a)?.Descending ?? false); return true;
            case "CANDIDATE": r = VfpValue.Logical(false); return true;   // no DBC index catalog → never a candidate.
            case "PRIMARY": r = VfpValue.Logical(false); return true;     // primary keys are a DBC-bound concept only.
            case "IDXCOLLATE": r = VfpValue.Character(ResolveSlot(a)?.Collation ?? string.Empty); return true;
            case "ATAGINFO": r = VfpValue.Integer(FnATagInfo(a)); return true;
            case "LOOKUP": r = FnLookup(a); return true;
            case "SECONDS": r = VfpValue.Number(DateTime.Now.TimeOfDay.TotalSeconds); return true;
            // Single-user model: there is no lock contention, so a lock is always granted (.T.). The
            // SET REPROCESS TO 0 + ON-ERROR fail-fast branch (Runtime.LockFailFast) governs RETRY
            // behaviour under contention, which this in-process interpreter never sees.
            case "RLOCK": case "LOCK": case "FLOCK": r = VfpValue.Logical(true); return true;
            case "ISRLOCKED": case "ISFLOCKED": r = VfpValue.Logical(false); return true;
            case "ALLT": r = VfpValue.Character((a.Length > 0 ? a[0].AsString : string.Empty).Trim(' ')); return true;
            case "OCCURS": r = VfpValue.Integer(FnOccurs(a)); return true;
            case "ATC": r = VfpValue.Integer(FnAtc(a)); return true;
            case "STRTRAN": r = VfpValue.Character(FnStrtran(a)); return true;
            case "ALEN": r = VfpValue.Integer(FnAlen(a)); return true;
            case "AERROR": r = FnAerror(a); return true;
            case "ERROR": r = VfpValue.Integer(_errNo); return true;
            case "MESSAGE": r = VfpValue.Character(a.Length > 0 && IsNumeric(a[0]) && (int)a[0].AsNumber == 1 ? string.Empty : _errMsg); return true;
            case "LINENO": r = VfpValue.Integer(_errLine); return true;
            case "ON": r = VfpValue.Character(string.Equals(a.Length > 0 ? a[0].AsString : string.Empty, "ERROR", StringComparison.OrdinalIgnoreCase) ? (Runtime.OnError ?? string.Empty) : string.Empty); return true;
            case "ISDIGIT": r = VfpValue.Logical(a.Length > 0 && a[0].AsString.Length > 0 && char.IsDigit(a[0].AsString[0])); return true;
            case "ISALPHA": r = VfpValue.Logical(a.Length > 0 && a[0].AsString.Length > 0 && char.IsLetter(a[0].AsString[0])); return true;
            case "TXNLEVEL": r = VfpValue.Integer(_txn.Count); return true;
            case "CURSORGETPROP": r = FnCursorGetProp(a); return true;
            case "CURSORSETPROP": r = FnCursorSetProp(a); return true;
            case "TABLEUPDATE": r = FnTableUpdate(a); return true;
            case "TABLEREVERT": r = FnTableRevert(a); return true;
            case "GETFLDSTATE": r = FnGetFldState(a); return true;
            case "SETFLDSTATE": r = VfpValue.Logical(true); return true;      // s4g395: practically inert for real tables — accepted, no-op.
            case "OLDVAL": r = FnOldVal(a); return true;
            case "CURVAL": r = FnCurVal(a); return true;
            case "MESSAGEBOX": r = VfpValue.Integer(6); return true;           // IDYES (never reached in targets).
            case "COCREATEGUID": r = FnCoCreateGuid(); return true;

            // ─── P2 scalar/string batch (MICROVFP_EXTENSIONS_BACKLOG C.18) — STUBS, RED until implemented ───
            case "VARTYPE": r = FnVartype(a); return true;
            case "TRANSFORM": r = FnTransform(a); return true;
            case "PROPER": r = FnProper(a); return true;
            case "ISLOWER": r = VfpValue.Logical(FnIsLower(a)); return true;
            case "ISUPPER": r = VfpValue.Logical(FnIsUpper(a)); return true;
            case "STREXTRACT": r = VfpValue.Character(FnStrExtract(a)); return true;
            case "GETWORDCOUNT": r = VfpValue.Integer(FnGetWordCount(a)); return true;
            case "GETWORDNUM": r = VfpValue.Character(FnGetWordNum(a)); return true;
            case "SOUNDEX": r = VfpValue.Character(FnSoundex(a)); return true;
            case "DIFFERENCE": r = VfpValue.Integer(FnDifference(a)); return true;
            case "STRCONV": r = VfpValue.Character(FnStrConv(a)); return true;
            case "CPCONVERT": r = VfpValue.Character(FnCpConvert(a)); return true;
            case "CPCURRENT": r = VfpValue.Integer(FnCpCurrent(a)); return true;
            case "CPDBF": r = VfpValue.Integer(FnCpDbf(a)); return true;
            case "TEXTMERGE": r = VfpValue.Character(FnTextMerge(a)); return true;
            case "ALINES": r = VfpValue.Integer(FnAlines(a)); return true;

            // ─── P2 array batch (MICROVFP_EXTENSIONS_BACKLOG C.1/C.7) — STUBS, RED until implemented ───
            case "ACOPY": r = VfpValue.Integer(FnACopy(a)); return true;
            case "ADEL": r = VfpValue.Integer(FnADel(a)); return true;
            case "AINS": r = VfpValue.Integer(FnAIns(a)); return true;
            case "AELEMENT": r = VfpValue.Integer(FnAElement(a)); return true;
            case "ASUBSCRIPT": r = VfpValue.Integer(FnASubscript(a)); return true;
            case "AFIELDS": r = VfpValue.Integer(FnAFields(a)); return true;
            case "ASORT": r = VfpValue.Integer(FnASort(a)); return true;
            case "ADATABASES": r = VfpValue.Integer(FnADatabases(a)); return true;
            case "AUSED": r = VfpValue.Integer(FnAUsed(a)); return true;
            case "ASESSIONS": r = VfpValue.Integer(FnASessions(a)); return true;

            default: r = VfpValue.Null; return false;
        }
    }

    // PROGRAM([n]): no arg / n==0 ⇒ current proc name; n<0 ⇒ stack depth; n>0 ⇒ the program
    // n levels up the call stack ("" when out of range). _callStack[0] is the "(main)" frame, so the
    // meaningful depth (and the highest valid n) is _callStack.Count-1. The rierror RI loops
    // (`do while !empty(program(lnXX))`) walk this until the name is empty ⇒ they now terminate.
    private VfpValue FnProgram(VfpValue[] a)
    {
        if (a.Length == 0 || !IsNumeric(a[0]))
            return VfpValue.Character(CurrentCall.Name);
        int n = (int)a[0].AsNumber;
        if (n == 0) return VfpValue.Character(CurrentCall.Name);
        if (n < 0) return VfpValue.Integer(_callStack.Count - 1);
        int idx = _callStack.Count - 1 - n;
        return VfpValue.Character(idx >= 0 && idx < _callStack.Count ? _callStack[idx].Name : string.Empty);
    }

    private VfpValue FnSelect(VfpValue[] a)
    {
        if (a.Length == 0) return VfpValue.Integer(Session.CurrentArea);
        var v = a[0];
        if (v.Type == VfpType.Character)
            return VfpValue.Integer(Session.FindAreaByAlias(v.AsString)?.Area ?? 0);
        int n = (int)v.AsNumber;
        return n switch
        {
            0 => VfpValue.Integer(Session.CurrentArea),
            1 => VfpValue.Integer(Session.HighestUnusedAreaNumber()),
            _ => VfpValue.Integer(Session.CurrentArea),
        };
    }

    private VfpValue FnAlias(VfpValue[] a)
    {
        VfpSession.WorkArea? wa = a.Length == 0 ? Session.AreaAt(Session.CurrentArea) : AreaArg(a, 0);
        return VfpValue.Character(wa?.Alias ?? string.Empty);
    }

    private int FnRecno(VfpValue[] a)
    {
        int area = a.Length == 0 ? Session.CurrentArea : AreaNumber(a[0]);
        var wa = Session.AreaAt(area);
        if (wa is null) return 0;
        var m = Meta(area);
        return m.Eof ? EffCount(area, wa) + 1 : m.RecNo;
    }

    private bool FnDeleted(VfpValue[] a)
    {
        int area = a.Length == 0 ? Session.CurrentArea : AreaNumber(a[0]);
        var wa = Session.AreaAt(area);
        if (wa is null) return false;
        var m = Meta(area);
        int rcTable = wa.Table.RecordCount;
        if (m.Buf is { } buf)
        {
            if (m.RecNo > rcTable)                              // buffered appended row.
            {
                int ai = m.RecNo - rcTable - 1;
                if (ai >= 0 && ai < buf.Appends.Count) return buf.Appends[ai].Deleted;
                return false;
            }
            if (m.RecNo >= 1 && m.RecNo <= rcTable && buf.Rows.TryGetValue(m.RecNo, out var e) && e.DeletedOverride is bool d)
                return d;                                       // buffered delete/recall shadows the on-disk flag.
        }
        return m.RecNo >= 1 && m.RecNo <= rcTable && wa.Table.IsRecordDeleted(m.RecNo - 1);
    }

    private bool FnSeek(VfpValue[] a)
    {
        if (a.Length == 0) return false;
        int area = a.Length > 1 ? AreaNumber(a[1]) : Session.CurrentArea;
        string? tag = a.Length > 2 ? a[2].AsString : null;
        return DoSeek(a[0], area, tag);
    }

    private VfpValue FnSet(VfpValue[] a)
    {
        if (a.Length == 0) return VfpValue.Character(string.Empty);
        return a[0].AsString.ToUpperInvariant() switch
        {
            "REPROCESS" => VfpValue.Character(Runtime.Reprocess == -2 ? "AUTOMATIC" : Runtime.Reprocess.ToString(CultureInfo.InvariantCulture)),
            "EXACT" => VfpValue.Character(_ctx.Exact ? "ON" : "OFF"),
            "DELETED" => VfpValue.Character(_ctx.Deleted ? "ON" : "OFF"),
            "ANSI" => VfpValue.Character(_ctx.Ansi ? "ON" : "OFF"),
            "COLLATE" => VfpValue.Character(_ctx.Collation?.Name ?? "MACHINE"),
            "UNIQUE" => VfpValue.Character(Runtime.Unique ? "ON" : "OFF"),
            "NEAR" => VfpValue.Character(_setNear ? "ON" : "OFF"),
            "RELATION" => VfpValue.Character(RelationSetString(Session.CurrentArea)),  // reproduces the SET RELATION args.
            "SKIP" => VfpValue.Character(SkipSetString(Session.CurrentArea)),          // comma-list of 1:n aliases.
            "DATASESSION" => VfpValue.Number(1m),   // NUMERIC (verified vs vfp9.exe) — single public session.
            "TALK" => VfpValue.Character("OFF"),
            "COMPATIBLE" => VfpValue.Character("OFF"),
            _ => VfpValue.Character(string.Empty),
        };
    }

    private VfpValue FnSys(VfpValue[] a)
    {
        int n = a.Length > 0 ? (int)a[0].AsNumber : 0;
        switch (n)
        {
            // SYS(0) = "<machine> # <station/user>" (network-dependent — see MICROVFP_SEMANTICS.md Nachtrag:
            // "netzabhängig, unzuverlässig"). VERIFIED against vfp9.exe on this box: it returns
            // "<MachineName> # <UserName>" (the part after "#" is the logged-on network user, NOT a numeric
            // station id). Matching that exactly is what lets createId's SYS(2007) workstation/user checksums
            // equal VFP9's. Stable + non-crashing: both halves come from the OS identity.
            case 0: return VfpValue.Character($"{Environment.MachineName} # {Environment.UserName}");
            case 1: return VfpValue.Character(JulianDay(DateTime.Today).ToString(CultureInfo.InvariantCulture));
            // SYS(10, nJulianDay) — Julian-day number → date string (inverse of SYS(11)). STUB, RED until implemented.
            case 10: return FnSys10(a);
            // SYS(14, nIndexNumber [, area]) — the KEY expression of the nth open index, ALL-CAPS (s4g266).
            // Unlike KEY(), SYS(14) REQUIRES the index number; an out-of-range number yields "" (no error).
            case 14:
            {
                if (a.Length < 2) return VfpValue.Character(string.Empty);
                var slot = ResolveSlot(a.Skip(1).ToArray());
                return VfpValue.Character(slot?.KeyExpr.ToUpperInvariant() ?? string.Empty);
            }
            case 2007: return VfpValue.Character(Crc16Ccitt(a.Length > 1 ? a[1].AsString : string.Empty).ToString(CultureInfo.InvariantCulture));
            case 2015: return VfpValue.Character("_" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant());
            default: return VfpValue.Character(string.Empty);
        }
    }

    // ─────────────────────────── index-introspection functions ───────────────────────────

    /// <summary>Parse the shared <c>[cIndexFile,] nIndexNumber [, cAlias|nWorkArea]</c> argument shape and
    /// resolve the addressed <see cref="IndexSlot"/> from the area's open set — null when the number is
    /// absent (except a master-tag fallback), out of range, or no index is open.</summary>
    private IndexSlot? ResolveSlot(VfpValue[] a)
    {
        int ai = 0, area = Session.CurrentArea;
        string? file = null;
        int? n = null;
        if (ai < a.Length && a[ai].Type == VfpType.Character) { file = a[ai].AsString; ai++; }
        if (ai < a.Length && IsNumeric(a[ai])) { n = (int)a[ai].AsNumber; ai++; }
        if (ai < a.Length) area = AreaNumber(a[ai]);

        var inv = FilterInventory(IndexInventory(area), file);
        if (n is null)   // no number ⇒ the master (controlling) tag.
        {
            string? ord = Meta(area).Order;
            return ord is null ? null : inv.FirstOrDefault(s => string.Equals(s.Name, ord, StringComparison.OrdinalIgnoreCase));
        }
        return n >= 1 && n <= inv.Count ? inv[n.Value - 1] : null;
    }

    /// <summary>Narrow an inventory to a single named index file (by file name, extension optional); the
    /// whole set when <paramref name="file"/> is null/empty.</summary>
    private static List<IndexSlot> FilterInventory(List<IndexSlot> inv, string? file)
    {
        if (string.IsNullOrEmpty(file)) return inv;
        string want = Path.GetFileNameWithoutExtension(file);
        return inv.Where(s => string.Equals(Path.GetFileNameWithoutExtension(s.FilePath), want, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private int FnTagCount(VfpValue[] a)
    {
        int area = Session.CurrentArea;
        string? file = null;
        if (a.Length >= 1) { if (a[0].Type == VfpType.Character) file = a[0].AsString; else area = AreaNumber(a[0]); }
        if (a.Length >= 2) area = AreaNumber(a[1]);
        return FilterInventory(IndexInventory(area), file).Count;
    }

    private string FnTag(VfpValue[] a)
    {
        int ai = 0, area = Session.CurrentArea;
        string? file = null;
        int? n = null;
        if (ai < a.Length && a[ai].Type == VfpType.Character) { file = a[ai].AsString; ai++; }
        if (ai < a.Length && IsNumeric(a[ai])) { n = (int)a[ai].AsNumber; ai++; }
        if (ai < a.Length) area = AreaNumber(a[ai]);
        var inv = FilterInventory(IndexInventory(area), file);
        if (n is null) return Meta(area).Order ?? string.Empty;   // master tag name.
        return n >= 1 && n <= inv.Count ? inv[n.Value - 1].Name : string.Empty;
    }

    private int FnTagNo(VfpValue[] a)
    {
        int ai = 0, area = Session.CurrentArea;
        string? tagName = null, file = null;
        if (ai < a.Length && a[ai].Type == VfpType.Character) { tagName = a[ai].AsString; ai++; }
        if (ai < a.Length && a[ai].Type == VfpType.Character) { file = a[ai].AsString; ai++; }
        if (ai < a.Length) area = AreaNumber(a[ai]);
        var inv = FilterInventory(IndexInventory(area), file);
        string? target = tagName ?? Meta(area).Order;   // no name ⇒ the controlling order.
        if (string.IsNullOrEmpty(target)) return 0;
        for (int i = 0; i < inv.Count; i++)
            if (string.Equals(inv[i].Name, target, StringComparison.OrdinalIgnoreCase)) return i + 1;
        return 0;
    }

    private string FnCdx(VfpValue[] a)
    {
        if (a.Length == 0 || !IsNumeric(a[0])) return string.Empty;
        int n = (int)a[0].AsNumber;
        int area = a.Length > 1 ? AreaNumber(a[1]) : Session.CurrentArea;
        var files = FileInventory(area);
        return n >= 1 && n <= files.Count ? files[n - 1] : string.Empty;
    }

    private string FnNdx(VfpValue[] a)
    {
        if (a.Length == 0 || !IsNumeric(a[0])) return string.Empty;
        int n = (int)a[0].AsNumber;
        int area = a.Length > 1 ? AreaNumber(a[1]) : Session.CurrentArea;
        var idxFiles = IdxFileInventory(area);
        return n >= 1 && n <= idxFiles.Count ? idxFiles[n - 1] : string.Empty;
    }

    private string FnOrder(VfpValue[] a)
    {
        int area = a.Length > 0 ? AreaNumber(a[0]) : Session.CurrentArea;
        return Meta(area).Order ?? string.Empty;
    }

    /// <summary>Open index FILES of an area for CDX()/MDX(): the structural <c>.cdx</c> (index 1, if any),
    /// then the additional <c>.cdx</c>/<c>.idx</c> in open order.</summary>
    private List<string> FileInventory(int area)
    {
        var files = new List<string>();
        var wa = Session.AreaAt(area);
        if (wa?.Cdx?.SourcePath is { } sp) files.Add(Path.GetFullPath(sp));
        if (_meta.TryGetValue(area, out var m) && m.ExtraIndexes is { } ex)
            files.AddRange(ex.Select(Path.GetFullPath));
        return files;
    }

    /// <summary>Open standalone <c>.idx</c> files of an area (in open order) — the NDX() domain.</summary>
    private List<string> IdxFileInventory(int area)
        => _meta.TryGetValue(area, out var m) && m.ExtraIndexes is { } ex
            ? ex.Where(IsIdxPath).Select(Path.GetFullPath).ToList()
            : new List<string>();

    /// <summary>ATAGINFO(ArrayName [, cTagFile [, area]]) — fill a (n×6) array with one row per open tag
    /// ([1] name, [2] type, [3] key, [4] filter, [5] direction, [6] collation) and return the tag count.</summary>
    private int FnATagInfo(VfpValue[] a)
    {
        if (a.Length == 0) return 0;
        string arrName = a[0].AsString;
        int ai = 1, area = Session.CurrentArea;
        string? file = null;
        if (ai < a.Length && a[ai].Type == VfpType.Character) { file = a[ai].AsString; ai++; }
        if (ai < a.Length) area = AreaNumber(a[ai]);

        var inv = FilterInventory(IndexInventory(area), file);
        if (inv.Count == 0) return 0;
        var arr = Memory.RedimOrCreateArray(arrName, inv.Count, 6);
        for (int i = 0; i < inv.Count; i++)
        {
            var s = inv[i];
            arr.Set(i + 1, 1, VfpValue.Character(s.Name));
            arr.Set(i + 1, 2, VfpValue.Character(s.Unique ? "UNIQUE" : "REGULAR"));
            arr.Set(i + 1, 3, VfpValue.Character(s.KeyExpr));
            arr.Set(i + 1, 4, VfpValue.Character(s.ForExpr));
            arr.Set(i + 1, 5, VfpValue.Character(s.Descending ? "DESCENDING" : "ASCENDING"));
            arr.Set(i + 1, 6, VfpValue.Character(s.Collation));
        }
        return inv.Count;
    }

    /// <summary>LOOKUP(rReturn, eSearch, rSearched [, cTag]) — seek <paramref name="a"/>[1] (via the tag
    /// when given, else a sequential scan on rSearched) and return the rReturn field value at the hit; a
    /// miss parks the pointer (per SET NEAR) / at EOF and returns a blank. The field-name arguments arrive
    /// as quoted names (MicroVfpExprRewrite).</summary>
    private VfpValue FnLookup(VfpValue[] a)
    {
        if (a.Length < 3) return VfpValue.Logical(false);
        string returnField = a[0].AsString;
        VfpValue searchVal = a[1];
        string searchField = a[2].AsString;
        string? tag = a.Length > 3 ? a[3].AsString : null;
        int area = Session.CurrentArea;

        bool found = !string.IsNullOrEmpty(tag)
            ? DoSeek(searchVal, area, tag)
            : LookupSequential(area, searchField, searchVal);

        if (found) return EvalText(returnField);
        return VfpValue.Character(string.Empty);   // miss ⇒ blank (FOUND() stays .F.).
    }

    /// <summary>Sequential LOCATE for <c>field = value</c> from the top of the work area; leaves the
    /// pointer on the first match (or EOF).</summary>
    private bool LookupSequential(int area, string field, VfpValue value)
    {
        GoTop(area);
        while (!Meta(area).Eof)
        {
            var cur = EvalText(field);
            if (ValuesLooseEqual(cur, value)) return true;
            Skip(area, 1);
        }
        return false;
    }

    private static bool ValuesLooseEqual(VfpValue x, VfpValue y)
    {
        if (x.Type == VfpType.Character || y.Type == VfpType.Character)
            return string.Equals(x.AsString?.TrimEnd(), y.AsString?.TrimEnd(), StringComparison.Ordinal);
        return x.AsNumber == y.AsNumber;
    }

    // ALEN(arr[,n]) — total elements (no n / n<=0), rows (n=1) or columns (n=2; 0 for 1-D). The array name
    // arrives as a string (MicroVfpExprRewrite quotes the first arg); an unknown array returns 0.
    private int FnAlen(VfpValue[] a)
    {
        if (a.Length == 0) return 0;
        var arr = Memory.FindArray(a[0].AsString);
        if (arr is null) return 0;
        int dim = a.Length > 1 && IsNumeric(a[1]) ? (int)a[1].AsNumber : 0;
        return arr.ALen(dim);
    }

    // AERROR(arr) — the full 7-column contract (MICROVFP_SEMANTICS.md Nachtrag). (Re)dimensions the named
    // array to (rows,7), fills it from the RETAINED last error (so it works both inside the ON ERROR
    // handler AND after it returns, when the live ERROR()/MESSAGE() are cleared), and returns the row
    // count. No error yet ⇒ returns 0 and leaves the array untouched. The array name arrives as a string
    // (MicroVfpExprRewrite quotes the first arg). ODBC/OLE multi-row layouts are out of the RI corpus.
    private VfpValue FnAerror(VfpValue[] a)
    {
        if (a.Length == 0) return VfpValue.Integer(0);
        string name = a[0].AsString;
        if (string.IsNullOrEmpty(name) || Runtime.LastErrorNumber == 0) return VfpValue.Integer(0);

        var arr = Memory.RedimOrCreateArray(name, 1, 7);
        arr.Set(1, 1, VfpValue.Integer(Runtime.LastErrorNumber));                                   // [1] ERROR()
        arr.Set(1, 2, VfpValue.Character(Runtime.LastErrorMessage ?? string.Empty));                 // [2] MESSAGE()
        arr.Set(1, 3, Runtime.LastErrorDetail is { } d ? VfpValue.Character(d) : VfpValue.Null);     // [3] detail/SYS(2018)
        arr.Set(1, 4, Runtime.LastErrorArea > 0 ? VfpValue.Integer(Runtime.LastErrorArea) : VfpValue.Null); // [4] work area
        // [5] = .NULL., EXCEPT a trigger failure (1539 → 1 insert/2 update/3 delete) or a field-rule error
        // (→ the violating field number).
        arr.Set(1, 5, Runtime.LastErrorTrigger != 0 ? VfpValue.Integer(Runtime.LastErrorTrigger)
                    : Runtime.LastErrorField != 0 ? VfpValue.Integer(Runtime.LastErrorField)
                    : VfpValue.Null);
        arr.Set(1, 6, VfpValue.Null);                                                                // [6] .NULL.
        arr.Set(1, 7, VfpValue.Null);                                                                // [7] .NULL.
        return VfpValue.Integer(1);
    }

    // ─────────────────────────── P2 array batch (MICROVFP_EXTENSIONS_BACKLOG C.1/C.7) ───────────────────────────
    // Authoritative semantics: backlog C.1/C.7 + hackfox s4g210/211/213/292/666, verified byte-for-byte
    // against the VFP9 runtime. VFP arrays are 1-BASED, ROW-MAJOR; a "2-D" array is rows×cols with a
    // parallel LINEAR (row-major) element view. Array NAMES arrive as strings (MicroVfpExprRewrite quotes
    // the reference argument(s)); an unknown array is a silent 0 (except ASUBSCRIPT, which errors — s4g213).

    /// <summary>ACOPY(aSource, aDest [, nStart [, nCount [, nDestStart]]]) — LINEAR (row-major) copy. When
    /// aDest does not yet exist it is created MATCHING aSource's dimensions (hackfox s4g210 quirk), even for
    /// a partial-range copy. Returns the number of elements copied.</summary>
    private int FnACopy(VfpValue[] a)
    {
        if (a.Length < 2) return 0;
        var src = Memory.FindArray(a[0].AsString);
        if (src is null) return 0;
        int srcLen = src.Length;

        int start = a.Length > 2 && IsNumeric(a[2]) ? (int)a[2].AsNumber : 1;
        if (start < 1) start = 1;
        int count = a.Length > 3 && IsNumeric(a[3]) ? (int)a[3].AsNumber : srcLen - start + 1;
        if (count < 0) count = srcLen - start + 1;
        int destStart = a.Length > 4 && IsNumeric(a[4]) ? (int)a[4].AsNumber : 1;
        if (destStart < 1) destStart = 1;

        string destName = a[1].AsString;
        var dest = Memory.FindArray(destName);
        bool destPreexisted = dest is not null;
        // The auto-create-matching-source-dims quirk (s4g210) applies ONLY when aDest does not yet exist.
        dest ??= Memory.RedimOrCreateArray(destName, src.Rows, src.Cols);

        int copied = 0;
        for (int k = 0; k < count; k++)
        {
            int sIdx = start + k, dIdx = destStart + k;
            if (sIdx < 1 || sIdx > srcLen) break;
            if (dIdx < 1 || dIdx > dest.Length)
            {
                // A PRE-EXISTING dest is never grown: VFP9 raises (verified live) rather than clipping.
                if (destPreexisted) throw new MicroVfpRuntimeException("Subscript is outside defined range.");
                break;
            }
            dest.SetLinear(dIdx, src.GetLinear(sIdx));
            copied++;
        }
        return copied;
    }

    /// <summary>ADEL(ArrayName, nElement [, nRowOrColumn]) — delete an element/row (default) or a column
    /// (3rd arg &gt; 1); size UNCHANGED, the freed tail slot(s) <c>.F.</c>-filled (hackfox s4g211). Returns 1.
    /// An out-of-range or non-positive index is a runtime ERROR in VFP9 (verified live), not a silent no-op.</summary>
    private int FnADel(VfpValue[] a)
    {
        if (a.Length < 2) return 0;
        var arr = Memory.FindArray(a[0].AsString);
        if (arr is null) return 0;
        int idx = (int)a[1].AsNumber;
        if (IsColumnMode(arr, a))
        {
            if (idx < 1 || idx > arr.Cols) throw new MicroVfpRuntimeException("Subscript is outside defined range.");
            arr.DeleteColumn(idx);
        }
        else
        {
            if (idx < 1 || idx > arr.Rows) throw new MicroVfpRuntimeException("Subscript is outside defined range.");
            arr.DeleteRow(idx);
        }
        return 1;                                            // s4g211: the error-return VALUE is unreliable; on success ⇒ 1.
    }

    /// <summary>AINS(ArrayName, nElement [, nRowOrColumn]) — insert a blank (<c>.F.</c>) element/row (default)
    /// or column (3rd arg &gt; 1); size UNCHANGED, the original last element/row/column is LOST (hackfox
    /// s4g211 — kept 1:1, not "protected against"). Returns 1.</summary>
    private int FnAIns(VfpValue[] a)
    {
        if (a.Length < 2) return 0;
        var arr = Memory.FindArray(a[0].AsString);
        if (arr is null) return 0;
        int idx = (int)a[1].AsNumber;
        if (IsColumnMode(arr, a))
        {
            if (idx < 1 || idx > arr.Cols) throw new MicroVfpRuntimeException("Subscript is outside defined range.");
            arr.InsertColumn(idx);
        }
        else
        {
            if (idx < 1 || idx > arr.Rows) throw new MicroVfpRuntimeException("Subscript is outside defined range.");
            arr.InsertRow(idx);
        }
        return 1;
    }

    // ADEL/AINS 3rd parameter: column mode only for a 2-D array and a value > 1 (s4g211: "a number less
    // than or equal to 1 is identical to omitting the parameter"; 2 is the only documented column trigger).
    private static bool IsColumnMode(VfpArray arr, VfpValue[] a)
        => arr.Is2D && a.Length > 2 && IsNumeric(a[2]) && (int)a[2].AsNumber > 1;

    /// <summary>AELEMENT(ArrayName, nRow [, nCol]) — the LINEAR (row-major) element number for the given
    /// subscripts. With BOTH subscripts on a 2-D array it is (nRow-1)*Cols+nCol. With a SINGLE subscript on
    /// a 2-D array VFP treats it as an index validated against Rows and returns the subscript itself (an
    /// out-of-range value is a runtime ERROR, not 0). For a 1-D array an out-of-range subscript ⇒ 0
    /// (hackfox s4g213: no error).</summary>
    private int FnAElement(VfpValue[] a)
    {
        if (a.Length < 2) return 0;
        var arr = Memory.FindArray(a[0].AsString);
        if (arr is null) return 0;
        int r = (int)a[1].AsNumber;
        if (arr.Is2D)
        {
            if (a.Length > 2 && IsNumeric(a[2]))
            {
                int c = (int)a[2].AsNumber;
                if (r < 1 || r > arr.Rows || c < 1 || c > arr.Cols) return 0;
                return (r - 1) * arr.Cols + c;
            }
            // Single subscript on a 2-D array: validated against Rows, returns the subscript itself.
            if (r < 1 || r > arr.Rows)
                throw new MicroVfpRuntimeException("Subscript is outside defined range.");
            return r;
        }
        return r >= 1 && r <= arr.Length ? r : 0;
    }

    /// <summary>ASUBSCRIPT(ArrayName, nElement, nSubscript) — the row (nSubscript=1) or column (2) subscript
    /// for a LINEAR element number. Unlike AELEMENT, an out-of-range element (or a column subscript on a 1-D
    /// array) is a REAL runtime ERROR, NOT a 0 fallback (hackfox s4g213 — deliberately reproduced).</summary>
    private int FnASubscript(VfpValue[] a)
    {
        if (a.Length < 3) throw new MicroVfpRuntimeException("ASUBSCRIPT() requires three arguments.");
        var arr = Memory.FindArray(a[0].AsString)
                  ?? throw new MicroVfpRuntimeException("ASUBSCRIPT(): the variable is not an array.");
        int n = (int)a[1].AsNumber;
        int sub = (int)a[2].AsNumber;
        if (n < 1 || n > arr.Length)
            throw new MicroVfpRuntimeException("ASUBSCRIPT(): element number is out of range.");
        if (arr.Is2D)
            return sub switch
            {
                1 => (n - 1) / arr.Cols + 1,
                2 => (n - 1) % arr.Cols + 1,
                _ => throw new MicroVfpRuntimeException("ASUBSCRIPT(): invalid subscript selector."),
            };
        if (sub == 1) return n;
        throw new MicroVfpRuntimeException("ASUBSCRIPT(): a 1-D array has no column subscript.");
    }

    /// <summary>AFIELDS(ArrayName [, cAlias | nWorkArea]) — (re)dimension ArrayName to (nFields × 18) and
    /// fill one row per field: [1] name, [2] type, [3] length, [4] decimals, [5] nullable, [6] NOCPTRANS
    /// (binary), [7]-[18] the DBC-property columns (blank for a free table). The hidden <c>_NullFlags</c>
    /// system field is NOT reported (hackfox s4g292). Returns the field count.</summary>
    private int FnAFields(VfpValue[] a)
    {
        if (a.Length == 0) return 0;
        VfpSession.WorkArea? wa;
        if (a.Length > 1)
        {
            var v = a[1];
            wa = v.Type == VfpType.Character
                ? Session.FindAreaByAlias(v.AsString)
                : Session.AreaAt((int)v.AsNumber);
        }
        else wa = Session.AreaAt(Session.CurrentArea);
        if (wa is null) return 0;

        var fields = new List<DbfColumn>();
        foreach (var c in wa.Table.Columns) if (!c.IsSystem) fields.Add(c);
        int n = fields.Count;
        if (n == 0) return 0;

        var arr = Memory.RedimOrCreateArray(a[0].AsString, n, 18);
        for (int i = 0; i < n; i++)
        {
            var c = fields[i];
            arr.Set(i + 1, 1, VfpValue.Character(c.Name));
            arr.Set(i + 1, 2, VfpValue.Character(c.Type.ToString()));
            arr.Set(i + 1, 3, VfpValue.Integer(c.Length));
            arr.Set(i + 1, 4, VfpValue.Integer(c.Decimal));
            arr.Set(i + 1, 5, VfpValue.Logical(c.IsNullable));
            arr.Set(i + 1, 6, VfpValue.Logical(c.IsBinary));
            for (int col = 7; col <= 18; col++) arr.Set(i + 1, col, VfpValue.Character(string.Empty));
        }
        return n;
    }

    /// <summary>ASORT(ArrayName [, nStart [, nCount [, nSortOrder [, nFlags]]]]) — in-place sort. For a 2-D
    /// array nStart identifies both the starting row AND the KEY COLUMN (its column), whole rows move with
    /// the key. nSortOrder any NONZERO value ⇒ descending (0/omitted ⇒ ascending). nFlags bit 1 ⇒
    /// case-insensitive C compare. All
    /// sorted elements must share a data type. Returns 1.</summary>
    private int FnASort(VfpValue[] a)
    {
        if (a.Length == 0) return 0;
        var arr = Memory.FindArray(a[0].AsString);
        if (arr is null) return 0;

        int startEl = a.Length > 1 && IsNumeric(a[1]) ? (int)a[1].AsNumber : 1;
        if (startEl < 1) startEl = 1;
        int numSorted = a.Length > 2 && IsNumeric(a[2]) ? (int)a[2].AsNumber : -1;
        bool desc = a.Length > 3 && IsNumeric(a[3]) && (int)a[3].AsNumber != 0; // VFP: ANY nonzero ⇒ descending.
        bool ci = a.Length > 4 && IsNumeric(a[4]) && ((int)a[4].AsNumber & 1) != 0;

        if (arr.Is2D)
        {
            int cols = arr.Cols;
            int keyCol = (startEl - 1) % cols + 1;
            int startRow = (startEl - 1) / cols + 1;
            int rowCount = numSorted < 0 ? arr.Rows - startRow + 1 : numSorted;
            SortRowRange(arr, startRow, rowCount, keyCol, desc, ci);
        }
        else
        {
            int count = numSorted < 0 ? arr.Length - startEl + 1 : numSorted;
            SortElementRange(arr, startEl, count, desc, ci);
        }
        return 1;
    }

    private void SortElementRange(VfpArray arr, int start, int count, bool desc, bool ci)
    {
        if (count <= 1) return;
        var items = new VfpValue[count];
        for (int i = 0; i < count; i++) items[i] = arr.GetLinear(start + i);
        Array.Sort(items, (x, y) => (desc ? -1 : 1) * SortCompare(x, y, ci));
        for (int i = 0; i < count; i++) arr.SetLinear(start + i, items[i]);
    }

    private void SortRowRange(VfpArray arr, int startRow, int rowCount, int keyCol, bool desc, bool ci)
    {
        if (rowCount <= 1) return;
        int cols = arr.Cols;
        var rows = new VfpValue[rowCount][];
        for (int r = 0; r < rowCount; r++)
        {
            rows[r] = new VfpValue[cols];
            for (int c = 1; c <= cols; c++) rows[r][c - 1] = arr.Get(startRow + r, c);
        }
        Array.Sort(rows, (x, y) => (desc ? -1 : 1) * SortCompare(x[keyCol - 1], y[keyCol - 1], ci));
        for (int r = 0; r < rowCount; r++)
            for (int c = 1; c <= cols; c++) arr.Set(startRow + r, c, rows[r][c - 1]);
    }

    // Type-homogeneous compare using the session collation (reused from the expression engine's decision:
    // MACHINE/GENERAL). Mixed data types are a VFP error 9/11 (backlog C.1) — reproduced as a runtime error.
    private int SortCompare(VfpValue x, VfpValue y, bool ci)
    {
        int cx = SortClass(x), cy = SortClass(y);
        if (cx != cy)
            throw new MicroVfpRuntimeException("ASORT(): array elements are not the same data type.");
        return cx switch
        {
            0 => ci
                ? _ctx.Collation.Compare(x.AsString.ToUpperInvariant().AsSpan(), y.AsString.ToUpperInvariant().AsSpan())
                : _ctx.Collation.Compare(x.AsString.AsSpan(), y.AsString.AsSpan()),
            1 => x.AsNumber.CompareTo(y.AsNumber),
            2 => x.AsDateTime.CompareTo(y.AsDateTime),
            3 => x.AsLogical.CompareTo(y.AsLogical),
            _ => 0,
        };
    }

    private static int SortClass(VfpValue v) => v.Type switch
    {
        VfpType.Character => 0,
        VfpType.Numeric or VfpType.Integer or VfpType.Currency => 1,
        VfpType.Date or VfpType.DateTime => 2,
        VfpType.Logical => 3,
        _ => 4,
    };

    /// <summary>ADATABASES(ArrayName) — fill (1 × 2) [name, full DBC path] for the open database container,
    /// or return 0 when none is open. microVFP has a single open DBC (backlog C.7). hackfox s4g666: column 2
    /// is always the full path regardless of SET FULLPATH.</summary>
    private int FnADatabases(VfpValue[] a)
    {
        if (a.Length == 0) return 0;
        string? dbc = Session.DatabasePath;
        if (Session.Database is null || string.IsNullOrEmpty(dbc)) return 0;
        var arr = Memory.RedimOrCreateArray(a[0].AsString, 1, 2);
        arr.Set(1, 1, VfpValue.Character(Path.GetFileNameWithoutExtension(dbc)));
        arr.Set(1, 2, VfpValue.Character(dbc));
        return 1;
    }

    /// <summary>AUSED(ArrayName [, nDataSessionId]) — fill (nAreas × 2) [alias, work-area number] for every
    /// open work area (ordered by area number), or return 0 when none is open. Single data session, so the
    /// optional session id is ignored (backlog C.7).</summary>
    private int FnAUsed(VfpValue[] a)
    {
        if (a.Length == 0) return 0;
        var areas = new List<VfpSession.WorkArea>(Session.OpenAreas);
        if (areas.Count == 0) return 0;
        areas.Sort((x, y) => x.Area.CompareTo(y.Area));
        var arr = Memory.RedimOrCreateArray(a[0].AsString, areas.Count, 2);
        for (int i = 0; i < areas.Count; i++)
        {
            arr.Set(i + 1, 1, VfpValue.Character(areas[i].Alias));
            arr.Set(i + 1, 2, VfpValue.Integer(areas[i].Area));
        }
        return areas.Count;
    }

    /// <summary>ASESSIONS(ArrayName) — fill a 1-D array with the open data-session ids. microVFP is a SINGLE
    /// data session (see the SET DATASESSION stub), so this is always [1] with a return of 1 (backlog C.1).</summary>
    private int FnASessions(VfpValue[] a)
    {
        if (a.Length == 0) return 0;
        var arr = Memory.RedimOrCreateArray(a[0].AsString, 1, 0);
        arr.SetLinear(1, VfpValue.Integer(1));
        return 1;
    }

    private VfpValue FnCoCreateGuid()
    {
        var bytes = new byte[16];
        Random.Shared.NextBytes(bytes);
        Memory.Set("lcBuffer", VfpValue.Character(Encoding.Latin1.GetString(bytes)));
        return VfpValue.Integer(0);
    }

    // ─────────────────────────── P2 scalar/string batch ───────────────────────────
    // Authoritative semantics: MICROVFP_EXTENSIONS_BACKLOG.md §C.18 + verified empirically against the
    // VFP9 runtime (TRANSFORM pictures / SOUNDEX quirks / PROPER edge cases confirmed byte-for-byte).

    /// <summary>The default word separators for GETWORDCOUNT()/GETWORDNUM() (space, tab, LF, CR).</summary>
    private const string DefaultWordDelims = " \t\n\r";

    /// <summary>VARTYPE(eExpr [, lNullDataType]): the 1-letter type code of the ALREADY-evaluated value
    /// (unlike TYPE(), which macro-evaluates a string). NULL → "X" (the VFP quirk) unless lNullDataType is
    /// set — but a bare .NULL. carries no base type, so it stays "X".</summary>
    private VfpValue FnVartype(VfpValue[] a)
    {
        if (a.Length == 0) return VfpValue.Character("U");
        var v = a[0];
        if (v.Type == VfpType.Null) return VfpValue.Character("X");
        string code = v.Type switch
        {
            VfpType.Character => "C",
            VfpType.Numeric or VfpType.Integer => "N",
            VfpType.Currency => "Y",
            VfpType.Date => "D",
            VfpType.DateTime => "T",
            VfpType.Logical => "L",
            _ => "U",
        };
        return VfpValue.Character(code);
    }

    /// <summary>TRANSFORM(eExpr [, cFormatCodes]): picture/@-function formatting of any value. Without a
    /// format the value's default stringification is used; with one, the @-function codes (@! upper, @0 hex)
    /// and numeric PICTURE templates (9/#/,/.) are honoured.</summary>
    private VfpValue FnTransform(VfpValue[] a)
    {
        if (a.Length == 0) return VfpValue.Character(string.Empty);
        var v = a[0];
        string fmt = a.Length > 1 && a[1].Type == VfpType.Character ? a[1].AsString : string.Empty;
        if (fmt.Length == 0) return VfpValue.Character(TransformNoFormat(v));

        // Split "@<funcs> <picture>" — the @-run ends at the first space; the rest is the PICTURE template.
        string funcs = string.Empty, picture = fmt;
        if (fmt[0] == '@')
        {
            int sp = fmt.IndexOf(' ');
            if (sp < 0) { funcs = fmt[1..]; picture = string.Empty; }
            else { funcs = fmt.Substring(1, sp - 1); picture = fmt[(sp + 1)..]; }
        }
        bool hex = funcs.Contains('0');
        bool upper = funcs.Contains('!');

        if (v.Type is VfpType.Numeric or VfpType.Integer or VfpType.Currency)
        {
            if (hex) return VfpValue.Character("0x" + ((uint)v.AsInteger).ToString("X8", CultureInfo.InvariantCulture));
            if (picture.Length > 0) return VfpValue.Character(FormatNumericPicture(v.AsNumber, picture));
            return VfpValue.Character(TransformNoFormat(v));
        }
        if (v.Type == VfpType.Character)
        {
            string s = v.AsString;
            if (upper) s = s.ToUpperInvariant();
            return VfpValue.Character(s);
        }
        if (v.Type == VfpType.Logical)
        {
            if (picture.Contains('Y') || picture.Contains('y')) return VfpValue.Character(v.AsLogical ? "Y" : "N");
            return VfpValue.Character(v.AsLogical ? "T" : "F");
        }
        return VfpValue.Character(TransformNoFormat(v));
    }

    /// <summary>The default (format-less) TRANSFORM/TEXTMERGE stringification of a value.</summary>
    private static string TransformNoFormat(VfpValue v) => v.Type switch
    {
        VfpType.Character => v.AsString,
        // VFP9 (runtime-verified): the format-less TRANSFORM of a logical is the ".T."/".F." literal, NOT "T"/"F".
        VfpType.Logical => v.AsLogical ? ".T." : ".F.",
        VfpType.Null => ".NULL.",
        VfpType.Date => v.AsDate == default ? new string(' ', 10) : v.AsDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
        VfpType.DateTime => v.AsDateTime.ToString("MM/dd/yyyy hh:mm:ss tt", CultureInfo.InvariantCulture),
        VfpType.Numeric or VfpType.Integer or VfpType.Currency => NumberToPlainString(v.AsNumber),
        _ => v.ToString(),
    };

    /// <summary>A numeric value as its shortest exact decimal string (no leading/trailing padding, trailing
    /// fractional zeros trimmed): 5→"5", 1234.5→"1234.5", 1234.50→"1234.5".</summary>
    private static string NumberToPlainString(decimal d)
    {
        string s = d.ToString(CultureInfo.InvariantCulture);
        if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
        return s;
    }

    /// <summary>Render <paramref name="value"/> onto a numeric PICTURE template (9/# digit slots, ',' grouping,
    /// '.' decimal point, '*' fill). Digits fill right-to-left; the sign takes the slot left of the top digit;
    /// unfilled slots become spaces and grouping is suppressed in the pad area.</summary>
    private static string FormatNumericPicture(decimal value, string picture)
    {
        int dot = picture.IndexOf('.');
        string intPat = dot < 0 ? picture : picture[..dot];
        string decPat = dot < 0 ? string.Empty : picture[(dot + 1)..];
        int decCount = decPat.Count(ch => ch is '9' or '#');

        decimal absRounded = Math.Round(Math.Abs(value), decCount, MidpointRounding.AwayFromZero);
        bool neg = value < 0;
        string fixedStr = absRounded.ToString("F" + decCount, CultureInfo.InvariantCulture);
        int fdot = fixedStr.IndexOf('.');
        string intDigits = fdot < 0 ? fixedStr : fixedStr[..fdot];
        string decDigits = fdot < 0 ? string.Empty : fixedStr[(fdot + 1)..];
        string body = (neg ? "-" : string.Empty) + intDigits;

        var outChars = new List<char>();
        int bi = body.Length - 1;
        for (int pi = intPat.Length - 1; pi >= 0; pi--)
        {
            char t = intPat[pi];
            switch (t)
            {
                case '9':
                case '#':
                    if (bi >= 0) outChars.Add(body[bi--]); else outChars.Add(' ');
                    break;
                case ',':
                    outChars.Add(bi >= 0 && char.IsDigit(body[bi]) ? ',' : ' ');
                    break;
                case '*':
                    if (bi >= 0) outChars.Add(body[bi--]); else outChars.Add('*');
                    break;
                default:
                    outChars.Add(t);
                    break;
            }
        }
        while (bi >= 0) outChars.Add(body[bi--]);   // picture too narrow → keep the overflow digits.
        outChars.Reverse();
        string intResult = new string(outChars.ToArray());
        return decCount > 0 ? intResult + "." + decDigits : intResult;
    }

    /// <summary>PROPER(cStr): upper-case the FIRST character of each space-delimited word, lower-case the
    /// rest. Deliberately as "simple-minded" as VFP — Mc/Mac and apostrophes are NOT special (the documented
    /// PROPER weakness): "mcDONALD"→"Mcdonald", "o'brien"→"O'brien".</summary>
    private VfpValue FnProper(VfpValue[] a)
    {
        if (a.Length == 0) return VfpValue.Character(string.Empty);
        string s = a[0].AsString;
        var sb = new StringBuilder(s.Length);
        bool atWordStart = true;
        foreach (char c in s)
        {
            if (c == ' ') { sb.Append(c); atWordStart = true; }
            else { sb.Append(atWordStart ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c)); atWordStart = false; }
        }
        return VfpValue.Character(sb.ToString());
    }

    /// <summary>ISLOWER(cStr): the FIRST character is a lowercase letter (empty/non-alpha → .F.).</summary>
    private static bool FnIsLower(VfpValue[] a) => a.Length > 0 && a[0].AsString.Length > 0 && char.IsLower(a[0].AsString[0]);

    /// <summary>ISUPPER(cStr): the FIRST character is an uppercase letter (empty/non-alpha → .F.).</summary>
    private static bool FnIsUpper(VfpValue[] a) => a.Length > 0 && a[0].AsString.Length > 0 && char.IsUpper(a[0].AsString[0]);

    /// <summary>STREXTRACT(cSearched, cBegin [, cEnd [, nOcc [, nFlags]]]): the substring after the
    /// nOcc-th cBegin up to cEnd (or end-of-string when cEnd is empty). nFlags bit 1 = case-insensitive;
    /// bit 2 = return the remainder when cEnd is present but not found.</summary>
    private static string FnStrExtract(VfpValue[] a)
    {
        if (a.Length < 2) return string.Empty;
        string searched = a[0].AsString;
        string begin = a[1].AsString;
        string end = a.Length > 2 ? a[2].AsString : string.Empty;
        int occ = a.Length > 3 ? (int)a[3].AsNumber : 1;
        int flags = a.Length > 4 ? (int)a[4].AsNumber : 0;
        if (occ < 1) occ = 1;
        var cmp = (flags & 1) != 0 ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool remainderIfNoEnd = (flags & 2) != 0;

        if (begin.Length == 0) return string.Empty;
        int idx = -1, from = 0;
        for (int k = 0; k < occ; k++)
        {
            idx = searched.IndexOf(begin, from, cmp);
            if (idx < 0) return string.Empty;
            from = idx + begin.Length;
        }
        int start = idx + begin.Length;
        if (end.Length == 0) return searched[start..];
        int endIdx = searched.IndexOf(end, start, cmp);
        if (endIdx < 0) return remainderIfNoEnd ? searched[start..] : string.Empty;
        return searched.Substring(start, endIdx - start);
    }

    /// <summary>Split <paramref name="s"/> into "words" on any single character in <paramref name="delims"/>,
    /// collapsing runs (no empty words) — the shared engine for GETWORDCOUNT/GETWORDNUM.</summary>
    private static List<string> GetWords(string s, string delims)
    {
        var res = new List<string>();
        int i = 0, n = s.Length;
        while (i < n)
        {
            while (i < n && delims.IndexOf(s[i]) >= 0) i++;
            if (i >= n) break;
            int start = i;
            while (i < n && delims.IndexOf(s[i]) < 0) i++;
            res.Add(s[start..i]);
        }
        return res;
    }

    /// <summary>GETWORDCOUNT(cStr [, cDelims]): word count. cDelims (a LIST of single-char separators)
    /// REPLACES the defaults (space/tab/LF/CR).</summary>
    private static int FnGetWordCount(VfpValue[] a)
    {
        if (a.Length == 0) return 0;
        string delims = a.Length > 1 && a[1].Type == VfpType.Character && a[1].AsString.Length > 0 ? a[1].AsString : DefaultWordDelims;
        return GetWords(a[0].AsString, delims).Count;
    }

    /// <summary>GETWORDNUM(cStr, nIndex [, cDelims]): the nIndex-th word (1-based). No bounds error — an
    /// out-of-range / non-positive index silently returns "" (the documented VFP quirk).</summary>
    private static string FnGetWordNum(VfpValue[] a)
    {
        if (a.Length < 2) return string.Empty;
        string delims = a.Length > 2 && a[2].Type == VfpType.Character && a[2].AsString.Length > 0 ? a[2].AsString : DefaultWordDelims;
        var words = GetWords(a[0].AsString, delims);
        int n = (int)a[1].AsNumber;
        return n >= 1 && n <= words.Count ? words[n - 1] : string.Empty;
    }

    /// <summary>The classic (simple) Soundex code (1 letter + 3 digits) VFP returns — verified against the
    /// VFP9 runtime incl. its quirks (first char kept verbatim; H/W transparent; adjacent same-code merge;
    /// empty → "0000").</summary>
    private static string SoundexCode(string s)
    {
        if (s.Length == 0) return "0000";
        char first = s[0];
        var sb = new StringBuilder(4);
        sb.Append(char.IsLetter(first) ? char.ToUpperInvariant(first) : first);
        int prev = SoundexDigit(first);
        for (int i = 1; i < s.Length && sb.Length < 4; i++)
        {
            int d = SoundexDigit(s[i]);
            if (d != 0 && d != prev) sb.Append((char)('0' + d));
            prev = d;
        }
        while (sb.Length < 4) sb.Append('0');
        return sb.ToString();
    }

    private static int SoundexDigit(char c) => char.ToUpperInvariant(c) switch
    {
        'B' or 'F' or 'P' or 'V' => 1,
        'C' or 'G' or 'J' or 'K' or 'Q' or 'S' or 'X' or 'Z' => 2,
        'D' or 'T' => 3,
        'L' => 4,
        'M' or 'N' => 5,
        'R' => 6,
        _ => 0,
    };

    /// <summary>SOUNDEX(cStr): phonetic code (see <see cref="SoundexCode"/>).</summary>
    private static string FnSoundex(VfpValue[] a) => SoundexCode(a.Length > 0 ? a[0].AsString : string.Empty);

    /// <summary>DIFFERENCE(cStr1, cStr2): count of position-wise matching Soundex characters (0..4).</summary>
    private static int FnDifference(VfpValue[] a)
    {
        if (a.Length < 2) return 0;
        string c1 = SoundexCode(a[0].AsString), c2 = SoundexCode(a[1].AsString);
        int match = 0;
        for (int i = 0; i < 4; i++) if (c1[i] == c2[i]) match++;
        return match;
    }

    /// <summary>STRCONV(cExpr, nConversion [, ...]): the common, runtime-verified conversion codes. A VFP
    /// character value is a byte string; the connection stores those bytes as Latin1 chars (byte==char code).
    /// The codepage is the connection's Windows-ANSI codepage (== CPCURRENT()). Verified byte-for-byte against
    /// the VFP9 runtime for input "Ab"+CHR(233):
    ///   5 = codepage → double-byte Unicode (UTF-16LE)   ["Ab é" → 41 00 62 00 E9 00]
    ///   6 = double-byte Unicode (UTF-16LE) → codepage   (inverse of 5)
    ///   7 = lower-case (locale)                          ["Ab é" → "ab é"]
    ///   8 = upper-case (locale)                          ["Ab é" → "AB É"]
    ///   9 = codepage → UTF-8                             ["Ab é" → 41 62 C3 A9]
    ///  10 = UTF-8 → codepage                             (inverse of 9)
    /// FLAGGED (not runtime-distinct on an SBCS/1252 connection — pass through unchanged, matching the runtime
    /// for such content): 1/2/3/4 (single↔double-byte DBCS locale conversions) and any other/exotic code.</summary>
    private string FnStrConv(VfpValue[] a)
    {
        if (a.Length < 2) return a.Length > 0 ? a[0].AsString : string.Empty;
        string s = a[0].AsString;
        int mode = (int)a[1].AsNumber;
        var l1 = Encoding.Latin1;
        var cp = ConnectionEncoding();
        byte[] bytes = l1.GetBytes(s);   // the connection's byte string.
        try
        {
            switch (mode)
            {
                case 5:   // codepage bytes → UTF-16LE bytes
                    return l1.GetString(Encoding.Unicode.GetBytes(cp.GetString(bytes)));
                case 6:   // UTF-16LE bytes → codepage bytes
                    return l1.GetString(cp.GetBytes(Encoding.Unicode.GetString(bytes)));
                case 7:   // lower-case (locale)
                    return l1.GetString(cp.GetBytes(cp.GetString(bytes).ToLower(CultureInfo.CurrentCulture)));
                case 8:   // upper-case (locale)
                    return l1.GetString(cp.GetBytes(cp.GetString(bytes).ToUpper(CultureInfo.CurrentCulture)));
                case 9:   // codepage → UTF-8
                    return l1.GetString(Encoding.UTF8.GetBytes(cp.GetString(bytes)));
                case 10:  // UTF-8 → codepage
                    return l1.GetString(cp.GetBytes(Encoding.UTF8.GetString(bytes)));
                default:  // FLAG: DBCS single↔double-byte locale modes / exotic codes — pass through.
                    return s;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or DecoderFallbackException or EncoderFallbackException)
        {
            return s;   // malformed input for the requested conversion — degrade to the input (no error).
        }
    }

    /// <summary>The connection's Windows-ANSI code page as an <see cref="Encoding"/> (== CPCURRENT()); falls
    /// back to Latin1 if the codepage cannot be resolved on this platform.</summary>
    private static Encoding ConnectionEncoding()
    {
        try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage); }
        catch (ArgumentException) { return Encoding.Latin1; }
    }

    /// <summary>CPCONVERT(nCurrentCP, nNewCP, cExpr): transcode cExpr between code pages. Same CP ⇒ identity;
    /// otherwise best-effort via the registered code-page provider, degrading to the input on any failure.</summary>
    private static string FnCpConvert(VfpValue[] a)
    {
        if (a.Length < 3) return a.Length > 2 ? a[2].AsString : string.Empty;
        int from = (int)a[0].AsNumber, to = (int)a[1].AsNumber;
        string s = a[2].AsString;
        if (from == to) return s;
        try
        {
            var srcEnc = Encoding.GetEncoding(from);
            var dstEnc = Encoding.GetEncoding(to);
            byte[] raw = Encoding.Latin1.GetBytes(s);   // the connection stores single-byte text as Latin1.
            byte[] converted = Encoding.Convert(srcEnc, dstEnc, raw);
            return Encoding.Latin1.GetString(converted);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return s;
        }
    }

    /// <summary>CPCURRENT([1|2]): the current system code page — 1 (default) = Windows ANSI, 2 = the
    /// underlying OEM/DOS code page.</summary>
    private static int FnCpCurrent(VfpValue[] a)
    {
        bool oem = a.Length > 0 && IsNumeric(a[0]) && (int)a[0].AsNumber == 2;
        var ti = CultureInfo.CurrentCulture.TextInfo;
        return oem ? ti.OEMCodePage : ti.ANSICodePage;
    }

    /// <summary>CPDBF([cAlias|nArea]): the code page marked in the addressed (or current) table's header,
    /// as a VFP code-page id; 0 when no table is open.</summary>
    private int FnCpDbf(VfpValue[] a)
    {
        var wa = AreaArg(a, 0);
        return wa?.Table.Encoding.CodePage ?? 0;
    }

    /// <summary>TEXTMERGE(cExpr [, lParse [, cDelimBegin [, cDelimEnd]]]): replace every &lt;&lt;expr&gt;&gt;
    /// placeholder with the default stringification of the evaluated expression (a one-liner TEXT…ENDTEXT
    /// engine). The begin/end delimiters default to "&lt;&lt;"/"&gt;&gt;" but may be overridden per-call
    /// (VFP's SET TEXTMERGE DELIMITERS is a separate, unimplemented command). lParse is accepted and ignored —
    /// this function always evaluates.</summary>
    private string FnTextMerge(VfpValue[] a)
    {
        if (a.Length == 0) return string.Empty;
        string s = a[0].AsString;
        string beg = a.Length > 2 && a[2].Type == VfpType.Character && a[2].AsString.Length > 0 ? a[2].AsString : "<<";
        string end = a.Length > 3 && a[3].Type == VfpType.Character && a[3].AsString.Length > 0 ? a[3].AsString : ">>";
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            int open = s.IndexOf(beg, i, StringComparison.Ordinal);
            if (open < 0) { sb.Append(s, i, s.Length - i); break; }
            sb.Append(s, i, open - i);
            int close = s.IndexOf(end, open + beg.Length, StringComparison.Ordinal);
            if (close < 0) { sb.Append(s, open, s.Length - open); break; }
            string expr = s.Substring(open + beg.Length, close - open - beg.Length);
            sb.Append(TransformNoFormat(EvalText(expr)));
            i = close + end.Length;
        }
        return sb.ToString();
    }

    /// <summary>ALINES(ArrayName, cExpr [, nFlags | lTrim [, cParseChar1 [, cParseChar2 …]]]): split cExpr
    /// into lines, (re)dimension the named array to the line count, fill it row-by-row, and return the count.
    /// Default parsing is on line breaks (CR, LF, CRLF); when one or more cParseChar are given they REPLACE
    /// the line-break parsing (split on ANY of those single chars). The 3rd arg is overloaded (the documented
    /// VFP quirk): logical ⇒ lTrim; numeric ⇒ nFlags (bit 1 = trim each line, bit 4 = skip empty lines);
    /// character ⇒ it is already the first cParseChar. The array name arrives as a string (MicroVfpExprRewrite
    /// quotes the first arg).</summary>
    private int FnAlines(VfpValue[] a)
    {
        if (a.Length < 2) return 0;
        string name = a[0].AsString;
        string text = a[1].AsString;

        bool trim = false, skipEmpty = false;
        int parseFrom = 2;                       // index of the first cParseChar arg (default: none).
        if (a.Length > 2)
        {
            var p = a[2];
            if (p.Type == VfpType.Logical) { trim = p.AsLogical; parseFrom = 3; }
            else if (p.Type is VfpType.Numeric or VfpType.Integer or VfpType.Currency)
            {
                int flags = (int)p.AsNumber;
                trim = (flags & 1) != 0;
                skipEmpty = (flags & 4) != 0;
                parseFrom = 3;
            }
            // else (character) ⇒ p is already the first parse char; parseFrom stays 2.
        }

        // Collect any explicit parse characters (each argument contributes its single chars).
        var parseChars = new List<char>();
        for (int k = parseFrom; k < a.Length; k++)
            foreach (char c in a[k].AsString) parseChars.Add(c);

        string[] lines;
        if (parseChars.Count > 0)
            lines = text.Split(parseChars.ToArray());
        else
            lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var outLines = new List<string>(lines.Length);
        foreach (var raw in lines)
        {
            string line = trim ? raw.Trim(' ', '\t') : raw;
            if (skipEmpty && line.Length == 0) continue;
            outLines.Add(line);
        }

        int count = outLines.Count;
        var arr = Memory.RedimOrCreateArray(name, Math.Max(1, count), 0);
        for (int i = 0; i < count; i++) arr.Set(i + 1, null, VfpValue.Character(outLines[i]));
        return count;
    }

    /// <summary>SYS(10, nJulianDay): a Julian-day number → date string, honouring the interpreter's date
    /// rendering (inverse of SYS(11)/SYS(1)).</summary>
    private VfpValue FnSys10(VfpValue[] a)
    {
        if (a.Length < 2) return VfpValue.Character(string.Empty);
        var d = FromJulianDay((int)a[1].AsNumber);
        return VfpValue.Character(d.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture));
    }

    /// <summary>The inverse of <see cref="JulianDay"/> (Fliegel–Van Flandern).</summary>
    private static DateOnly FromJulianDay(int jd)
    {
        int a = jd + 32044;
        int b = (4 * a + 3) / 146097;
        int c = a - 146097 * b / 4;
        int d = (4 * c + 3) / 1461;
        int e = c - 1461 * d / 4;
        int m = (5 * e + 2) / 153;
        int day = e - (153 * m + 2) / 5 + 1;
        int month = m + 3 - 12 * (m / 10);
        int year = 100 * b + d - 4800 + m / 10;
        return new DateOnly(year, month, day);
    }

    private static int FnOccurs(VfpValue[] a)
    {
        if (a.Length < 2) return 0;
        string needle = a[0].AsString, hay = a[1].AsString;
        if (needle.Length == 0) return 0;
        int count = 0, i = 0;
        while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }

    private static int FnAtc(VfpValue[] a)
    {
        if (a.Length < 2) return 0;
        string needle = a[0].AsString, hay = a[1].AsString;
        int occ = a.Length > 2 ? Math.Max(1, (int)a[2].AsNumber) : 1;
        int i = -1;
        for (int k = 0; k < occ; k++)
        {
            i = hay.IndexOf(needle, i + 1, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return 0;
        }
        return i + 1;
    }

    // STRTRAN(cSearched, cSought [, cReplacement]) — replace every (case-sensitive, like VFP default)
    // occurrence of cSought with cReplacement. The RI infra (riopen/rireuse/riend) flips a cursor's
    // "?"/"*" reuse marker in pcRIcursors with this.
    private static string FnStrtran(VfpValue[] a)
    {
        if (a.Length < 2) return a.Length > 0 ? a[0].AsString : string.Empty;
        string src = a[0].AsString, sought = a[1].AsString;
        string repl = a.Length > 2 ? a[2].AsString : string.Empty;
        if (sought.Length == 0) return src;
        return src.Replace(sought, repl, StringComparison.Ordinal);
    }

    // OLDVAL(cField [, cAlias]) — the value at buffer start, captured by the matching REPLACE (so a
    // manually-fired RI UPDATE trigger reads the OLD key); falls back to the current value when none.
    private VfpValue FnOldVal(VfpValue[] a)
    {
        if (a.Length == 0) return VfpValue.Null;
        string field = StripQualifier(a[0].AsString);
        int area = a.Length > 1 ? AreaNumber(a[1]) : Session.CurrentArea;
        var m = Meta(area);
        // The buffer's per-record OLD map (the value at buffer start / last commit) takes precedence, then
        // the inline-RI OldVals (write-through path), then the current on-disk value.
        if (m.Buf is { } buf && buf.Rows.TryGetValue(m.RecNo, out var e) && e.Old.TryGetValue(field, out var bold))
            return VfpValue.FromClr(bold);
        if (m.OldVals is not null && m.OldVals.TryGetValue(field, out var old))
            return VfpValue.FromClr(old);
        return FnCurVal(a);
    }

    // CURVAL(cField [, cAlias]) — the current ON-DISK value (bypasses the buffer). Single-process ⇒ this is
    // also the "committed" value the (moot) optimistic-conflict check would compare against.
    private VfpValue FnCurVal(VfpValue[] a)
    {
        if (a.Length == 0) return VfpValue.Null;
        string field = StripQualifier(a[0].AsString);
        int area = a.Length > 1 ? AreaNumber(a[1]) : Session.CurrentArea;
        var wa = Session.AreaAt(area);
        return wa is null ? VfpValue.Null : VfpValue.FromClr(ReadFieldOnDisk(wa, field));
    }

    // ─────────────────────────── CURSORGETPROP / CURSORSETPROP / TABLEUPDATE / TABLEREVERT / GETFLDSTATE ─────────

    // CURSORGETPROP(cProperty [, cAlias|nWorkArea]) — the free-table property subset microVFP backs
    // (s4g348): Buffering (real per-area mode), SourceName/SourceType/Database. Unknown properties fall
    // back to .NULL. (a view-only property set is out of scope — no view model).
    private VfpValue FnCursorGetProp(VfpValue[] a)
    {
        if (a.Length == 0) return VfpValue.Null;
        int area = a.Length > 1 ? AreaNumber(a[1]) : Session.CurrentArea;
        var m = Meta(area);
        var wa = Session.AreaAt(area);
        return a[0].AsString.ToUpperInvariant() switch
        {
            "BUFFERING" => VfpValue.Integer(m.Buffering),
            "SOURCENAME" => VfpValue.Character(m.SourceName ?? wa?.Alias ?? string.Empty),
            "SOURCETYPE" => m.SourceType ?? VfpValue.Integer(0),
            "DATABASE" => VfpValue.Character(m.DatabaseProp ?? string.Empty),
            _ => VfpValue.Null,
        };
    }

    // CURSORSETPROP(cProperty, eValue [, cAlias|nWorkArea]) — sets the free-table subset (s4g348). Buffering
    // 1..5 (needs SET MULTILOCKS ON in real VFP for 4/5; single-process here so no lock model). Setting a
    // property outside the backed subset returns .F. (rather than throwing). Returns .T. on success.
    private VfpValue FnCursorSetProp(VfpValue[] a)
    {
        if (a.Length < 2) return VfpValue.Logical(false);
        int area = a.Length > 2 ? AreaNumber(a[2]) : Session.CurrentArea;
        var m = Meta(area);
        switch (a[0].AsString.ToUpperInvariant())
        {
            case "BUFFERING":
                int n = (int)a[1].AsNumber;
                if (n < 1 || n > 5) return VfpValue.Logical(false);
                // Changing the buffering mode while edits are still PENDING is rejected in VFP9 (the buffer
                // must be committed/reverted first) — leave BOTH the mode and the buffer untouched. This also
                // guarantees a live buffer never coexists with mode 1 (reads never shadow disk under 1).
                if (n != m.Buffering && m.Buf is { IsEmpty: false }) return VfpValue.Logical(false);
                m.Buffering = n;
                if (n >= 2) _anyBuffering = true;
                return VfpValue.Logical(true);
            case "SOURCENAME": m.SourceName = a[1].AsString; return VfpValue.Logical(true);
            case "SOURCETYPE": m.SourceType = a[1]; return VfpValue.Logical(true);
            case "DATABASE": m.DatabaseProp = a[1].AsString; return VfpValue.Logical(true);
            default: return VfpValue.Logical(false);   // outside the backed free-table subset.
        }
    }

    // TABLEUPDATE([nRows | lAllRows] [, lForce] [, cAlias|nWorkArea]) — commit buffered changes to disk;
    // .T./.F. lForce is accepted but moot single-process (no optimistic conflict is fabricated). A numeric
    // >=1 or logical .T. first arg ⇒ all rows; otherwise the current row only.
    private VfpValue FnTableUpdate(VfpValue[] a)
    {
        bool allRows = a.Length > 0 && (a[0].Type == VfpType.Logical ? a[0].AsLogical : (IsNumeric(a[0]) && a[0].AsNumber >= 1));
        int area = a.Length > 2 ? AreaNumber(a[2]) : Session.CurrentArea;
        return VfpValue.Logical(CommitBuffer(area, allRows));
    }

    // TABLEREVERT([lAllRows] [, cAlias|nWorkArea]) — discard buffered changes; returns the count reverted.
    private VfpValue FnTableRevert(VfpValue[] a)
    {
        bool allRows = a.Length > 0 && (a[0].Type == VfpType.Logical ? a[0].AsLogical : (IsNumeric(a[0]) && a[0].AsNumber >= 1));
        int area = a.Length > 1 ? AreaNumber(a[1]) : Session.CurrentArea;
        // TABLEREVERT on a never-buffered (mode 1) area is a catchable ERROR in VFP9, not a trivial 0.
        if (!_meta.TryGetValue(area, out var m) || m.Buffering <= 1)
            throw new MicroVfpRuntimeException("TABLEREVERT(): the current work area is not buffered.");
        if (m.Buf is null) return VfpValue.Integer(0);         // buffering on, nothing pending → 0.
        var wa = Session.AreaAt(area);
        var buf = m.Buf;
        int count;
        if (allRows)
        {
            count = buf.Rows.Count + buf.Appends.Count;
            buf.Rows.Clear();
            buf.Appends.Clear();
        }
        else
        {
            count = 0;
            int rec = m.RecNo, rcTable = wa?.Table.RecordCount ?? 0;
            if (rec >= 1 && rec <= rcTable) { if (buf.Rows.Remove(rec)) count = 1; }
            else if (rec > rcTable) { int ai = rec - rcTable - 1; if (ai >= 0 && ai < buf.Appends.Count) { buf.Appends.RemoveAt(ai); count = 1; } }
        }
        m.Cached = null;                         // reverted values re-read from disk.
        if (buf.IsEmpty) m.Buf = null;
        return VfpValue.Integer(count);
    }

    // GETFLDSTATE(cFieldName | nFieldNumber [, cAlias]) — the per-field buffer change state: 1 unchanged,
    // 2 changed, 3 appended (unchanged field), 4 appended+changed. nFieldNumber is 1-based (0 ⇒ the record
    // delete-state). An unbuffered / unedited field is 1. (s4g395: on real tables the state is derived from
    // the buffer; SETFLDSTATE is inert.)
    private VfpValue FnGetFldState(VfpValue[] a)
    {
        if (a.Length == 0) return VfpValue.Integer(1);
        int area = a.Length > 1 ? AreaNumber(a[1]) : Session.CurrentArea;
        var wa = Session.AreaAt(area);
        if (wa is null) return VfpValue.Integer(1);
        var m = Meta(area);
        int colIdx = IsNumeric(a[0]) ? (int)a[0].AsNumber - 1 : ColumnIndex(wa.Table, StripQualifier(a[0].AsString));
        if (m.Buffering <= 1 || m.Buf is not { } buf) return VfpValue.Integer(1);
        int rcTable = wa.Table.RecordCount;
        if (m.RecNo > rcTable)                                  // a buffered appended row.
        {
            int ai = m.RecNo - rcTable - 1;
            if (ai >= 0 && ai < buf.Appends.Count)
                return VfpValue.Integer(colIdx >= 0 && buf.Appends[ai].Fields.ContainsKey(colIdx) ? 4 : 3);
            return VfpValue.Integer(1);
        }
        if (m.RecNo >= 1 && m.RecNo <= rcTable && buf.Rows.TryGetValue(m.RecNo, out var e))
        {
            if (colIdx >= 0) return VfpValue.Integer(e.Fields.ContainsKey(colIdx) ? 2 : 1);
            return VfpValue.Integer(e.DeletedOverride is not null ? 2 : 1);   // field 0 = record delete-state.
        }
        return VfpValue.Integer(1);
    }

    private string TypeOf(string expr)
    {
        string e = expr.Trim();
        if (IsBareVariable(e) && !IsDefinedName(e)) return "U";
        // An array-element reference `name(…)` / `name[…]` on an UNDEFINED array is "U" (the corpus uses
        // TYPE('gaErrors(1)')<>"U" to test existence). A DEFINED array falls through to report the element's
        // own type (TYPE('gaErrors[r,1]')<>"L" tests whether a row slot is still an unfilled .F.).
        string lead = LeadingIdentOf(e);
        if (lead.Length > 0 && lead.Length < e.Length)
        {
            char after = e[lead.Length..].TrimStart() is { Length: > 0 } rest ? rest[0] : '\0';
            if ((after == '(' || after == '[') &&
                Memory.FindArray(lead) is null && !_procs.ContainsKey(lead))
                return "U";
        }
        VfpValue v;
        try { v = EvalText(e); } catch { return "U"; }
        return v.Type switch
        {
            VfpType.Character => "C",
            VfpType.Numeric or VfpType.Integer => "N",
            VfpType.Currency => "Y",
            VfpType.Date => "D",
            VfpType.DateTime => "T",
            VfpType.Logical => "L",
            VfpType.Null => "X",
            _ => "U",
        };
    }

    private bool IsDefinedName(string name)
    {
        if (Memory.IsDefined(name)) return true;
        if (_defines.ContainsKey(name)) return true;
        var cur = Session.AreaAt(Session.CurrentArea);
        return cur is not null && ColumnIndex(cur.Table, name) >= 0;
    }

    // ─────────────────────────── helpers ───────────────────────────

    private VfpSession.WorkArea? AreaArg(VfpValue[] a, int i)
        => a.Length > i ? Session.AreaAt(AreaNumber(a[i])) : Session.AreaAt(Session.CurrentArea);

    private AreaMeta? MetaArg(VfpValue[] a, int i)
    {
        var wa = AreaArg(a, i);
        return wa is null ? null : Meta(wa.Area);
    }

    private int AreaNumber(VfpValue v)
        => IsNumeric(v) ? (int)v.AsNumber : (Session.FindAreaByAlias(v.AsString)?.Area ?? 0);

    private int ResolveAreaRef(NameRef nr)
    {
        if (nr.IsNameExpr)
        {
            var v = Eval(nr.Expr!.Expression);
            return IsNumeric(v) ? (int)v.AsNumber : (Session.FindAreaByAlias(v.AsString)?.Area ?? 0);
        }
        if (int.TryParse(nr.Name, out var n)) return n;
        return Session.FindAreaByAlias(nr.Name!)?.Area ?? 0;
    }

    private string NameOf(NameRef nr) => nr.IsNameExpr ? Eval(nr.Expr!.Expression).AsString : (nr.Name ?? string.Empty);

    private void AssignTo(string target, VfpValue value)
    {
        string t = target.Trim();
        if (t.StartsWith("m.", StringComparison.OrdinalIgnoreCase)) t = t[2..];

        // An array-element target `a(i)` / `a(i,j)` / `a[i]` / `a[i,j]`: write the element of the named
        // array (1-based). The subscripts are live expressions (e.g. gaErrors[lnErrorRows,4]).
        int open = t.IndexOfAny(new[] { '(', '[' });
        if (open > 0)
        {
            string name = t.Substring(0, open).Trim();
            if (Memory.FindArray(name) is { } arr)
            {
                char close = t[open] == '(' ? ')' : ']';
                int end = t.LastIndexOf(close);
                if (end > open)
                {
                    var subs = PrgScan.SplitTopCommas(t.Substring(open + 1, end - open - 1));
                    int s1 = subs.Count > 0 ? (int)EvalText(subs[0]).AsNumber : 1;
                    int? s2 = subs.Count > 1 ? (int)EvalText(subs[1]).AsNumber : (int?)null;
                    arr.Set(s1, s2, value);
                }
            }
            return;   // a non-array subscript target is not a memvar path — ignore (as before).
        }
        Memory.Set(t, value);
    }

    private VfpValue Eval(PrgExpr e)
    {
        if (e.IsParsed) return e.Parsed!.Evaluate(_row, _ctx);
        return EvalText(e.Text);
    }

    private VfpValue EvalText(string text)
    {
        // Normalise VFP array syntax (bracket subscripts → parens; ALEN/AERROR array-name → quoted name)
        // so dynamically-evaluated strings (TYPE/EVAL/macro/DIMENSION dimension expressions) resolve arrays.
        try { return VfpExpression.Parse(MicroVfpExprRewrite.Normalize(text)).Evaluate(_row, _ctx); }
        catch { return VfpValue.Null; }
    }

    // The leading identifier of an expression (letters/digits/underscore after an optional leading letter),
    // or "" when it does not start with one.
    private static string LeadingIdentOf(string s)
    {
        s = s.TrimStart();
        if (s.Length == 0 || !(char.IsAsciiLetter(s[0]) || s[0] == '_')) return string.Empty;
        int j = 1;
        while (j < s.Length && (char.IsAsciiLetterOrDigit(s[j]) || s[j] == '_')) j++;
        return s.Substring(0, j);
    }

    private static bool Truth(VfpValue v) => v.Type == VfpType.Logical && v.AsLogical;
    private static bool IsNumeric(VfpValue v) => v.Type is VfpType.Numeric or VfpType.Integer or VfpType.Currency;

    private static SqlStatement? SafeParseSql(string sql)
    {
        try { return SqlParser.Parse(sql); } catch { return null; }
    }

    private void ScanDefines(IReadOnlyList<PrgStatement> body)
    {
        foreach (var s in Walk(body))
            if (s is DirectiveStmt d) ProcessDefine(d.Text);
    }

    private static IEnumerable<PrgStatement> Walk(IReadOnlyList<PrgStatement> body)
    {
        foreach (var s in body)
        {
            yield return s;
            switch (s)
            {
                case IfStmt i: foreach (var x in Walk(i.Then)) yield return x; foreach (var x in Walk(i.Else)) yield return x; break;
                case DoWhileStmt w: foreach (var x in Walk(w.Body)) yield return x; break;
                case ForStmt f: foreach (var x in Walk(f.Body)) yield return x; break;
                case ScanStmt sc: foreach (var x in Walk(sc.Body)) yield return x; break;
                case DoCaseStmt dc:
                    foreach (var c in dc.Cases) foreach (var x in Walk(c.Body)) yield return x;
                    if (dc.Otherwise is not null) foreach (var x in Walk(dc.Otherwise)) yield return x;
                    break;
            }
        }
    }

    private void ProcessDefine(string text)
    {
        // #DEFINE NAME value  →  register NAME = the value expression (string/number literal).
        string t = text.TrimStart();
        if (t.Length == 0 || t[0] != '#') return;
        t = t[1..].TrimStart();
        if (!t.StartsWith("define", StringComparison.OrdinalIgnoreCase)) return;
        string rest = t[6..].TrimStart();
        int j = 0;
        while (j < rest.Length && (char.IsAsciiLetterOrDigit(rest[j]) || rest[j] == '_')) j++;
        if (j == 0) return;
        string name = rest[..j];
        string valText = rest[j..].Trim();
        if (valText.Length == 0) { _defines[name] = VfpValue.Logical(true); return; }
        _defines[name] = EvalText(valText);
    }

    private static int JulianDay(DateTime d)
    {
        int a = (14 - d.Month) / 12, y = d.Year + 4800 - a, m = d.Month + 12 * a - 3;
        return d.Day + (153 * m + 2) / 5 + 365 * y + y / 4 - y / 100 + y / 400 - 32045;
    }

    /// <summary>VFP <c>SYS(2007)</c> is CRC-16/CCITT-FALSE (poly 0x1021, init 0xFFFF, no reflect/xorout)
    /// over the CP1252 bytes (verified: "ABC"→62728, ""→65535).</summary>
    private static int Crc16Ccitt(string s)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in Encoding.Latin1.GetBytes(s))
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    // ─────────────────────────── the engine row/host bridge ───────────────────────────

    private sealed class Row : IRowContext, IVfpFunctionHost
    {
        private readonly VfpInterpreter _it;
        public Row(VfpInterpreter it) => _it = it;

        public object? GetField(string name) => _it.ResolveName(name);

        public int RecNo
        {
            get { var m = _it.Meta(_it.Session.CurrentArea); return m.RecNo; }
        }

        public bool Deleted => _it.FnDeleted(Array.Empty<VfpValue>());

        public int RecCount => _it.Session.AreaAt(_it.Session.CurrentArea)?.Table.RecordCount ?? 0;

        public bool TryInvoke(string upperName, VfpValue[] args, EvaluationContext ctx, out VfpValue result)
            => _it.HostInvoke(upperName, args, ctx, out result);
    }
}
