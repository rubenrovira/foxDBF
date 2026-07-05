using System;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP P2 table/record batch — part (2): the whole-table I/O commands that build or grow tables via
/// the existing Core writers — COPY STRUCTURE [EXTENDED] / CREATE FROM (DdlExecutor), COPY TO / APPEND
/// FROM / TOTAL (DbfWriter.Create/AppendRecord), and PACK (DbfWriter.Pack physical delete-compaction).
///
/// Written TESTS-FIRST: RED until the PRG commands are wired to those Core building blocks.
///
/// SAFETY: each case builds FRESH synthetic tables in its own throwaway temp dir (never a committed
/// fixture) and inspects the produced tables through INDEPENDENT read handles. Public-safe — synthetic
/// data only, no VFP9 oracle (the byte-exact oracle golden lives in the Internal project).
/// </summary>
public sealed class MicroVfpCopyTotalPackTableRecordTests
{
    private sealed class Bench : IDisposable
    {
        public string Dir { get; }
        public VfpSession Session { get; }
        public VfpInterpreter Interp { get; }

        public Bench()
        {
            Dir = Path.Combine(Path.GetTempPath(), "foxdbf_ctp_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Dir);
            Session = new VfpSession();
            Session.OpenDirectory(Dir);
            Interp = new VfpInterpreter(Session);
        }

        public string PathOf(string name) => System.IO.Path.Combine(Dir, name.EndsWith(".dbf") ? name : name + ".dbf");

        /// <summary>The canonical "sales" fixture: 5 rows already ordered by <c>grp</c> (A,A,B,C,C).</summary>
        public void MakeSales(string name = "sales")
        {
            var cols = new[]
            {
                new DbfColumnDef("id", 'I'),
                new DbfColumnDef("grp", 'C', 1),
                new DbfColumnDef("amt", 'N', 10, 2),
            };
            using var w = DbfWriter.Create(PathOf(name), cols);
            w.AppendRecord(1, "A", 10.00m);
            w.AppendRecord(2, "A", 20.00m);
            w.AppendRecord(3, "B", 5.00m);
            w.AppendRecord(4, "C", 100.00m);
            w.AppendRecord(5, "C", 1.00m);
            w.Flush();
        }

        public void MakeExtra(string name = "extra")
        {
            var cols = new[]
            {
                new DbfColumnDef("id", 'I'),
                new DbfColumnDef("grp", 'C', 1),
                new DbfColumnDef("amt", 'N', 10, 2),
            };
            using var w = DbfWriter.Create(PathOf(name), cols);
            w.AppendRecord(6, "D", 7.00m);
            w.AppendRecord(7, "D", 8.00m);
            w.Flush();
        }

        public void Run(string prg) => Interp.Execute(prg);

        public int DiskCount(string name)
        {
            using var t = DbfTable.Open(PathOf(name), new DbfOptions { LockMode = LockMode.Shared });
            return t.RecordCount;
        }

        public string[] DiskCols(string name)
        {
            using var t = DbfTable.Open(PathOf(name), new DbfOptions { LockMode = LockMode.Shared });
            return t.Columns.Select(c => c.Name.ToUpperInvariant()).ToArray();
        }

        public string DiskStr(string name, int rec0, string col)
        {
            using var t = DbfTable.Open(PathOf(name), new DbfOptions { LockMode = LockMode.Shared });
            return t.GetRecord(rec0)?[col]?.ToString()?.TrimEnd() ?? string.Empty;
        }

        public decimal DiskNum(string name, int rec0, string col)
        {
            using var t = DbfTable.Open(PathOf(name), new DbfOptions { LockMode = LockMode.Shared });
            return Convert.ToDecimal(t.GetRecord(rec0)?[col] ?? 0);
        }

        public void Dispose()
        {
            try { Session.Dispose(); } catch { }
            try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); } catch { }
        }
    }

    // ─────────────────────────── (2a) COPY STRUCTURE TO ───────────────────────────

    [Fact]
    public void CopyStructureTo_MakesEmptySameStructureTable()
    {
        using var b = new Bench();
        b.MakeSales();
        b.Run("USE sales\nCOPY STRUCTURE TO shell");

        Assert.True(File.Exists(b.PathOf("shell")), "COPY STRUCTURE TO did not create the target .dbf");
        Assert.Equal(0, b.DiskCount("shell"));                          // structure only, no rows
        Assert.Equal(new[] { "ID", "GRP", "AMT" }, b.DiskCols("shell")); // identical columns
    }

    // ─────────────────────────── (2b) COPY STRUCTURE EXTENDED + CREATE FROM ───────────────────────────

    [Fact]
    public void CopyStructureExtended_ThenCreateFrom_RoundTripsStructure()
    {
        using var b = new Bench();
        b.MakeSales();

        // EXTENDED writes a structure-descriptor table (one row per source field).
        b.Run("USE sales\nCOPY STRUCTURE EXTENDED TO sx");
        Assert.True(File.Exists(b.PathOf("sx")), "COPY STRUCTURE EXTENDED did not create the descriptor table");
        Assert.Equal(3, b.DiskCount("sx"));   // one descriptor row per field of sales (id/grp/amt)

        // CREATE FROM rebuilds a real, empty table from that descriptor table.
        b.Run("CREATE rebuilt FROM sx");
        Assert.True(File.Exists(b.PathOf("rebuilt")), "CREATE FROM did not create the rebuilt .dbf");
        Assert.Equal(0, b.DiskCount("rebuilt"));
        Assert.Equal(new[] { "ID", "GRP", "AMT" }, b.DiskCols("rebuilt"));
    }

    // ─────────────────────────── (2c) COPY TO ───────────────────────────

    [Fact]
    public void CopyTo_CopiesAllRows()
    {
        using var b = new Bench();
        b.MakeSales();
        b.Run("USE sales\nCOPY TO out");

        Assert.Equal(5, b.DiskCount("out"));
        Assert.Equal("A", b.DiskStr("out", 0, "grp"));
        Assert.Equal(100.00m, b.DiskNum("out", 3, "amt"));
    }

    [Fact]
    public void CopyTo_FieldsAndFor_ProjectAndFilter()
    {
        using var b = new Bench();
        b.MakeSales();

        b.Run("USE sales\nCOPY TO proj FIELDS id, amt");
        Assert.Equal(new[] { "ID", "AMT" }, b.DiskCols("proj"));
        Assert.Equal(5, b.DiskCount("proj"));

        b.Run("USE sales\nCOPY TO big FOR amt > 15");   // only amt 20 and 100 qualify
        Assert.Equal(2, b.DiskCount("big"));
        Assert.Equal(20.00m, b.DiskNum("big", 0, "amt"));
        Assert.Equal(100.00m, b.DiskNum("big", 1, "amt"));
    }

    // ─────────────────────────── (2d) APPEND FROM ───────────────────────────

    [Fact]
    public void AppendFrom_AppendsAllRowsOfSourceDbf()
    {
        using var b = new Bench();
        b.MakeSales();
        b.MakeExtra();

        b.Run("USE sales\nAPPEND FROM extra");
        Assert.Equal(7, b.DiskCount("sales"));
        Assert.Equal("D", b.DiskStr("sales", 5, "grp"));
        Assert.Equal(8.00m, b.DiskNum("sales", 6, "amt"));
    }

    [Fact]
    public void AppendFrom_For_FiltersSourceRows()
    {
        using var b = new Bench();
        b.MakeSales();
        b.MakeExtra();

        b.Run("USE sales\nAPPEND FROM extra FOR amt > 7");   // only extra's amt 8 row qualifies
        Assert.Equal(6, b.DiskCount("sales"));
        Assert.Equal(8.00m, b.DiskNum("sales", 5, "amt"));
    }

    // ─────────────────────────── (2e) TOTAL ───────────────────────────

    [Fact]
    public void Total_GroupsByKey_SumsNumericFields()
    {
        using var b = new Bench();
        b.MakeSales();   // already ordered by grp: A,A,B,C,C

        b.Run("USE sales\nTOTAL ON grp TO totalled FIELDS amt");

        Assert.Equal(3, b.DiskCount("totalled"));           // one row per group A/B/C
        Assert.Equal("A", b.DiskStr("totalled", 0, "grp"));
        Assert.Equal(30.00m, b.DiskNum("totalled", 0, "amt")); // 10 + 20
        Assert.Equal("B", b.DiskStr("totalled", 1, "grp"));
        Assert.Equal(5.00m, b.DiskNum("totalled", 1, "amt"));
        Assert.Equal("C", b.DiskStr("totalled", 2, "grp"));
        Assert.Equal(101.00m, b.DiskNum("totalled", 2, "amt")); // 100 + 1
    }

    // ─────────────────────────── (2f) PACK ───────────────────────────

    [Fact]
    public void Pack_PhysicallyRemovesDeletedRows_AndRenumbers()
    {
        using var b = new Bench();
        b.MakeSales();

        b.Run("USE sales EXCLUSIVE\nGO 3\nDELETE\nPACK");   // PACK needs EXCLUSIVE (VFP 110); delete rec 3 then compact

        Assert.Equal(4, b.DiskCount("sales"));                 // physical row removed
        // Records renumber: old rec 4 (C/100) slides into slot 3 (0-based index 2).
        Assert.Equal("C", b.DiskStr("sales", 2, "grp"));
        Assert.Equal(100.00m, b.DiskNum("sales", 2, "amt"));
    }
}
