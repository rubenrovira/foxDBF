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

/// <summary>microVFP referential-integrity / trigger auto-fire plus the transaction snapshot-and-restore atomicity machinery.</summary>
public sealed partial class VfpInterpreter
{
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
            // Inside an ADO.NET transaction the captured TablePath is the private COPY; canonicalize it back to
            // the LIVE path so Use re-resolves the DBC member (long field names) and re-applies the read
            // redirect onto the copy. In autocommit TxLivePath is null → the path is used unchanged.
            string? reopenTarget = r.TablePath is { } tp && Session.TxLivePath is { } toLive ? toLive(tp) : r.TablePath;
            Session.Use(reopenTarget ?? r.Alias, r.Area, r.Alias, again: false, exclusive: r.Excl, noUpdate: r.NoUpd);
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

    /// <summary>
    /// Engage the ADO.NET COPY-ON-WRITE transaction seam for a table the interpreter is about to write to
    /// DIRECTLY (via its own short-lived <see cref="DbfWriter"/>), returning the path the writer must open.
    /// <para>
    /// Inside a <c>FoxDbfTransaction</c> this lazily takes the table's private working copy on FIRST write —
    /// the SAME <c>TxBeginWritePath</c> seam the raw DML path uses via <c>OpenWritableTarget</c> — and repoints
    /// every open work area on the live file at the copy (read-your-writes), then returns the COPY path. From
    /// then on the caller's <c>path</c> is the copy, so EVERY downstream file touch in the same op — the
    /// pre-image snapshots (<see cref="CaptureSnapshot"/> for a trigger-abort revert, <see cref="SnapshotForTxn"/>
    /// for a PRG <c>BEGIN TRANSACTION</c> frame), the <see cref="DbfWriter"/>, the read-your-writes
    /// <see cref="ReopenFileAreas"/>, and any <see cref="RestoreSnapshot"/> — all operate on the copy. The two
    /// snapshot layers therefore compose rather than fight: the interpreter's own transaction/trigger-abort
    /// reverts happen WITHIN the copy, and the outer COW transaction commits or discards the copy as a whole.
    /// </para>
    /// <para>
    /// In autocommit (no active transaction — including EnforceRules=off and every direct-interpreter test)
    /// the live path is returned unchanged and no area is reopened, so the write path is byte-identical.
    /// </para>
    /// </summary>
    private string BeginTxWrite(string path)
    {
        if (Session.TxBeginWritePath is null) return path;   // autocommit — unchanged.
        string writePath = Session.RedirectWritePath(path);  // live → private copy (idempotent for a copy path).
        if (!SamePath(writePath, path))
            ReopenFileAreas(path);                            // repoint the live-riding area(s) at the copy.
        return writePath;
    }

}
