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

/// <summary>microVFP record navigation — GO/SKIP/SEEK/LOCATE/CONTINUE/SCAN traversal, SET KEY visibility, and the record-pointer model (AreaMeta).</summary>
public sealed partial class VfpInterpreter
{
    // ─────────────────────────── LOCATE / CONTINUE (project-review 5.2) ───────────────────────────
    //
    // LOCATE positions on the FIRST record — in the area's CURRENT order (the master index if one is set,
    // else physical) — that satisfies FOR within the scope/WHILE window, honouring SET DELETED /
    // SET KEY visibility through the SAME Visible() gate GO/SKIP use (SET FILTER is not modelled). Found ⇒ pointer on the record,
    // FOUND()=.T.; the search runs off the end ⇒ EOF(), FOUND()=.F. LOCATE with no FOR positions on the
    // first visible record. CONTINUE resumes the LAST LOCATE of the CURRENT work area from the record AFTER
    // the current one, reusing its remembered FOR/WHILE + scope window (per AreaMeta); CONTINUE with no
    // prior LOCATE raises VFP error 42. Both are ONE pointer move and fire the GO/SKIP side effects:
    // MaybeAutoCommitRow before leaving the row, RepositionChildren after landing.

    private enum LocScope { All, Rest, Next, Record }

    private void ExecLocate(LocateStmt loc)
    {
        int area = Session.CurrentArea;
        if (Session.AreaAt(area) is null) return;     // no table open ⇒ nothing to search.
        var m = Meta(area);

        var (kind, count) = ResolveLocScope(loc.Scope);
        // A WHILE with NO explicit scope keyword defaults to REST — the search starts at the CURRENT record,
        // not the top of the table (the canonical `SEEK key` / `LOCATE … WHILE key=…` key-group idiom relies
        // on this; a top restart would discard the SEEK). Mirrors ExecScan's `fromCurrent` rule. Guard on the
        // RAW parsed scope (loc.Scope) so an explicit ALL is never overridden. VFP9-verified.
        if (loc.Scope is null && loc.While is not null) kind = LocScope.Rest;

        MaybeAutoCommitRow(area);                     // leaving the current row commits its pending edit (mode 2/3).
        var wa = Session.AreaAt(area);                // MUST re-fetch: a commit may reopen the file areas.
        if (wa is null) return;

        // Remember this LOCATE so a following CONTINUE can resume it (per work area).
        m.LocateActive = true;
        m.LocateFor = loc.For;
        m.LocateWhile = loc.While;

        if (kind == LocScope.Record)
        {
            m.LocateWindow = new HashSet<int> { count };
            m.LocateWindowFull = true;                // single-record window is "full" (exhaust ⇒ park on it).
            LocateOnRecord(area, wa, m, count, loc.For, loc.While);
            RepositionChildren(area);
            return;
        }

        var order = TraversalOrder(area, wa);         // recnos in the CURRENT order (index order or physical).
        int startIdx = kind == LocScope.All ? 0 : CurrentIndex(order, m);   // REST/NEXT start at the current record.
        if (startIdx < 0) startIdx = order.Count;     // current at EOF/BOF ⇒ REST/NEXT window is empty.

        List<int> candidates;
        bool unbounded;
        if (kind == LocScope.Next)
        {
            var (window, full) = BuildNextWindow(order, wa, startIdx, count);
            m.LocateWindow = new HashSet<int>(window);
            m.LocateWindowFull = full;
            candidates = window;
            unbounded = false;
        }
        else                                          // ALL / REST — unbounded to the end of the order.
        {
            m.LocateWindow = null;
            m.LocateWindowFull = false;
            candidates = SubListFrom(order, startIdx);
            unbounded = true;
        }

        RunLocateScan(area, wa, m, candidates, loc.For, loc.While, unbounded, m.LocateWindowFull);
        RepositionChildren(area);
    }

    private void ExecContinue()
    {
        int area = Session.CurrentArea;
        var m = Meta(area);
        if (!m.LocateActive)
            throw new MicroVfpRuntimeException("The LOCATE command must be issued before the CONTINUE command.", 42);

        MaybeAutoCommitRow(area);
        var wa = Session.AreaAt(area);                // MUST re-fetch: a commit may reopen the file areas.
        if (wa is null) return;

        var order = TraversalOrder(area, wa);
        int curIdx = CurrentIndex(order, m);
        int from = (m.Eof || curIdx < 0) ? order.Count : curIdx + 1;   // resume AFTER the current record.

        List<int> candidates = SubListFrom(order, from);
        bool unbounded = m.LocateWindow is null;
        if (!unbounded) candidates = candidates.Where(m.LocateWindow!.Contains).ToList();   // stay inside the NEXT/RECORD window.

        RunLocateScan(area, wa, m, candidates, m.LocateFor, m.LocateWhile, unbounded, m.LocateWindowFull);
        RepositionChildren(area);
    }

    /// <summary>Parse the LOCATE scope head (null/"ALL"/"REST"/"NEXT n"/"RECORD n"); the count expression is
    /// evaluated in the current context (VFP allows an expression, e.g. <c>NEXT lnCount</c>).</summary>
    private (LocScope Kind, int Count) ResolveLocScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return (LocScope.All, 0);
        string s = scope.Trim();
        int sp = s.IndexOf(' ');
        string head = (sp < 0 ? s : s.Substring(0, sp)).ToUpperInvariant();
        string rest = sp < 0 ? string.Empty : s.Substring(sp + 1).Trim();
        return head switch
        {
            "ALL" => (LocScope.All, 0),
            "REST" => (LocScope.Rest, 0),
            "NEXT" => (LocScope.Next, ScopeCount(rest)),
            "RECORD" => (LocScope.Record, ScopeCount(rest)),
            _ => (LocScope.All, 0),                    // unrecognised head ⇒ ALL (VFP's default scope).
        };

        int ScopeCount(string expr)
        {
            try { return (int)Eval(PrgExpr.Parse(expr)).AsNumber; } catch { return 0; }
        }
    }

    /// <summary>The recnos of <paramref name="area"/> in its CURRENT traversal order — the active index
    /// sequence (<see cref="ActiveOrder"/>) or, with no controlling order, physical 1..count.</summary>
    private List<int> TraversalOrder(int area, VfpSession.WorkArea wa)
    {
        var ord = ActiveOrder(area);
        if (ord is not null) return ord;
        int rc = EffCount(area, wa);
        var list = new List<int>(rc);
        for (int i = 1; i <= rc; i++) list.Add(i);
        return list;
    }

    /// <summary>The index of the CURRENT record within <paramref name="order"/>, or -1 when the pointer is
    /// at EOF/BOF (or on a record absent from the active index).</summary>
    private static int CurrentIndex(List<int> order, AreaMeta m) => order.IndexOf(m.RecNo);

    private static List<int> SubListFrom(List<int> order, int from)
        => from >= order.Count ? new List<int>() : order.GetRange(from, order.Count - from);

    /// <summary>The first <paramref name="count"/> VISIBLE recnos of the NEXT window, walking <paramref
    /// name="order"/> from <paramref name="startIdx"/>, plus whether the window reached its full count (a
    /// full window that finds nothing parks on its last record; a window truncated by EOF goes to EOF).</summary>
    private (List<int> Window, bool Full) BuildNextWindow(List<int> order, VfpSession.WorkArea wa, int startIdx, int count)
    {
        var w = new List<int>();
        for (int idx = Math.Max(0, startIdx); idx < order.Count && w.Count < count; idx++)
            if (Visible(wa, order[idx])) w.Add(order[idx]);
        return (w, count > 0 && w.Count == count);
    }

    /// <summary>Walk <paramref name="candidates"/> (already in order, already the right scope subset) for the
    /// first record satisfying FOR, honouring WHILE (stops on the first .F., parking there) and visibility.
    /// No match: an <paramref name="unbounded"/> (ALL/REST) walk parks at EOF; a full bounded (NEXT/RECORD)
    /// window leaves the pointer where it is — on the FIRST LOCATE that is its last examined record, on a
    /// CONTINUE whose remaining slice is already empty it stays put (only FOUND() clears) (VFP9-verified).</summary>
    private void RunLocateScan(int area, VfpSession.WorkArea wa, AreaMeta m, List<int> candidates,
        PrgExpr? forExpr, PrgExpr? whileExpr, bool unbounded, bool windowFull)
    {
        var ord = ActiveOrder(area);
        foreach (int rec in candidates)
        {
            if (!Visible(wa, rec)) continue;
            PositionPointer(m, ord, rec);
            if (whileExpr is not null && !Truth(Eval(whileExpr))) { m.Found = false; return; }  // WHILE .F. ⇒ stop here.
            if (forExpr is null || Truth(Eval(forExpr))) { m.Found = true; return; }             // match ⇒ done.
        }
        // No match after the walk.
        if (!unbounded && windowFull)
        {
            // A fully-counted bounded window (NEXT n / RECORD n) that finds nothing does NOT move to EOF: the
            // pointer stays where it is (VFP9-verified). On the FIRST LOCATE a full window always examined a
            // record, so the pointer is parked on that last record; on a CONTINUE whose remaining window slice
            // is already empty (lastExamined == -1) the pointer is simply left untouched — RecNo/Bof/Eof below
            // are skipped and only FOUND() clears.
            m.Found = false;
            return;
        }
        m.RecNo = wa.Table.RecordCount + 1; m.Eof = true; m.Bof = false; m.Found = false;
        m.Cached = null; m.OldVals = null;
        if (ord is not null) m.OrderPos = ord.Count;
    }

    /// <summary>LOCATE RECORD n — GO n (explicit; no visibility skip), then evaluate WHILE/FOR on it; the
    /// pointer stays on record n whether or not it matches (VFP9-verified). Out-of-range n ⇒ EOF/not found.</summary>
    private void LocateOnRecord(int area, VfpSession.WorkArea wa, AreaMeta m, int rec, PrgExpr? forExpr, PrgExpr? whileExpr)
    {
        var ord = ActiveOrder(area);
        if (rec < 1 || rec > wa.Table.RecordCount)
        {
            m.RecNo = wa.Table.RecordCount + 1; m.Eof = true; m.Bof = false; m.Found = false;
            m.Cached = null; m.OldVals = null;
            if (ord is not null) m.OrderPos = ord.Count;
            return;
        }
        PositionPointer(m, ord, rec);
        if (whileExpr is not null && !Truth(Eval(whileExpr))) { m.Found = false; return; }
        m.Found = forExpr is null || Truth(Eval(forExpr));
    }

    /// <summary>Position the pointer on <paramref name="rec"/> for FOR/WHILE evaluation (re-basing the record
    /// cache + OLDVAL buffer + index position), the SAME state a GO would set — but WITHOUT the GO/SKIP side
    /// effects, which LOCATE fires ONCE for its whole scan.</summary>
    private static void PositionPointer(AreaMeta m, List<int>? ord, int rec)
    {
        m.RecNo = rec; m.Eof = false; m.Bof = false; m.Cached = null; m.OldVals = null;
        if (ord is not null) m.OrderPos = ord.IndexOf(rec);
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
        public string? OrderedKeyExpr; // the controlling order's KEY expression (5.5: tag-scoped cache refresh).
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

        // ── LOCATE / CONTINUE (project-review 5.2) — the last LOCATE issued in THIS area, remembered so a
        // following CONTINUE resumes the SAME search (same FOR/WHILE + scope window) from the current
        // position instead of restarting. Per work area. ──
        public bool LocateActive;          // a LOCATE has run in this area ⇒ CONTINUE is legal (else VFP err 42).
        public PrgExpr? LocateFor;         // the remembered FOR predicate (null ⇒ match the first visible record).
        public PrgExpr? LocateWhile;       // the remembered WHILE predicate (stops the walk at the first .F.).
        public HashSet<int>? LocateWindow; // bounded scope (NEXT/RECORD) candidate recnos; null ⇒ ALL/REST (to end).
        public bool LocateWindowFull;      // a NEXT window that reached its full count (exhaust ⇒ park, not EOF).

        // ── multi-index model (microVFP INDEX/ORDER MODEL) ──
        // Non-structural index files opened via SET INDEX TO / USE … INDEX, in OPEN order (full paths).
        // The structural .cdx (auto-opened on USE) is held by WorkArea.Cdx and is NOT listed here.
        public List<string>? ExtraIndexes;
    }

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
        // Remember the controlling key expression so a later REPLACE can decide (tag-scoped, 5.5) whether it
        // touched the order key — a CDX tag exposes it; a standalone .idx does not (null ⇒ drop on any REPLACE).
        m.OrderedKeyExpr = src.CdxTag?.KeyExpression?.Trim();
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

}
