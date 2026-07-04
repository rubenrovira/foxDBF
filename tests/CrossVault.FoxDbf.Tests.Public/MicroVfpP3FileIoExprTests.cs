using System;
using System.IO;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP P3 — the two expression-level singletons that ride alongside the low-level file family:
/// <c>ICASE()</c> (MICROVFP_EXTENSIONS_BACKLOG.md §C.3) and <c>DBUSED()</c> (§C.5). Values PINNED to the
/// VFP9 runtime (probed authoritatively; see the Internal oracle test). RED until implemented.
///
/// ICASE has NO hackfox entry — its edges were pinned empirically: fully LAZY (neither unmatched
/// conditions nor unmatched value expressions are evaluated), first-true wins, an odd trailing default,
/// and — the surprise — NO match with NO default returns <c>.NULL.</c> (type "X"), NOT <c>.F.</c>.
/// DBUSED replicates the documented VFP9 bug where a name WITH extension but no path resolves against the
/// current directory (⇒ <c>.F.</c> when the DBC lives elsewhere), while a bare name matches by base name.
/// </summary>
public sealed class MicroVfpP3FileIoExprTests
{
    private sealed class H : IDisposable
    {
        public VfpSession Session { get; }
        public VfpInterpreter Interp { get; }
        public H()
        {
            Session = new VfpSession();
            Interp = new VfpInterpreter(Session);
        }
        public void Run(string prg) => Interp.Execute(prg);
        public string Str(string e) => Interp.EvalExpression(e).AsString;
        public int Int(string e) => Interp.EvalExpression(e).AsInteger;
        public bool Bool(string e) => Interp.EvalExpression(e).AsLogical;
        public bool IsNull(string e) => Interp.EvalExpression(e).IsNull;
        public void Dispose() { try { Session.Dispose(); } catch { } }
    }

    // ─────────────────────────── ICASE core matrix ───────────────────────────

    [Fact]
    public void ICase_First_True_Pair_Wins()
    {
        using var h = new H();
        Assert.Equal(20, h.Int("ICASE(.F., 10, .T., 20, .F., 30, 99)"));
        Assert.Equal(20, h.Int("ICASE(.F., 10, .T., 20)"));
        // Mixed value types: the selected pair's value is returned verbatim.
        Assert.Equal("str", h.Str("ICASE(.T., 'str', 99)"));
    }

    [Fact]
    public void ICase_Uses_The_Odd_Trailing_Default_When_No_Condition_Matches()
    {
        using var h = new H();
        Assert.Equal(99, h.Int("ICASE(.F., 10, .F., 20, 99)"));
    }

    [Fact]
    public void ICase_No_Match_And_No_Default_Returns_Null_Not_False()
    {
        using var h = new H();
        Assert.True(h.IsNull("ICASE(.F., 10, .F., 20)"));
        Assert.True(h.IsNull("ICASE(.F., 10)"));
    }

    // ─────────────────────────── ICASE laziness (short-circuit; unmatched branches NOT evaluated) ───────────────────────────

    [Fact]
    public void ICase_Is_Fully_Lazy_Unmatched_Conditions_And_Values_Are_Not_Evaluated()
    {
        using var h = new H();
        h.Run(
            "PUBLIC gnc\n" +
            "gnc = 0\n" +
            "\n" +
            "FUNCTION bump\n" +
            "gnc = gnc + 1\n" +
            "RETURN 99");

        // 1st condition true ⇒ the SECOND condition expression bump() is never evaluated.
        h.Run("gnc = 0");
        Assert.Equal(1, h.Int("ICASE(.T., 1, bump(), 2)"));
        Assert.Equal(0, h.Int("gnc"));

        // 1st condition false ⇒ its VALUE expression bump() is never evaluated.
        h.Run("gnc = 0");
        Assert.Equal(7, h.Int("ICASE(.F., bump(), .T., 7)"));
        Assert.Equal(0, h.Int("gnc"));

        // 1st condition true ⇒ later pair's value bump() is never evaluated.
        h.Run("gnc = 0");
        Assert.Equal(100, h.Int("ICASE(.T., 100, .T., bump())"));
        Assert.Equal(0, h.Int("gnc"));
    }

    // ─────────────────────────── DBUSED ───────────────────────────

    [Fact]
    public void DbUsed_Matches_A_Bare_Name_By_Base_Name_And_A_Full_Path_Exactly()
    {
        using var dir = new MicroVfpTestSupport.TempDir("p3fileio_dbused");
        string db = MicroVfpTestSupport.CopyDatabase(
            Path.Combine(Fixtures.RepoRoot, "Tastrade_VFPData"), dir);
        string dbc = Path.Combine(db, "tastrade.dbc");

        using var h = new H();
        Assert.False(h.Bool("DBUSED('tastrade')"));          // nothing open yet
        h.Session.OpenDatabase(dbc);

        Assert.True(h.Bool("DBUSED('tastrade')"));            // bare name → base-name match
        Assert.True(h.Bool("DBUSED('TASTRADE')"));            // case-insensitive
        Assert.True(h.Bool($"DBUSED('{dbc}')"));              // full path → exact
        Assert.True(h.Bool("DBUSED(DBC())"));                 // self-test via the current DBC path
        Assert.False(h.Bool("DBUSED('nonexistent')"));       // unknown name

        // Documented VFP9 bug (hackfox s4g422), replicated on purpose: a name WITH extension but no path is
        // resolved against the current directory — since the DBC lives in the temp copy (never the process
        // CWD), the qualified path mismatches the open DBC and DBUSED returns .F. even though it IS open.
        Assert.False(h.Bool("DBUSED('tastrade.dbc')"));
    }
}
