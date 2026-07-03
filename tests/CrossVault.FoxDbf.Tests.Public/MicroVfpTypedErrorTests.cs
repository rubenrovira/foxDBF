using System;
using System.IO;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Project-review 5.3 — the microVFP error-number contract must be carried by TYPED numbers on the
/// exceptions our own code throws, NOT recovered by sniffing the English message text. These tests
/// provoke each OWN error class through the interpreter and assert the number surfaced via
/// <c>AERROR()</c>/<c>ERROR()</c> after an <c>ON ERROR</c> trap, plus a robustness test proving the
/// typed path wins over (and no longer depends on) the text heuristic — a reworded/misleading message
/// on an OWN exception still maps to its pinned number, while text sniffing only ever decides for a
/// FOREIGN exception.
///
/// The numbers are ORACLE-PINNED against the VFP9 runtime (the byte-exact cross-check lives in the
/// Internal project, which alone may launch the runtime): file/table-not-exist=1, CONTINUE-no-LOCATE=42,
/// invalid-session=1540, CANDIDATE/uniqueness=1884, ASORT/data-type-mismatch=9. Max-call-depth has NO
/// VFP-authoritative number (deep recursion faults the runtime uncatchably), so microVFP turns it into a
/// catchable error with a stable internal code.
///
/// SAFETY: every case runs on a fresh synthetic table in a throwaway temp dir (no fixtures, no customer
/// data, no runtime launch) — public-safe.
/// </summary>
public sealed class MicroVfpTypedErrorTests
{
    // ─────────────────────────── scaffolding ───────────────────────────

    private static VfpInterpreter Bare(out VfpSession s) { s = new VfpSession(); return new VfpInterpreter(s); }

    /// <summary>Install an <c>ON ERROR</c> trap that captures <c>ERROR()</c>, run <paramref name="provoke"/>
    /// (which must raise), and return the trapped VFP error number.</summary>
    private static int TrapErrorNumber(VfpInterpreter interp, string provoke)
    {
        interp.Execute("lnTrapErr = 0\nON ERROR lnTrapErr = ERROR()");
        interp.Execute(provoke);
        return (int)interp.EvalExpression("lnTrapErr").AsNumber;
    }

    /// <summary>Same, but reads the number back through <c>AERROR()[1]</c> (the RI/AERROR contract path).</summary>
    private static int TrapAerrorNumber(VfpInterpreter interp, string provoke)
    {
        interp.Execute("PUBLIC laTrap\nDIMENSION laTrap(1,7)\nlnRows = 0\nON ERROR lnRows = AERROR(laTrap)");
        interp.Execute(provoke);
        return (int)interp.EvalExpression("laTrap(1,1)").AsNumber;
    }

    /// <summary>A fresh synthetic table (with a duplicate id) opened in its own temp dir, for the
    /// CANDIDATE / USE cases that need a real on-disk table.</summary>
    private sealed class Bench : IDisposable
    {
        public string Dir { get; }
        public VfpSession Session { get; }
        public VfpInterpreter Interp { get; }

        public Bench()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_typederr_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            string dbf = Path.Combine(Dir, "people.dbf");
            using (var w = DbfWriter.Create(dbf, new[] { new DbfColumnDef("id", 'I'), new DbfColumnDef("name", 'C', 10) }))
            {
                w.AppendRecord(10, "alice");
                w.AppendRecord(10, "amy");     // duplicate id ⇒ a CANDIDATE tag is a uniqueness violation
                w.Flush();
            }
            Session = new VfpSession();
            Session.OpenDirectory(Dir);
            Interp = new VfpInterpreter(Session);
        }

        public void Dispose()
        {
            Session.Dispose();
            try { Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    // ─────────────────────────── (1) each OWN class carries its VFP number ───────────────────────────

    [Fact]
    public void Continue_WithoutLocate_MapsTo42()
    {
        var interp = Bare(out var s);
        using (s) Assert.Equal(42, TrapErrorNumber(interp, "CONTINUE"));
    }

    [Theory]
    [InlineData("SET DATASESSION TO 5")]
    [InlineData("SET DATASESSION TO 0")]
    public void InvalidSession_MapsTo1540(string provoke)
    {
        var interp = Bare(out var s);
        using (s) Assert.Equal(1540, TrapErrorNumber(interp, provoke));
    }

    [Fact]
    public void UseNonexistentTable_MapsTo1()
    {
        using var b = new Bench();
        Assert.Equal(1, TrapErrorNumber(b.Interp, "USE no_such_table_zzz IN 0"));
    }

    [Fact]
    public void CandidateDuplicate_MapsTo1884()
    {
        using var b = new Bench();
        // A CANDIDATE tag over a column that already holds a duplicate key is a uniqueness violation
        // (VFP error 1884) — currently mis-mapped to the generic fallback (1) because the heuristic has
        // no branch for it; the typed throw site fixes that.
        Assert.Equal(1884, TrapErrorNumber(b.Interp, "USE people\nINDEX ON id TAG c CANDIDATE"));
    }

    [Fact]
    public void CandidateDuplicate_SurfacesThroughAerrorColumn1()
    {
        using var b = new Bench();
        Assert.Equal(1884, TrapAerrorNumber(b.Interp, "USE people\nINDEX ON id TAG c CANDIDATE"));
    }

    [Fact]
    public void InsertUnknownColumn_MapsTo12()
    {
        // VFP treats an unknown name in the INSERT column-list as an undefined VARIABLE (err 12 "Variable
        // '...' is not found."), NOT an SQL column — oracle-pinned against the VFP9 runtime. The typed
        // throw site carries 12 so ErrorNumberOf never falls to the "does not exist" → 1 text heuristic.
        using var b = new Bench();
        var ex = Assert.Throws<FoxDbfSqlException>(
            () => b.Session.Execute("INSERT INTO people (nosuchcol) VALUES (1)"));
        Assert.Equal(12, ex.VfpErrorNumber);
        Assert.Equal(12, VfpInterpreter.ErrorNumberOf(ex));
    }

    [Fact]
    public void UpdateUnknownColumn_MapsTo1806()
    {
        // An unknown SET column is the SQL-column class (err 1806 "SQL: Column '...' is not found.") — the
        // same number SELECT reports for an unknown projected column, oracle-pinned against VFP9.
        using var b = new Bench();
        var ex = Assert.Throws<FoxDbfSqlException>(
            () => b.Session.Execute("UPDATE people SET nosuchcol = 1"));
        Assert.Equal(1806, ex.VfpErrorNumber);
        Assert.Equal(1806, VfpInterpreter.ErrorNumberOf(ex));
    }

    [Fact]
    public void AsortMixedTypes_MapsTo9()
    {
        var interp = Bare(out var s);
        using (s)
            Assert.Equal(9, TrapErrorNumber(interp,
                "DIMENSION laMix(2)\nlaMix(1) = 5\nlaMix(2) = 'text'\nlx = ASORT(laMix)"));
    }

    [Fact]
    public void MaxCallDepth_IsCatchableWithAStableNumber()
    {
        // VFP itself faults uncatchably on unbounded recursion (no oracle number exists); microVFP turns
        // it into a catchable runtime error carrying a STABLE internal code.
        var interp = MicroVfpTestSupport.NewFromSource("PROCEDURE rec\n  = rec()\nENDPROC", out var s);
        using (s)
        {
            int code = TrapErrorNumber(interp, "DO rec");
            Assert.Equal(VfpInterpreter.MaxCallDepthErrorNumber, code);
            Assert.True(code > 0, "the guard number must be a positive (non-zero) VFP-style error number");
        }
    }

    // ─────────────────────────── (2) robustness: typed wins, text is FOREIGN-only ───────────────────────────

    [Fact]
    public void TypedNumber_OnOwnException_WinsOverMisleadingText()
    {
        // A message engineered to hijack the text heuristic ("deadlock"→lock→109, "not found"→1): the
        // TYPED number on our OWN exception must win regardless of what the text says.
        const string misleading = "deadlock detected in column not found";
        Assert.Equal(1583, VfpInterpreter.ErrorNumberOf(new MicroVfpRuntimeException(misleading, 1583)));
        Assert.Equal(1539, VfpInterpreter.ErrorNumberOf(new FoxDbfException(misleading, 1539)));
        Assert.Equal(1, VfpInterpreter.ErrorNumberOf(new FoxDbfSqlException(misleading) { VfpErrorNumber = 1 }));
    }

    [Fact]
    public void ForeignException_FallsBackToText_WithoutHijackingAnOwnClass()
    {
        // The SAME misleading text on a FOREIGN (BCL) exception: text sniffing is the documented
        // last resort, so it produces SOME positive number — but it must NOT be allowed to decide an
        // OWN class the way the typed path does. The proof: the OWN typed exception yields its pinned
        // number while the foreign one does not.
        const string misleading = "deadlock detected in column not found";
        int foreign = VfpInterpreter.ErrorNumberOf(new Exception(misleading));
        int ownTyped = VfpInterpreter.ErrorNumberOf(new MicroVfpRuntimeException(misleading, 1583));

        Assert.True(foreign > 0, "a foreign exception still resolves to a positive fallback number (RI needs non-zero)");
        Assert.NotEqual(ownTyped, foreign);   // the typed OWN number is authoritative; text never reaches it
        Assert.Equal(1583, ownTyped);
    }

    [Fact]
    public void UntypedOwnException_StillUsesTextFallback_Unchanged()
    {
        // An OWN exception whose throw site did NOT pin a number keeps the legacy behaviour: it falls to
        // the last-resort text heuristic (here "does not exist" → 1), so nothing regresses.
        Assert.Equal(1, VfpInterpreter.ErrorNumberOf(new MicroVfpRuntimeException("File 'x' does not exist.")));
    }
}
