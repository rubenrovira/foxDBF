using System;
using System.IO;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP P2 table/record batch — part (1): the record↔array/MEMVAR movers (GATHER / SCATTER), the
/// table-header metadata functions (HEADER() / LUPDATE()), the SET NULL nullability gate, and the
/// FLUSH / SET AUTOSAVE persistence commands.
///
/// Written TESTS-FIRST: they pin the desired VFP9 contract and are RED until the interpreter is wired to
/// the existing REPLACE field-write path (GATHER/SCATTER), the DbfTable header accessors (HEADER/LUPDATE),
/// the DDL NULL-ability plumbing (SET NULL) and the open-writer flush (FLUSH / AUTOSAVE).
///
/// SAFETY: every case builds a FRESH synthetic table in its own throwaway temp dir (never a committed
/// fixture); the dir is removed on dispose. Public-safe — synthetic data only, no VFP9 oracle.
/// </summary>
public sealed class MicroVfpGatherScatterTableRecordTests
{
    private sealed class Bench : IDisposable
    {
        public string Dir { get; }
        public string DbfPath { get; }
        public VfpSession Session { get; }
        public VfpInterpreter Interp { get; }

        public Bench()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_gs_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            DbfPath = Path.Combine(Dir, "people.dbf");

            var cols = new[]
            {
                new DbfColumnDef("id", 'I'),
                new DbfColumnDef("name", 'C', 10),
                new DbfColumnDef("amt", 'N', 10, 2),
                new DbfColumnDef("note", 'M', 4),
            };
            using (var w = DbfWriter.Create(DbfPath, cols))
            {
                w.AppendRecord(1, "Alice", 10.00m, "memo-a");   // rec 1
                w.AppendRecord(2, "Bob",   25.50m, "memo-b");   // rec 2
                w.AppendRecord(3, "Cara",  99.00m, "memo-c");   // rec 3
                w.Flush();
            }

            Session = new VfpSession();
            Session.OpenDirectory(Dir);
            Interp = new VfpInterpreter(Session);
        }

        public void Run(string prg) => Interp.Execute(prg);
        public decimal Num(string expr) => Interp.EvalExpression(expr).AsNumber;
        public string Str(string expr) => Interp.EvalExpression(expr).AsString;
        public bool Bool(string expr) => Interp.EvalExpression(expr).AsLogical;

        // ── on-DISK inspection via an INDEPENDENT second handle (never the live interpreter cache) ──
        public string DiskStr(int rec0, string col)
        {
            using var t = DbfTable.Open(DbfPath, new DbfOptions { LockMode = LockMode.Shared });
            return t.GetRecord(rec0)?[col]?.ToString()?.TrimEnd() ?? string.Empty;
        }

        public decimal DiskNum(int rec0, string col)
        {
            using var t = DbfTable.Open(DbfPath, new DbfOptions { LockMode = LockMode.Shared });
            return Convert.ToDecimal(t.GetRecord(rec0)?[col] ?? 0);
        }

        public int OnDiskHeaderLength()
        {
            using var t = DbfTable.Open(DbfPath, new DbfOptions { LockMode = LockMode.Shared });
            return t.HeaderLength;
        }

        public void Dispose()
        {
            try { Session.Dispose(); } catch { }
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    // ─────────────────────────── (1a) SCATTER TO array → GATHER FROM array round-trip ───────────────────────────

    [Fact]
    public void ScatterToArray_ThenGatherFromArray_MEMO_RoundTripsWholeRecord()
    {
        using var b = new Bench();
        b.Run("USE people");

        // SCATTER rec 2 (Bob) into a 1-D array (MEMO ⇒ the memo column is captured as an element).
        b.Run("GO 2\nSCATTER TO aRec MEMO");
        // Element order = field order: 1=id, 2=name, 3=amt, 4=note.
        Assert.Equal("Bob", b.Str("aRec(2)").TrimEnd());
        Assert.Equal(25.50m, b.Num("aRec(3)"));
        Assert.Equal("memo-b", b.Str("aRec(4)").TrimEnd());

        // GATHER the captured array back over rec 1 (Alice) ⇒ rec 1 becomes a copy of rec 2.
        b.Run("GO 1\nGATHER FROM aRec MEMO");

        Assert.Equal(2m, b.DiskNum(0, "id"));
        Assert.Equal("Bob", b.DiskStr(0, "name"));
        Assert.Equal(25.50m, b.DiskNum(0, "amt"));
        Assert.Equal("memo-b", b.DiskStr(0, "note"));
    }

    // ─────────────────────────── (1b) SCATTER/GATHER FIELDS subset ───────────────────────────

    [Fact]
    public void ScatterGather_FieldsSubset_OnlyListedFieldsMove()
    {
        using var b = new Bench();
        b.Run("USE people");

        b.Run("GO 2\nSCATTER TO aF FIELDS name, amt");   // 2 elements: name, amt
        Assert.Equal("Bob", b.Str("aF(1)").TrimEnd());
        Assert.Equal(25.50m, b.Num("aF(2)"));

        b.Run("GO 1\nGATHER FROM aF FIELDS name, amt");   // only name+amt overwritten on rec 1

        Assert.Equal("Bob", b.DiskStr(0, "name"));   // moved
        Assert.Equal(25.50m, b.DiskNum(0, "amt"));   // moved
        Assert.Equal(1m, b.DiskNum(0, "id"));        // untouched
        Assert.Equal("memo-a", b.DiskStr(0, "note")); // untouched
    }

    // ─────────────────────────── (1c) SCATTER MEMVAR → GATHER MEMVAR round-trip ───────────────────────────

    [Fact]
    public void ScatterMemvar_ThenGatherMemvar_RoundTripsWholeRecord()
    {
        using var b = new Bench();
        b.Run("USE people");

        // Read rec 3 (Cara) into same-named memory variables, then write them over rec 1.
        b.Run("GO 3\nSCATTER MEMVAR MEMO");
        b.Run("GO 1\nGATHER MEMVAR MEMO");

        Assert.Equal(3m, b.DiskNum(0, "id"));
        Assert.Equal("Cara", b.DiskStr(0, "name"));
        Assert.Equal(99.00m, b.DiskNum(0, "amt"));
        Assert.Equal("memo-c", b.DiskStr(0, "note"));
    }

    // ─────────────────────────── (1d) HEADER() / LUPDATE() ───────────────────────────

    [Fact]
    public void Header_ReturnsOnDiskHeaderLength()
    {
        using var b = new Bench();
        b.Run("USE people");

        int expected = b.OnDiskHeaderLength();
        Assert.True(expected > 0, "sanity: the freshly written DBF must have a non-zero header length");
        Assert.Equal(expected, (int)b.Num("HEADER()"));
        // The alias-argument form addresses the same open work area.
        Assert.Equal(expected, (int)b.Num("HEADER('people')"));
    }

    [Fact]
    public void Lupdate_ReturnsHeaderLastUpdateDate_Today()
    {
        using var b = new Bench();
        b.Run("USE people");

        // The table was just written, so its header last-update date is today's date.
        Assert.True(b.Bool("LUPDATE() == DATE()"),
            "LUPDATE() must return the DBF header's last-update date (today for a just-written table)");
    }

    // ─────────────────────────── (1e) SET NULL ───────────────────────────

    [Fact]
    public void SetNullOn_MakesUnqualifiedColumnNullable_And_OmittedInsertBecomesNull()
    {
        using var b = new Bench();

        Assert.Equal("OFF", b.Str("SET('NULL')"));   // VFP default
        b.Run("SET NULL ON");
        Assert.Equal("ON", b.Str("SET('NULL')"));

        // Column b has no explicit NULL/NOT NULL clause ⇒ inherits SET NULL ON ⇒ nullable.
        b.Run("CREATE TABLE nt (a I, b C(5))");
        b.Run("INSERT INTO nt (a) VALUES (7)");     // b omitted ⇒ NULL (not blank) under SET NULL ON
        b.Run("SELECT nt\nGO TOP");

        Assert.True(b.Bool("ISNULL(nt.b)"),
            "under SET NULL ON, an omitted nullable column must be .NULL. (not blank)");
    }

    // ─────────────────────────── (1f) FLUSH / SET AUTOSAVE ───────────────────────────

    [Fact]
    public void FlushAndAutosave_AreRecognized_And_FlushPersists()
    {
        using var b = new Bench();
        b.Run("USE people");

        Assert.Equal("OFF", b.Str("SET('AUTOSAVE')"));   // VFP default
        b.Run("SET AUTOSAVE ON");
        Assert.Equal("ON", b.Str("SET('AUTOSAVE')"));

        b.Run("GO 1\nREPLACE name WITH 'Zed'");
        b.Run("FLUSH FORCE");
        Assert.Equal("Zed", b.DiskStr(0, "name"));
    }
}
