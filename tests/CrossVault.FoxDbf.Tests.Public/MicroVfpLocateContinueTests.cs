using System;
using System.IO;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP project-review 5.2 — LOCATE / CONTINUE record-pointer positioning. Until this batch both
/// commands PARSED but were silent no-ops (the single most common VFP idiom, <c>LOCATE FOR … / IF
/// FOUND()</c>, ran without error yet never moved the pointer). These are PUBLIC-SAFE tests: every table
/// is a synthetic throwaway built in a fresh temp dir, so nothing committed is touched.
///
/// The expected RECNO()/FOUND()/EOF() at each step were pinned against the VFP9 runtime (a differential
/// harvest over the identical synthetic table); the internal oracle companion re-verifies the same
/// sequence live against the runtime over a TasTrade copy.
/// </summary>
public sealed class MicroVfpLocateContinueTests
{
    // The VFP9-verified 6-row fixture: n N(3) / c C(2).
    //   rec1 (10,"A")  rec2 (20,"B")  rec3 (20,"C")  rec4 (30,"A")  rec5 (20,"D")  rec6 (40,"B")
    private static void BuildMtx(string dbf)
    {
        var cols = new[] { new DbfColumnDef("n", 'N', 3), new DbfColumnDef("c", 'C', 2) };
        using var w = DbfWriter.Create(dbf, cols);
        w.AppendRecord(10, "A");
        w.AppendRecord(20, "B");
        w.AppendRecord(20, "C");
        w.AppendRecord(30, "A");
        w.AppendRecord(20, "D");
        w.AppendRecord(40, "B");
        w.Flush();
    }

    private static VfpInterpreter OpenMtx(MicroVfpTestSupport.TempDir dir, out VfpSession session)
    {
        BuildMtx(Path.Combine(dir.Path, "mtx.dbf"));
        session = new VfpSession();
        session.OpenDirectory(dir.Path);
        var interp = new VfpInterpreter(session);
        interp.Execute("USE mtx");
        return interp;
    }

    private static int Recno(VfpInterpreter i) => i.EvalExpression("RECNO()").AsInteger;
    private static bool Found(VfpInterpreter i) => i.EvalExpression("FOUND()").AsLogical;
    private static bool Eof(VfpInterpreter i) => i.EvalExpression("EOF()").AsLogical;

    private static void AssertAt(VfpInterpreter i, int recno, bool found, bool eof)
    {
        Assert.Equal(recno, Recno(i));
        Assert.Equal(found, Found(i));
        Assert.Equal(eof, Eof(i));
    }

    // ─────────────────────────── (1) basic LOCATE / FOUND / CONTINUE ───────────────────────────

    [Fact]
    public void Locate_For_PositionsOnFirstMatch_FoundTrue()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_basic");
        var i = OpenMtx(dir, out var s);
        using (s)
        {
            i.Execute("LOCATE FOR n = 20");
            AssertAt(i, recno: 2, found: true, eof: false);
        }
    }

    [Fact]
    public void Locate_For_NoMatch_GoesEof_FoundFalse()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_nomatch");
        var i = OpenMtx(dir, out var s);
        using (s)
        {
            i.Execute("LOCATE FOR n = 99");
            AssertAt(i, recno: 7, found: false, eof: true);   // reccount+1 = EOF
        }
    }

    [Fact]
    public void Locate_NoFor_PositionsOnFirstVisibleRecord()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_nofor");
        var i = OpenMtx(dir, out var s);
        using (s)
        {
            i.Execute("GO 4");            // move away first
            i.Execute("LOCATE");          // no FOR ⇒ first visible record
            AssertAt(i, recno: 1, found: true, eof: false);
        }
    }

    [Fact]
    public void Continue_WalksSuccessiveMatches_ThenEof()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_continue");
        var i = OpenMtx(dir, out var s);
        using (s)
        {
            i.Execute("LOCATE FOR n = 20");
            AssertAt(i, 2, true, false);
            i.Execute("CONTINUE");
            AssertAt(i, 3, true, false);
            i.Execute("CONTINUE");
            AssertAt(i, 5, true, false);
            i.Execute("CONTINUE");
            AssertAt(i, 7, false, true);   // walked off the end
            i.Execute("CONTINUE");         // CONTINUE at EOF is a no-op miss, NOT an error
            AssertAt(i, 7, false, true);
        }
    }

    [Fact]
    public void Continue_WithoutPriorLocate_Raises()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_nocont");
        var i = OpenMtx(dir, out var s);
        using (s)
        {
            var ex = Assert.Throws<MicroVfpRuntimeException>(() => i.Execute("CONTINUE"));
            // VFP9-pinned wording (error 42) — the internal oracle pins the exact number/message.
            Assert.Contains("LOCATE", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CONTINUE", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ─────────────────────────── (2) order / visibility / scope ───────────────────────────

    [Fact]
    public void Locate_SearchesInIndexOrder_NotPhysical()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_order");
        var i = OpenMtx(dir, out var s);
        using (s)
        {
            i.Execute("INDEX ON c TAG cc");
            i.Execute("SET ORDER TO cc");
            // c-order is rec1(A),rec4(A),rec2(B),rec6(B),rec3(C),rec5(D). First with n>=20 is rec4,
            // NOT the physical first match rec2.
            i.Execute("LOCATE FOR n >= 20");
            AssertAt(i, 4, true, false);
        }
    }

    [Fact]
    public void Continue_WalksInIndexOrder()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_order_cont");
        var i = OpenMtx(dir, out var s);
        using (s)
        {
            i.Execute("INDEX ON c TAG cc");
            i.Execute("SET ORDER TO cc");
            i.Execute("LOCATE FOR n >= 20");
            AssertAt(i, 4, true, false);   // rec4 (A,30)
            i.Execute("CONTINUE");
            AssertAt(i, 2, true, false);   // rec2 (B,20)
            i.Execute("CONTINUE");
            AssertAt(i, 6, true, false);   // rec6 (B,40)
            i.Execute("CONTINUE");
            AssertAt(i, 3, true, false);   // rec3 (C,20)
            i.Execute("CONTINUE");
            AssertAt(i, 5, true, false);   // rec5 (D,20)
            i.Execute("CONTINUE");
            AssertAt(i, 7, false, true);   // EOF
        }
    }

    [Fact]
    public void Locate_SetDeletedOn_SkipsDeletedRecords()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_deleted");
        var i = OpenMtx(dir, out var s);
        using (s)
        {
            i.Execute("SET DELETED ON");
            i.Execute("GO 2");
            i.Execute("DELETE");            // hide rec2 (the first physical n=20)
            i.Execute("GO TOP");
            i.Execute("LOCATE FOR n = 20"); // must skip the deleted rec2 → rec3
            AssertAt(i, 3, true, false);
        }
    }

    [Fact]
    public void Locate_NextScope_BoundsTheSearchWindow()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_next");
        var i = OpenMtx(dir, out var s);
        using (s)
        {
            i.Execute("GO 2");
            i.Execute("LOCATE NEXT 2 FOR c = 'C'");    // window {rec2,rec3}; rec3 matches
            AssertAt(i, 3, true, false);

            i.Execute("GO 2");
            i.Execute("LOCATE NEXT 2 FOR c = 'D'");    // window {rec2,rec3}; no D → not found, parked on last examined
            AssertAt(i, 3, false, false);              // VFP9: stays on the last window record, NOT EOF
        }
    }

    [Fact]
    public void Locate_RestScope_SearchesFromCurrentRecord()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_rest");
        var i = OpenMtx(dir, out var s);
        using (s)
        {
            i.Execute("GO 3");
            i.Execute("LOCATE REST FOR n = 20");   // rec3 itself matches (REST includes current)
            AssertAt(i, 3, true, false);

            i.Execute("GO 4");
            i.Execute("LOCATE REST FOR n = 20");   // rec4(30) skip, rec5(20) match
            AssertAt(i, 5, true, false);
        }
    }

    [Fact]
    public void Locate_RecordScope_ChecksExactlyThatRecord()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_record");
        var i = OpenMtx(dir, out var s);
        using (s)
        {
            i.Execute("GO 1");
            i.Execute("LOCATE RECORD 4 FOR n = 30");   // rec4 matches
            AssertAt(i, 4, true, false);

            i.Execute("GO 1");
            i.Execute("LOCATE RECORD 4 FOR n = 99");   // rec4 fails FOR → parked on rec4, not found, not EOF
            AssertAt(i, 4, false, false);
        }
    }

    [Fact]
    public void Locate_While_StopsAtFirstNonMatchingRow()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_while");
        var i = OpenMtx(dir, out var s);
        using (s)
        {
            i.Execute("INDEX ON c TAG cc");
            i.Execute("SET ORDER TO cc");   // c-order: rec1(A),rec4(A),rec2(B),...

            i.Execute("GO TOP");
            i.Execute("LOCATE FOR n = 30 WHILE c = 'A'");   // rec1(A,10) no, rec4(A,30) yes
            AssertAt(i, 4, true, false);

            i.Execute("GO TOP");
            i.Execute("LOCATE FOR n = 40 WHILE c = 'A'");   // rec1,rec4 (A) fail FOR, rec2 (B) fails WHILE → stop
            AssertAt(i, 2, false, false);                    // VFP9: parked on the row that broke WHILE, not EOF
        }
    }

    // ─────────────────────────── (3) pointer-move side effects ───────────────────────────

    [Fact]
    public void LocateAndContinue_RepositionSetRelationChildren()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_relation");
        // Parent cust: cid C(2) / n N(3).  Child ord: oid C(2) / cid C(2), indexed on cid.
        var cust = new[] { new DbfColumnDef("cid", 'C', 2), new DbfColumnDef("n", 'N', 3) };
        using (var w = DbfWriter.Create(Path.Combine(dir.Path, "cust.dbf"), cust))
        { w.AppendRecord("A", 10); w.AppendRecord("B", 20); w.AppendRecord("C", 20); w.Flush(); }
        var ord = new[] { new DbfColumnDef("oid", 'C', 2), new DbfColumnDef("cid", 'C', 2) };
        using (var w = DbfWriter.Create(Path.Combine(dir.Path, "ord.dbf"), ord))
        { w.AppendRecord("1", "A"); w.AppendRecord("2", "B"); w.AppendRecord("3", "C"); w.Flush(); }

        using var s = new VfpSession();
        s.OpenDirectory(dir.Path);
        var i = new VfpInterpreter(s);
        i.Execute("SELECT 0\nUSE ord\nINDEX ON cid TAG cc\nSET ORDER TO cc");
        i.Execute("SELECT 0\nUSE cust");
        i.Execute("SET RELATION TO cid INTO ord");

        i.Execute("LOCATE FOR n = 20");                       // parent → rec2 (cid 'B')
        Assert.Equal(2, i.EvalExpression("RECNO('cust')").AsInteger);
        Assert.Equal(2, i.EvalExpression("RECNO('ord')").AsInteger);   // child re-seeked to cid 'B'

        i.Execute("CONTINUE");                                // parent → rec3 (cid 'C')
        Assert.Equal(3, i.EvalExpression("RECNO('cust')").AsInteger);
        Assert.Equal(3, i.EvalExpression("RECNO('ord')").AsInteger);   // child re-seeked to cid 'C'
    }

    [Fact]
    public void Locate_UnderRowBuffering_AutoCommitsPendingEditOnMove()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_buffer");
        var cols = new[]
        {
            new DbfColumnDef("id", 'I'),
            new DbfColumnDef("name", 'C', 10),
            new DbfColumnDef("city", 'C', 10),
        };
        using (var w = DbfWriter.Create(Path.Combine(dir.Path, "people.dbf"), cols))
        { w.AppendRecord(1, "Ann", "berlin"); w.AppendRecord(2, "Bob", "aachen"); w.AppendRecord(3, "Cy", "kassel"); w.Flush(); }

        using var s = new VfpSession();
        s.OpenDirectory(dir.Path);
        var i = new VfpInterpreter(s);
        i.Execute("USE people");
        i.Execute("=CURSORSETPROP('Buffering', 3)");
        i.Execute("GO 1");
        i.Execute("REPLACE city WITH 'moved'");
        // Row buffering: the edit is pending; the ON-DISK value is still the original.
        Assert.Equal("berlin", i.EvalExpression("CURVAL('city')").AsString.TrimEnd());

        i.Execute("LOCATE FOR id = 3");   // a pointer MOVE ⇒ implicit TABLEUPDATE of the pending rec1 edit
        Assert.Equal(3, Recno(i));

        i.Execute("GO 1");
        Assert.Equal("moved", i.EvalExpression("CURVAL('city')").AsString.TrimEnd());   // committed to disk by the move
    }

    [Fact]
    public void Locate_State_IsPerWorkArea_Independent()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_perarea");
        BuildMtx(Path.Combine(dir.Path, "ta.dbf"));
        BuildMtx(Path.Combine(dir.Path, "tb.dbf"));

        using var s = new VfpSession();
        s.OpenDirectory(dir.Path);
        var i = new VfpInterpreter(s);
        i.Execute("SELECT 0\nUSE ta");
        i.Execute("SELECT 0\nUSE tb");

        i.Execute("SELECT ta\nLOCATE FOR n = 20");   // ta → rec2
        Assert.Equal(2, i.EvalExpression("RECNO('ta')").AsInteger);

        i.Execute("SELECT tb\nLOCATE FOR n = 40");   // tb → rec6 (a DIFFERENT FOR predicate)
        Assert.Equal(6, i.EvalExpression("RECNO('tb')").AsInteger);

        // ta's CONTINUE must resume ta's OWN LOCATE (FOR n=20 → rec3), not tb's (FOR n=40).
        i.Execute("SELECT ta\nCONTINUE");
        Assert.Equal(3, i.EvalExpression("RECNO('ta')").AsInteger);
        Assert.True(i.EvalExpression("FOUND('ta')").AsLogical);
    }

    // ─────────────────────────── (4) MUST-FIX 5.2 — WHILE-no-scope defaults to REST ───────────────────────────

    // A WHILE clause with NO explicit scope keyword defaults to REST: the search starts at the CURRENT
    // record, not the top. n=10 lives only at rec1, so from rec3 onward (WHILE .T. never stops) the walk
    // runs off the end. VFP9-verified (GO 3 / LOCATE FOR n=10 WHILE .T. → RECNO()=7, FOUND()=.F., EOF()).
    [Fact]
    public void Locate_While_NoScope_DefaultsToRest_FromCurrentRecord()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_while_rest");
        var i = OpenMtx(dir, out var s);
        using (s)
        {
            i.Execute("GO 3");
            i.Execute("LOCATE FOR n = 10 WHILE .T.");   // REST-from-rec3: n=10 (rec1) is behind us ⇒ EOF
            AssertAt(i, recno: 7, found: false, eof: true);
        }
    }

    // The explicit-ALL control: an explicit ALL scope is NOT overridden by the WHILE default — it restarts
    // from the top and finds rec1. VFP9-verified (GO 3 / LOCATE ALL FOR n=10 WHILE .T. → RECNO()=1, .T.).
    [Fact]
    public void Locate_While_ExplicitAllScope_RestartsFromTop()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_while_all");
        var i = OpenMtx(dir, out var s);
        using (s)
        {
            i.Execute("GO 3");
            i.Execute("LOCATE ALL FOR n = 10 WHILE .T.");   // ALL wins over the WHILE default ⇒ top ⇒ rec1
            AssertAt(i, recno: 1, found: true, eof: false);
        }
    }

    // ───────────────────────── (5) MUST-FIX 5.2 — CONTINUE past an exhausted bounded scope ─────────────────────────

    // CONTINUE after a fully-counted NEXT window that found nothing leaves the pointer COMPLETELY UNCHANGED
    // (only FOUND() stays .F.) — it does NOT walk to EOF. VFP9-verified (GO 2 / LOCATE NEXT 2 FOR c='D' →
    // rec3/.F.; CONTINUE → still rec3/.F./EOF()=.F.).
    [Fact]
    public void Continue_AfterExhaustedNextWindow_LeavesPointerUnchanged()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_cont_next");
        var i = OpenMtx(dir, out var s);
        using (s)
        {
            i.Execute("GO 2");
            i.Execute("LOCATE NEXT 2 FOR c = 'D'");    // window {rec2,rec3}; no D ⇒ parked on rec3
            AssertAt(i, recno: 3, found: false, eof: false);
            i.Execute("CONTINUE");                     // window already exhausted ⇒ pointer parked, NOT EOF
            AssertAt(i, recno: 3, found: false, eof: false);
        }
    }

    // Same divergence for a RECORD-scope miss: CONTINUE (repeatedly) keeps the pointer on the record, never
    // advancing to EOF. VFP9-verified (GO 1 / LOCATE RECORD 4 FOR n=99 → rec4/.F.; CONTINUE ×2 → rec4/.F.).
    [Fact]
    public void Continue_AfterRecordScopeMiss_StaysParkedNotEof()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_cont_record");
        var i = OpenMtx(dir, out var s);
        using (s)
        {
            i.Execute("GO 1");
            i.Execute("LOCATE RECORD 4 FOR n = 99");   // rec4 fails FOR ⇒ parked on rec4, not found
            AssertAt(i, recno: 4, found: false, eof: false);
            i.Execute("CONTINUE");                     // single-record window exhausted ⇒ stays on rec4
            AssertAt(i, recno: 4, found: false, eof: false);
            i.Execute("CONTINUE");                     // still parked — never EOF
            AssertAt(i, recno: 4, found: false, eof: false);
        }
    }

    // ─────────────────────── (6) MUST-FIX 5.2 — NOOPTIMIZE + scope-after-FOR grammar ───────────────────────

    // `LOCATE FOR <expr> NOOPTIMIZE` — the trailing optimizer hint must be stripped (not folded into the FOR
    // expression, which previously made the predicate a silent never-matching Null). VFP9-verified: finds
    // the first c='B' (rec2).
    [Fact]
    public void Locate_For_NoOptimize_FindsFirstMatch()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_noopt");
        var i = OpenMtx(dir, out var s);
        using (s)
        {
            i.Execute("LOCATE FOR c = 'B' NOOPTIMIZE");
            AssertAt(i, recno: 2, found: true, eof: false);
        }
    }

    // VFP also accepts the Scope AFTER the FOR clause. `LOCATE FOR n=20 NEXT 3` from rec1 bounds the search
    // to {rec1,rec2,rec3}; the first n=20 is rec2. VFP9-verified.
    [Fact]
    public void Locate_For_ScopeAfterFor_NextWindow_Match()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_scopeafter");
        var i = OpenMtx(dir, out var s);
        using (s)
        {
            i.Execute("GO 1");
            i.Execute("LOCATE FOR n = 20 NEXT 3");     // window {rec1,rec2,rec3}; first n=20 ⇒ rec2
            AssertAt(i, recno: 2, found: true, eof: false);
        }
    }

    // Scope-after-FOR whose window holds no match parks on the last window record (same as leading NEXT).
    // VFP9-verified (GO 2 / LOCATE FOR c='D' NEXT 2 → rec3/.F./EOF()=.F.).
    [Fact]
    public void Locate_For_ScopeAfterFor_NextWindow_MissParks()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_scopeafter_miss");
        var i = OpenMtx(dir, out var s);
        using (s)
        {
            i.Execute("GO 2");
            i.Execute("LOCATE FOR c = 'D' NEXT 2");    // window {rec2,rec3}; no D ⇒ parked on rec3
            AssertAt(i, recno: 3, found: false, eof: false);
        }
    }

    // NOOPTIMIZE trailing a WHILE body (with the WHILE-no-scope REST default in force) must also be stripped.
    // VFP9-verified (GO 1 / LOCATE FOR n=20 WHILE .T. NOOPTIMIZE → rec2/.T.).
    [Fact]
    public void Locate_For_While_NoOptimize_Combined()
    {
        using var dir = new MicroVfpTestSupport.TempDir("loc_while_noopt");
        var i = OpenMtx(dir, out var s);
        using (s)
        {
            i.Execute("GO 1");
            i.Execute("LOCATE FOR n = 20 WHILE .T. NOOPTIMIZE");
            AssertAt(i, recno: 2, found: true, eof: false);
        }
    }
}
