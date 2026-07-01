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
            string? order = u.Order is null ? null : NameOf(u.Order);
            _meta[wa.Area] = new AreaMeta { Order = string.IsNullOrEmpty(order) ? null : order };
            GoTop(wa.Area);
        }
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
            bool tagDescending = MasterTag(area)?.Descending ?? false;
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
        var names = Session.AreaAt(area)?.Cdx?.TagNames;
        if (int.TryParse(name, out int n))
        {
            if (n <= 0) return null;   // SET ORDER TO 0 ⇒ natural/record order.
            if (names is not null && n <= names.Count) return names[n - 1];
            throw new MicroVfpRuntimeException(
                $"SET ORDER TO {n}: index number is out of range (the work area has " +
                $"{(names?.Count ?? 0)} tag(s)).");
        }
        if (names is not null)
            foreach (var tagName in names)
                if (string.Equals(tagName, name, StringComparison.OrdinalIgnoreCase))
                    return tagName;
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

        // Unsupported forms → an explicit, catchable refusal (never a silent no-op).
        if (ix.ToIdx is not null || ix.Tag is null)
            throw new MicroVfpRuntimeException(
                "INDEX ON … TO <idx>: standalone .idx indexes are not supported — use INDEX ON … TAG <name> " +
                "(into the structural .cdx).");
        if (ix.OfCdx is not null)
            throw new MicroVfpRuntimeException(
                "INDEX ON … TAG … OF <cdx>: non-structural .cdx files are not supported — only the structural .cdx.");

        // VFP UPPERCASES the tag name in the .cdx directory (verified byte-for-byte vs VFP9); the KEY /
        // FOR expression case is PRESERVED as typed (matches the reverse-engineered DBC .dcx).
        string tagName = NameOf(ix.Tag).Trim().ToUpperInvariant();
        if (tagName.Length == 0)
            throw new MicroVfpRuntimeException("INDEX ON: a TAG name is required.");
        if (wa.Table.SourcePath is not string path)
            throw new MicroVfpRuntimeException("INDEX ON: the current table has no file on disk.");

        string keyExpr = ix.Key.Text.Trim();
        string? forExpr = ix.For?.Text is { } f && f.Trim().Length > 0 ? f.Trim() : null;
        bool candidate = ix.Candidate;
        // UNIQUE clause OR the SET UNIQUE session default (candidate is a distinct constraint, not UNIQUE).
        bool unique = ix.Unique || (Runtime.Unique && !candidate);
        string collation = _ctx.Collation?.Name ?? "MACHINE";
        var def = new CdxTagDefinition(tagName, keyExpr, forExpr, ix.Descending, collation, unique);

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
        var m = Meta(area);
        m.Order = tagName;
        m.OrderReversed = false;   // a fresh INDEX ON resets any prior SET ORDER … DESCENDING override.
        m.Ordered = null; m.OrderedFor = null; m.OrderPos = -1;
        m.KeySet = false; m.KeyRange = false; m.KeyLow = null; m.KeyHigh = null; m.KeyVisible = null;
        GoTop(area);
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

        if (MasterTag(area) is not { } master)
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

    /// <summary>The controlling (master) tag of <paramref name="area"/>, or null when none is active.</summary>
    private CdxTag? MasterTag(int area)
    {
        var wa = Session.AreaAt(area);
        var m = Meta(area);
        if (wa?.Cdx is null || string.IsNullOrEmpty(m.Order)) return null;
        return wa.Cdx.Tag(m.Order!) ?? wa.Cdx.Tag(m.Order!.ToUpperInvariant());
    }

    /// <summary>Build the set of recnos whose master-index key falls in the area's active SET KEY range.
    /// A missing tag ⇒ no restriction (every record). CHARACTER keys are compared on the SAME collated key
    /// bytes the tag STORES (so MACHINE, GENERAL weights and UPPER()-style expression keys all compare
    /// correctly instead of against decoded weight-garbage); other keys decode to a value and compare by
    /// value order.</summary>
    private HashSet<int> BuildKeyVisible(VfpSession.WorkArea wa, AreaMeta m)
    {
        var set = new HashSet<int>();
        var tag = MasterTag(wa.Area);
        if (tag is null)
        {
            for (int r = 1; r <= wa.Table.RecordCount; r++) set.Add(r);
            return set;
        }

        if (tag.IsCharacterKey)
        {
            var coll = VfpCollations.FromSortSequence(tag.Collation);
            // Encode each bound the SAME way the tag stores keys: collated weights padded (0x20) to the
            // tag key length. Comparing bytes then mirrors the on-disk sort order exactly (this is what
            // SEEK does), so a GENERAL tag's weight keys and a MACHINE tag's raw bytes both match.
            byte[]? loBytes = m.KeyLow is { } lo ? CollatedBoundKey(coll, lo, tag.KeyLength) : null;
            byte[]? hiBytes = m.KeyRange && m.KeyHigh is { } hi ? CollatedBoundKey(coll, hi, tag.KeyLength) : null;
            byte[]? loNatural = !m.KeyRange && m.KeyLow is { } lo1 ? coll.GetCollatedKey((lo1.AsString ?? string.Empty).AsSpan()) : null;
            foreach (var e in tag.EnumerateEntries())
            {
                if (CharKeyInRange(m, e.Key, loBytes, hiBytes, loNatural))
                    set.Add((int)e.RecordNumber);
            }
            return set;
        }

        foreach (var e in tag.EnumerateEntries())
        {
            var key = tag.DecodeKey(e.Key);
            if (KeyInRange(m, key)) set.Add((int)e.RecordNumber);
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
        var reopen = new List<(int Area, string Alias, string? Order, bool Excl, bool NoUpd, string? TablePath)>();
        foreach (var w in Session.OpenAreas)
            if (SamePath(w.Table.SourcePath, snap.Path))
                reopen.Add((w.Area, w.Alias, _meta.TryGetValue(w.Area, out var mm) ? mm.Order : null, w.Exclusive, w.NoUpdate, w.Table.SourcePath));

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
            _meta[r.Area] = new AreaMeta { Order = r.Order };
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
        // P3b: a bound delete trigger fires BEFORE the mark; a RESTRICT rule returning .F. ABORTS it.
        if (EnforceReferentialIntegrity && !FireDmlTrigger(RiEvent.Delete, area)) return;
        WriteFlag(area, recall: false);
    }

    private void ExecRecall(RecallStmt rc)
    {
        if (rc.Scope is not null || rc.For is not null) return;
        WriteFlag(Session.CurrentArea, recall: true);
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

        // ── SET KEY (master-index key-range scope; microVFP P1 gap #1) ──
        public bool KeySet;             // a SET KEY range is active on this area's master index.
        public bool KeyRange;           // true ⇒ [KeyLow, KeyHigh] range; false ⇒ single-value match.
        public VfpValue? KeyLow;        // the single value / range low bound (null ⇒ open low).
        public VfpValue? KeyHigh;       // the range high bound (null ⇒ open high / single-value mode).
        public HashSet<int>? KeyVisible; // cached recnos within the key range (null ⇒ rebuild lazily).

        // ── SET RELATION (this area as PARENT; microVFP P1 gap #2) ──
        public List<Relation>? Relations; // child relations set on THIS area (null ⇒ none).
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
        if (wa?.Cdx is null || string.IsNullOrEmpty(m.Order)) return null;
        var tag = wa.Cdx.Tag(m.Order!) ?? wa.Cdx.Tag(m.Order!.ToUpperInvariant());
        if (tag is null) return null;
        if (m.Ordered is null || m.OrderedFor != tag.Name)
        {
            var ordered = tag.EnumerateEntries().Select(e => (int)e.RecordNumber).ToList();
            // A SET ORDER … DESCENDING|ASCENDING override reverses the tag's own traversal direction.
            if (m.OrderReversed) ordered.Reverse();
            m.Ordered = ordered;
            m.OrderedFor = tag.Name;
            m.OrderPos = -1;
        }
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
        int rc = wa.Table.RecordCount;
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
        int rc = wa.Table.RecordCount;
        for (int i = rc; i >= 1; i--)
            if (Visible(wa, i)) { m.RecNo = i; m.Eof = false; m.Bof = false; return; }
        m.RecNo = rc + 1; m.Eof = true; m.Bof = true;
    }

    private void GoRecordCore(int area, int rec)
    {
        var m = Meta(area);
        var wa = Session.AreaAt(area);
        int rc = wa?.Table.RecordCount ?? 0;
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

        int rc = wa.Table.RecordCount;
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
    private void GoTop(int area) { GoTopCore(area); RepositionChildren(area); }
    private void GoBottom(int area) { GoBottomCore(area); RepositionChildren(area); }
    private void GoRecord(int area, int rec) { GoRecordCore(area, rec); RepositionChildren(area); }

    private void Skip(int area, int count)
    {
        SkipCore(area, count);
        ApplyOneToManyBound(area);   // SET SKIP: clamp a one-to-many child to its parent-key group.
        RepositionChildren(area);
    }

    private bool DoSeek(VfpValue key, int area, string? tag)
    {
        bool ok = DoSeekCore(key, area, tag);
        RepositionChildren(area);
        return ok;
    }

    private bool DoSeekCore(VfpValue key, int area, string? tag, bool relationSeek = false)
    {
        var m = Meta(area);
        var wa = Session.AreaAt(area);
        if (wa?.Cdx is null) { m.Found = false; return false; }
        var cdxTag = (tag is null ? (m.Order is null ? wa.Cdx.TagNames.FirstOrDefault() : m.Order) : tag);
        CdxTag? t = cdxTag is null ? null : (wa.Cdx.Tag(cdxTag) ?? wa.Cdx.Tag(cdxTag.ToUpperInvariant()));
        if (t is null) { m.Found = false; return false; }
        // Character keys seek by RAW BYTES (a prefix seek): this lets a SHORT value match a COMPOSITE
        // character tag (e.g. relate on `cust_id`, tag `cust_id+ord_id`) — the P1-gap-#2 prefix quirk —
        // which the value-typed Seek(object) cannot do (a composite expression resolves to KeyType.Unknown,
        // so its Encode returns null). A single-field character key is unaffected (same raw bytes).
        // The RELATION reposition seek honours SET EXACT (hackfox quirk 1): under EXACT ON a prefix-only
        // hit on a composite child key is NOT a match (→ child EOF), matching VFP9; the SEEK command path
        // keeps its always-prefix behaviour.
        uint? recno = (t.IsCharacterKey && key.Type == VfpType.Character)
            ? t.Seek(Encoding.Latin1.GetBytes(key.AsString).AsSpan(), relationSeek && _ctx.Exact)
            : t.Seek(key.ToClr() ?? string.Empty);
        m.OldVals = null;                 // a record move re-bases the OLDVAL() buffer (per-record buffering).
        if (recno is uint r && r >= 1 && r <= (uint)wa.Table.RecordCount)
        {
            m.RecNo = (int)r; m.Eof = false; m.Bof = false; m.Found = true; m.Cached = null;
            // Anchor the index position so a following SCAN WHILE / SKIP walks forward in index order.
            var ord = ActiveOrder(area);
            if (ord is not null) m.OrderPos = ord.IndexOf((int)r);
            return true;
        }
        m.RecNo = wa.Table.RecordCount + 1; m.Eof = true; m.Found = false; m.Cached = null;
        return false;
    }

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

    private object? ReadField(VfpSession.WorkArea wa, string field)
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
            case "RECCOUNT": r = VfpValue.Integer(AreaArg(a, 0)?.Table.RecordCount ?? 0); return true;
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
            case "CURSORGETPROP": r = VfpValue.Integer(1); return true;       // no buffering.
            case "GETFLDSTATE": r = VfpValue.Integer(1); return true;
            case "OLDVAL": r = FnOldVal(a); return true;
            case "CURVAL": r = a.Length > 0 ? EvalText(QualifiedExpr(a)) : VfpValue.Null; return true;
            case "MESSAGEBOX": r = VfpValue.Integer(6); return true;           // IDYES (never reached in targets).
            case "COCREATEGUID": r = FnCoCreateGuid(); return true;
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
        return m.Eof ? wa.Table.RecordCount + 1 : m.RecNo;
    }

    private bool FnDeleted(VfpValue[] a)
    {
        int area = a.Length == 0 ? Session.CurrentArea : AreaNumber(a[0]);
        var wa = Session.AreaAt(area);
        if (wa is null) return false;
        var m = Meta(area);
        return m.RecNo >= 1 && m.RecNo <= wa.Table.RecordCount && wa.Table.IsRecordDeleted(m.RecNo - 1);
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
            case 2007: return VfpValue.Character(Crc16Ccitt(a.Length > 1 ? a[1].AsString : string.Empty).ToString(CultureInfo.InvariantCulture));
            case 2015: return VfpValue.Character("_" + Guid.NewGuid().ToString("N")[..9].ToUpperInvariant());
            default: return VfpValue.Character(string.Empty);
        }
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

    private VfpValue FnCoCreateGuid()
    {
        var bytes = new byte[16];
        Random.Shared.NextBytes(bytes);
        Memory.Set("lcBuffer", VfpValue.Character(Encoding.Latin1.GetString(bytes)));
        return VfpValue.Integer(0);
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
        if (m.OldVals is not null && m.OldVals.TryGetValue(field, out var old))
            return VfpValue.FromClr(old);
        return EvalText(a.Length > 1 ? a[1].AsString + "." + field : field);
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

    private string QualifiedExpr(VfpValue[] a)
    {
        // OLDVAL/CURVAL(cField [,cAlias]) → with no buffering, simply the current value.
        string field = a[0].AsString;
        return a.Length > 1 ? a[1].AsString + "." + field : field;
    }

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
