using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Phase 1c (tasks 1-3): the DML executor (INSERT / UPDATE / DELETE). Every test mutates a FRESH
/// TEMP table (created in a throwaway temp dir, NEVER a committed fixture) and asserts the resulting
/// on-disk state by RE-OPENING the file with the independent <see cref="DbfTable"/> reader (a
/// different code path from the executor) plus a round-trip SELECT.
///
/// VFP semantics under test: INSERT is single-row VALUES; column mapping (explicit list vs physical
/// field order) and per-type encoding (C/N/I/Y/D/T/L/M); value/column count mismatch is an error;
/// UPDATE evaluates each SET expression against the row's OWN current values (self-reference);
/// UPDATE/DELETE with no WHERE affect ALL candidate rows; DELETE is a SOFT delete (flag only, no
/// pack); and <c>SET DELETED ON</c> excludes already-deleted rows from the candidate set.
///
/// RED until the executor is implemented (the Phase-1c stub throws <see cref="NotImplementedException"/>).
/// </summary>
public sealed class SqlDmlExecutorTests : IDisposable
{
    private readonly SqlTestSupport.TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // ---- fixtures (temp only) -------------------------------------------------------------

    /// <summary>A fresh temp copy of the canonical person table (10 rows, row 7 deleted).</summary>
    private string NewPersonTable(string name = "person.dbf", bool withIndex = true)
    {
        string path = _dir.File(name);
        SqlTestSupport.CreatePersonTable(path, withIndex);
        return path;
    }

    /// <summary>A fresh empty temp table exercising every value type INSERT must encode.</summary>
    private string NewTypedTable(string name = "typed.dbf")
    {
        string path = _dir.File(name);
        using var w = DbfWriter.Create(path, new[]
        {
            new DbfColumnDef("CID", 'C', 10),
            new DbfColumnDef("CNUM", 'N', 10, 2),
            new DbfColumnDef("CINT", 'I'),
            new DbfColumnDef("CCUR", 'Y'),
            new DbfColumnDef("CDATE", 'D'),
            new DbfColumnDef("CDT", 'T'),
            new DbfColumnDef("CFLAG", 'L'),
            new DbfColumnDef("CMEMO", 'M'),
        }, new DbfCreateOptions { Overwrite = true });
        return path;
    }

    private VfpSession OpenSession(bool deleted = true)
    {
        var s = new VfpSession(new EvaluationContext { Deleted = deleted });
        s.OpenDirectory(_dir.Path);
        return s;
    }

    /// <summary>All physical records (incl. deleted), 1-based recno, as (recno, deleted, field-map).</summary>
    private static List<(int Recno, bool Deleted, Dictionary<string, object?> Fields)> ReadAll(string path)
    {
        var rows = new List<(int, bool, Dictionary<string, object?>)>();
        using var t = DbfTable.Open(path);
        int recno = 0;
        foreach (var rec in t.EnumerateAll(includeDeleted: true))
        {
            recno++;
            var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in t.Columns) map[c.Name] = rec[c.Name];
            rows.Add((recno, rec.IsDeleted, map));
        }
        return rows;
    }

    private static int RecordCount(string path)
    {
        using var t = DbfTable.Open(path);
        return t.RecordCount;
    }

    // ========================================================================================
    //  INSERT
    // ========================================================================================

    [Fact]
    public void Insert_With_Column_List_RoundTrips()
    {
        string path = NewPersonTable();
        using var s = OpenSession();

        var r = s.Execute(
            "INSERT INTO person (id, name, city, amount, hired, active) " +
            "VALUES (11, 'Newman', 'Bonn', 555.50, {^2020-05-05}, .T.)");

        Assert.NotNull(r);
        Assert.Equal(1, r!.AffectedRecords);
        Assert.Equal(11, RecordCount(path));

        var last = ReadAll(path).Single(x => x.Recno == 11).Fields;
        Assert.Equal(11, Convert.ToInt32(last["ID"]));
        Assert.Equal("Newman", ((string)last["NAME"]!).Trim());
        Assert.Equal("Bonn", ((string)last["CITY"]!).Trim());
        Assert.Equal(555.50m, Convert.ToDecimal(last["AMOUNT"]));
        Assert.Equal(new DateOnly(2020, 5, 5), (DateOnly)last["HIRED"]!);
        Assert.True((bool)last["ACTIVE"]!);
    }

    [Fact]
    public void Insert_Without_Column_List_Uses_Physical_Field_Order()
    {
        string path = NewPersonTable();
        using var s = OpenSession();

        var r = s.Execute("INSERT INTO person VALUES (12, 'Order', 'Kiel', 77.00, {^2019-01-02}, .F.)");

        Assert.Equal(1, r!.AffectedRecords);
        // fresh 10-row table + 1 append → the new physical record is recno 11 (its ID is 12).
        var last = ReadAll(path).Single(x => x.Recno == 11).Fields;
        Assert.Equal(12, Convert.ToInt32(last["ID"]));
        Assert.Equal("Order", ((string)last["NAME"]!).Trim());
        Assert.Equal("Kiel", ((string)last["CITY"]!).Trim());
        Assert.Equal(77.00m, Convert.ToDecimal(last["AMOUNT"]));
        Assert.Equal(new DateOnly(2019, 1, 2), (DateOnly)last["HIRED"]!);
        Assert.False((bool)last["ACTIVE"]!);
    }

    [Fact]
    public void Insert_Encodes_All_Value_Types()
    {
        string path = NewTypedTable();
        using var s = OpenSession();

        var r = s.Execute(
            "INSERT INTO typed (cid, cnum, cint, ccur, cdate, cdt, cflag, cmemo) " +
            "VALUES ('hello', 123.45, 42, 9.99, {^2021-03-04}, {^2021-03-04 12:30:00}, .T., 'memo body')");

        Assert.Equal(1, r!.AffectedRecords);
        Assert.Equal(1, RecordCount(path));

        var row = ReadAll(path).Single().Fields;
        Assert.Equal("hello", ((string)row["CID"]!).Trim());          // C
        Assert.Equal(123.45m, Convert.ToDecimal(row["CNUM"]));         // N
        Assert.Equal(42, Convert.ToInt32(row["CINT"]));               // I
        Assert.Equal(9.99m, Convert.ToDecimal(row["CCUR"]));          // Y
        Assert.Equal(new DateOnly(2021, 3, 4), (DateOnly)row["CDATE"]!); // D
        Assert.Equal(new DateTime(2021, 3, 4, 12, 30, 0), (DateTime)row["CDT"]!); // T
        Assert.True((bool)row["CFLAG"]!);                             // L
        Assert.Equal("memo body", ((string)row["CMEMO"]!).Trim());    // M
    }

    [Fact]
    public void Insert_Column_Value_Count_Mismatch_Throws()
    {
        NewPersonTable();
        using var s = OpenSession();
        // 3 columns, 2 values.
        Assert.Throws<FoxDbfSqlException>(() =>
            s.Execute("INSERT INTO person (id, name, city) VALUES (99, 'X')"));
    }

    [Fact]
    public void Insert_No_List_Value_Count_Mismatch_Throws()
    {
        NewPersonTable();
        using var s = OpenSession();
        // table has 6 fields, only 3 values supplied.
        Assert.Throws<FoxDbfSqlException>(() =>
            s.Execute("INSERT INTO person VALUES (99, 'X', 'Y')"));
    }

    [Fact]
    public void Insert_Allows_Functions_In_Values()
    {
        string path = NewPersonTable();
        using var s = OpenSession();

        var r = s.Execute("INSERT INTO person (id, name, hired) VALUES (13, UPPER('zed'), DATE())");

        Assert.Equal(1, r!.AffectedRecords);
        var last = ReadAll(path).Single(x => x.Recno == 11).Fields;
        Assert.Equal("ZED", ((string)last["NAME"]!).Trim());
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today), (DateOnly)last["HIRED"]!);
    }

    [Fact]
    public void Insert_Bare_Field_Reference_In_Values_Is_Error()
    {
        NewPersonTable();
        using var s = OpenSession();
        // 'amount' is a field reference, not a constant — no row context to read it from.
        Assert.Throws<FoxDbfSqlException>(() =>
            s.Execute("INSERT INTO person (id, amount) VALUES (14, amount)"));
    }

    [Fact]
    public void Insert_Then_Select_Sees_New_Row()
    {
        NewPersonTable();
        using var s = OpenSession();

        s.Execute("INSERT INTO person (id, name, city, amount) VALUES (21, 'Fresh', 'Trier', 1.00)");

        var sel = s.Execute("SELECT id, name FROM person WHERE id = 21");
        Assert.NotNull(sel);
        var rows = sel!.Rows.Select(x => x.ToArray()).ToList();
        Assert.Single(rows);
        Assert.Equal(21, Convert.ToInt32(rows[0][0]));
        Assert.Equal("Fresh", ((string)rows[0][1]!).Trim());
    }

    // ========================================================================================
    //  UPDATE
    // ========================================================================================

    [Fact]
    public void Update_Single_Column_Where_Selective()
    {
        string path = NewPersonTable();
        using var s = OpenSession();

        var r = s.Execute("UPDATE person SET city = 'Updated' WHERE id = 1");

        Assert.Equal(1, r!.AffectedRecords);
        var all = ReadAll(path);
        Assert.Equal("Updated", ((string)all.Single(x => Convert.ToInt32(x.Fields["ID"]) == 1).Fields["CITY"]!).Trim());
        // a non-matching row is untouched.
        Assert.Equal("Munich", ((string)all.Single(x => Convert.ToInt32(x.Fields["ID"]) == 3).Fields["CITY"]!).Trim());
    }

    [Fact]
    public void Update_Multi_Column()
    {
        string path = NewPersonTable();
        using var s = OpenSession();

        var r = s.Execute("UPDATE person SET city = 'Two', amount = 12.50 WHERE id = 2");

        Assert.Equal(1, r!.AffectedRecords);
        var row = ReadAll(path).Single(x => Convert.ToInt32(x.Fields["ID"]) == 2).Fields;
        Assert.Equal("Two", ((string)row["CITY"]!).Trim());
        Assert.Equal(12.50m, Convert.ToDecimal(row["AMOUNT"]));
        // an unmentioned column keeps its value (full-row rewrite must not blank it).
        Assert.Equal("Sm", ((string)row["NAME"]!).Trim());
    }

    [Fact]
    public void Update_Self_Referential_Reads_Rows_Own_Value()
    {
        string path = NewPersonTable();
        var before = ReadAll(path).ToDictionary(
            x => Convert.ToInt32(x.Fields["ID"]), x => Convert.ToDecimal(x.Fields["AMOUNT"]));
        using var s = OpenSession();

        var r = s.Execute("UPDATE person SET amount = amount + 1 WHERE id <= 3");

        Assert.Equal(3, r!.AffectedRecords);
        var after = ReadAll(path).ToDictionary(
            x => Convert.ToInt32(x.Fields["ID"]), x => Convert.ToDecimal(x.Fields["AMOUNT"]));
        Assert.Equal(before[1] + 1, after[1]);
        Assert.Equal(before[2] + 1, after[2]);
        Assert.Equal(before[3] + 1, after[3]);
        Assert.Equal(before[4], after[4]); // outside the WHERE → unchanged
    }

    [Fact]
    public void Update_No_Where_Affects_All_Live_Rows_Under_Deleted_On()
    {
        string path = NewPersonTable();
        using var s = OpenSession(deleted: true);

        var r = s.Execute("UPDATE person SET city = 'ALL'");

        // 10 physical rows, row 7 deleted → 9 live candidates under SET DELETED ON.
        Assert.Equal(9, r!.AffectedRecords);
        var all = ReadAll(path);
        Assert.All(all.Where(x => !x.Deleted), x => Assert.Equal("ALL", ((string)x.Fields["CITY"]!).Trim()));
        // the deleted row was skipped — keeps its original city.
        Assert.Equal("Hamburg", ((string)all.Single(x => x.Deleted).Fields["CITY"]!).Trim());
    }

    [Fact]
    public void Update_No_Where_Affects_Every_Row_Under_Deleted_Off()
    {
        string path = NewPersonTable();
        using var s = OpenSession(deleted: false);

        var r = s.Execute("UPDATE person SET city = 'ALL'");

        Assert.Equal(10, r!.AffectedRecords);
        var all = ReadAll(path);
        Assert.All(all, x => Assert.Equal("ALL", ((string)x.Fields["CITY"]!).Trim()));
        // VFP UPDATE modifies field DATA only — the already-deleted row (#7) stays deleted; the
        // full-row rewrite must NOT clear its deletion flag and silently RECALL it under DELETED OFF.
        Assert.Equal(1, all.Count(x => x.Deleted));
        Assert.True(all.Single(x => x.Recno == 7).Deleted);
    }

    [Fact]
    public void Update_Of_NonMemo_Column_Preserves_Memo_And_Ole_And_Does_Not_Grow_Fpt()
    {
        string path = _dir.File("ole.dbf");
        byte[] oleG = Enumerable.Range(0, 200).Select(i => (byte)(i * 7 + 3)).ToArray();
        byte[] oleP = Enumerable.Range(0, 137).Select(i => (byte)(255 - i)).ToArray();
        using (var w = DbfWriter.Create(path, new[]
        {
            new DbfColumnDef("ID",   'I', 4),
            new DbfColumnDef("NOTE", 'C', 10),
            new DbfColumnDef("MEMO", 'M', 4),
            new DbfColumnDef("GEN",  'G', 4),
            new DbfColumnDef("PIC",  'P', 4),
        }, new DbfCreateOptions { Overwrite = true }))
        {
            w.AppendRecord(1, "row-one", "memo body one", oleG, oleP);
            w.AppendRecord(2, "row-two", "memo body two", oleG, oleP);
            w.Flush();
        }

        string fptPath = Path.ChangeExtension(path, ".fpt");

        // Snapshot the pre-UPDATE state: the .fpt length and the block pointers for the FPT-backed
        // columns of the row we are about to touch.
        long fptSizeBefore = new FileInfo(fptPath).Length;
        int[] PointersOf(string p, int recno)
        {
            using var t = DbfTable.Open(p);
            var rec = t.GetRecord(recno - 1)!.Value;
            int Ptr(string col) => BinaryPrimitives.ReadInt32LittleEndian(
                rec.GetRawField(t.Columns.Single(c => c.Name == col)));
            return new[] { Ptr("MEMO"), Ptr("GEN"), Ptr("PIC") };
        }
        int[] before = PointersOf(path, 1);

        using var s = OpenSession();
        // UPDATE touches ONLY the non-FPT NOTE column; MEMO / GEN / PIC must be left untouched.
        var r = s.Execute("UPDATE ole SET note = 'changed' WHERE id = 1");
        Assert.Equal(1, r!.AffectedRecords);

        // The .fpt did NOT grow — no duplicate block was appended for the untouched memo/OLE fields.
        Assert.Equal(fptSizeBefore, new FileInfo(fptPath).Length);

        // The FPT block pointers are byte-identical (no re-point), and content round-trips intact.
        int[] after = PointersOf(path, 1);
        Assert.Equal(before, after);

        using var tbl = DbfTable.Open(path);
        var row = tbl.GetRecord(0)!.Value;
        Assert.Equal("changed", ((string)row["NOTE"]!).Trim());
        Assert.Equal("memo body one", ((string)row["MEMO"]!).Trim());   // memo content preserved
        byte[] ReadOle(string col)
        {
            int block = BinaryPrimitives.ReadInt32LittleEndian(
                row.GetRawField(tbl.Columns.Single(c => c.Name == col)));
            return tbl.Memo!.ReadBytes(block)!;
        }
        Assert.Equal(oleG, ReadOle("GEN"));   // General/OLE object intact, NOT corrupted
        Assert.Equal(oleP, ReadOle("PIC"));   // Picture object intact, NOT corrupted
    }

    [Fact]
    public void Update_Deleted_On_Skips_Already_Deleted_Rows()
    {
        string path = NewPersonTable();
        using var s = OpenSession(deleted: true);

        // row 7 (Adams) is deleted; a WHERE that would otherwise match it must not update it.
        var r = s.Execute("UPDATE person SET city = 'Z' WHERE name = 'Adams'");

        Assert.Equal(0, r!.AffectedRecords);
        Assert.Equal("Hamburg", ((string)ReadAll(path).Single(x => x.Deleted).Fields["CITY"]!).Trim());
    }

    // ========================================================================================
    //  DELETE
    // ========================================================================================

    [Fact]
    public void Delete_Where_Is_Soft_And_Count_Unchanged()
    {
        string path = NewPersonTable();
        using var s = OpenSession();

        var r = s.Execute("UPDATE person SET amount = amount"); // no-op warmup to ensure write path is clean
        Assert.Equal(9, r!.AffectedRecords);

        var d = s.Execute("DELETE FROM person WHERE city = 'Munich'");

        // rows 3, 5, 8 are Munich (all live) → 3 marked.
        Assert.Equal(3, d!.AffectedRecords);
        Assert.Equal(10, RecordCount(path)); // SOFT delete: physical count unchanged.
        var all = ReadAll(path);
        Assert.Equal(3, all.Count(x => x.Deleted && ((string)x.Fields["CITY"]!).Trim() == "Munich"));
    }

    [Fact]
    public void Delete_Where_Rows_Excluded_From_Subsequent_Select_Under_Deleted_On()
    {
        NewPersonTable();
        using var s = OpenSession(deleted: true);

        s.Execute("DELETE FROM person WHERE city = 'Munich'");

        var sel = s.Execute("SELECT id FROM person WHERE city = 'Munich'");
        Assert.NotNull(sel);
        Assert.Empty(sel!.Rows); // soft-deleted Munich rows are filtered out under SET DELETED ON.
    }

    [Fact]
    public void Delete_No_Where_Marks_All_Live_Rows_Under_Deleted_On()
    {
        string path = NewPersonTable();
        using var s = OpenSession(deleted: true);

        var d = s.Execute("DELETE FROM person");

        // 9 live rows marked (row 7 already deleted, excluded from the candidate set).
        Assert.Equal(9, d!.AffectedRecords);
        Assert.Equal(10, RecordCount(path));
        Assert.All(ReadAll(path), x => Assert.True(x.Deleted));
    }

    [Fact]
    public void Delete_No_Where_Marks_Every_Row_Under_Deleted_Off()
    {
        string path = NewPersonTable();
        using var s = OpenSession(deleted: false);

        var d = s.Execute("DELETE FROM person");

        Assert.Equal(10, d!.AffectedRecords);
        Assert.All(ReadAll(path), x => Assert.True(x.Deleted));
    }
}
