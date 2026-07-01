using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Phase 1b (task 3): the <see cref="VfpSession"/> work-area model — USE / SELECT &lt;area&gt;,
/// IN 0 / SELECT 0 semantics, current-area + alias tracking, alias.field resolution, AUTO-OPEN of a
/// table named in a SELECT, and bare-name resolution against an open DBC (long names) FIRST then a
/// free <c>.dbf</c> in the data directory.
///
/// RED until <see cref="VfpSession"/> is implemented (the stub throws). SAFETY: temp DBC / DBF only.
/// </summary>
public sealed class VfpSessionTests : IDisposable
{
    private readonly SqlTestSupport.TempDir _dir = new();

    public VfpSessionTests() => SqlTestSupport.CreatePersonTable(_dir.File("person.dbf"));

    public void Dispose() => _dir.Dispose();

    private VfpSession FreeTableSession()
    {
        var s = new VfpSession();
        s.OpenDirectory(_dir.Path);
        return s;
    }

    // ---- USE + current-area / alias tracking ----------------------------------------------

    [Fact]
    public void Use_Selects_Area_And_Sets_Alias()
    {
        using var s = FreeTableSession();
        s.Execute("USE person AS p");
        Assert.Equal("P", s.CurrentAlias?.ToUpperInvariant());
        Assert.True(s.CurrentArea >= 1);
    }

    [Fact]
    public void Use_In_0_Does_Not_Change_Current_Area()
    {
        using var s = FreeTableSession();
        s.Execute("USE person AS a");
        int areaA = s.CurrentArea;
        // USE ... IN 0 opens in the lowest free area WITHOUT selecting it.
        s.Execute("USE person AGAIN IN 0 AS b");
        Assert.Equal("A", s.CurrentAlias?.ToUpperInvariant());
        Assert.Equal(areaA, s.CurrentArea);
    }

    [Fact]
    public void SelectArea_By_Alias_And_Number_Switches_Current()
    {
        using var s = FreeTableSession();
        s.Execute("USE person AS a");
        int areaA = s.CurrentArea;
        s.Execute("USE person AGAIN IN 0 AS b");

        s.Execute("SELECT b");
        Assert.Equal("B", s.CurrentAlias?.ToUpperInvariant());

        s.Execute("SELECT a");
        Assert.Equal("A", s.CurrentAlias?.ToUpperInvariant());
        Assert.Equal(areaA, s.CurrentArea);
    }

    [Fact]
    public void Select_0_Selects_An_Empty_Work_Area()
    {
        using var s = FreeTableSession();
        s.Execute("USE person AS a");
        s.Execute("SELECT 0");                  // lowest free area → no table
        Assert.Null(s.CurrentAlias);
    }

    [Fact]
    public void Direct_Use_And_SelectArea_Api()
    {
        using var s = FreeTableSession();
        s.Use("person", alias: "x");
        Assert.Equal("X", s.CurrentAlias?.ToUpperInvariant());
        s.SelectArea(0);
        Assert.Null(s.CurrentAlias);
        s.SelectArea("x");
        Assert.Equal("X", s.CurrentAlias?.ToUpperInvariant());
    }

    // ---- alias.field resolution -----------------------------------------------------------

    [Fact]
    public void AliasDotField_Projection_Resolves()
    {
        using var s = FreeTableSession();
        var result = s.Execute("SELECT a.id, a.name FROM person a")!;
        Assert.Equal(2, result.Columns.Count);
        var rows = SqlTestSupport.Materialize(result);
        Assert.Equal(9, rows.Count);                          // #7 deleted
        Assert.All(rows, r => Assert.NotNull(r[0]));
    }

    // ---- auto-open of a SELECT'd table ----------------------------------------------------

    [Fact]
    public void Select_AutoOpens_Table_Not_In_A_Work_Area()
    {
        using var s = FreeTableSession();
        // No prior USE: the executor must auto-open 'person' for the query.
        var result = s.Execute("SELECT * FROM person")!;
        Assert.Equal(9, SqlTestSupport.Materialize(result).Count);
    }

    [Fact]
    public void AutoOpen_Closes_Scratch_Area_And_Leaves_Current_Unchanged()
    {
        using var s = FreeTableSession();
        s.Execute("USE person AS keep");
        int before = s.CurrentArea;
        s.Execute("SELECT * FROM person");        // auto-open into a scratch area, then close it
        Assert.Equal(before, s.CurrentArea);
        Assert.Equal("KEEP", s.CurrentAlias?.ToUpperInvariant());
    }

    // ---- name resolution: DBC long name vs free-table directory ---------------------------

    [Fact]
    public void OpenDirectory_Resolves_Free_Table_By_File_Name()
    {
        using var s = new VfpSession();
        s.OpenDirectory(_dir.Path);
        var result = s.Execute("SELECT id FROM person")!;
        Assert.Equal(9, SqlTestSupport.Materialize(result).Count);
    }

    [Fact]
    public void OpenDatabase_Resolves_Table_By_Long_Name()
    {
        using var db = new DbcShop(_dir);
        using var s = new VfpSession();
        s.OpenDatabase(db.DbcPath);

        // 'Customers' is the DBC long name (file is cust.dbf) → resolves via the DBC.
        var result = s.Execute("SELECT * FROM Customers")!;
        var rows = SqlTestSupport.Materialize(result);
        Assert.Equal(2, rows.Count);
        Assert.Contains(result.Columns, c => c.Name.Equals("COMPANY", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OpenDatabase_Falls_Back_To_Free_Dbf_In_Directory()
    {
        using var db = new DbcShop(_dir);
        using var s = new VfpSession();
        s.OpenDatabase(db.DbcPath);

        // 'widgets' is NOT a DBC member; it is a free .dbf sibling → directory fallback.
        var result = s.Execute("SELECT * FROM widgets")!;
        Assert.Single(SqlTestSupport.Materialize(result));
    }

    // ---- a tiny temp DBC with one member ('Customers'→cust.dbf) + a free sibling ('widgets') ----

    private sealed class DbcShop : IDisposable
    {
        public string DbcPath { get; }
        public DbcShop(SqlTestSupport.TempDir dir)
        {
            string sub = Path.Combine(dir.Path, "shop");
            Directory.CreateDirectory(sub);
            DbcPath = Path.Combine(sub, "shop.dbc");

            DbfDatabaseBuilder.Create(DbcPath, new[]
            {
                new DbcTableSpec("Customers", "cust.dbf", new[]
                {
                    new DbfColumnDef("CUST_ID", 'I', 4),
                    new DbfColumnDef("COMPANY", 'C', 30),
                }),
            });

            // populate the DBC member table
            using (var w = DbfWriter.Open(Path.Combine(sub, "cust.dbf")))
            {
                w.AppendRecord(1, "Acme");
                w.AppendRecord(2, "Globex");
                w.Flush();
            }

            // a free .dbf sibling (NOT a DBC member) for the directory-fallback case
            using (var w = DbfWriter.Create(Path.Combine(sub, "widgets.dbf"), new[]
            {
                new DbfColumnDef("WID", 'I', 4),
            }, new DbfCreateOptions { Overwrite = true }))
            {
                w.AppendRecord(42);
                w.Flush();
            }
        }

        public void Dispose() { /* parent TempDir cleans the whole tree */ }
    }
}
