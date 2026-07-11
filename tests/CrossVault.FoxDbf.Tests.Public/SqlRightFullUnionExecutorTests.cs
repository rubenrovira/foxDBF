using System;
using System.Collections.Generic;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Phase-2 (RIGHT / FULL OUTER JOIN + UNION [ALL]): the executor's rows MUST equal the independent
/// brute-force ORACLE (<see cref="SqlTestSupport.RunJoinOracle"/> extended for RIGHT/FULL, and
/// <see cref="SqlTestSupport.RunUnionOracle"/>). These are RED until the executor implements the
/// three forms (it currently throws <see cref="NotSupportedException"/> for RIGHT/FULL and UNION).
///
/// Coverage:
///   RIGHT JOIN  — every right-side row appears; matched left joined; unmatched right gets NULL left;
///                 output columns stay FROM/JOIN declaration order; == the LEFT-with-sides-swapped result.
///   FULL JOIN   — matched + unmatched-left (NULL right) + unmatched-right (NULL left), each once.
///   UNION ALL   — concatenation (column count must match).
///   UNION       — concatenation + collation-aware dedup, NULLs equal; names from the FIRST select.
///   ORDER BY / TOP over the WHOLE union; mismatched column count -> FoxDbfSqlException.
///   Regression  — INNER / LEFT / comma-join + single-table still equal the oracle.
///
/// SAFETY: throwaway temp tables only — never a committed fixture.
/// </summary>
public sealed class SqlRightFullUnionExecutorTests : IDisposable
{
    private readonly SqlTestSupport.TempDir _indexed = new();
    private readonly SqlTestSupport.TempDir _plain = new();

    public SqlRightFullUnionExecutorTests()
    {
        SqlTestSupport.CreateJoinTables(_indexed.Path, withIndex: true);
        SqlTestSupport.CreateJoinTables(_plain.Path, withIndex: false);
        // collation-dedup fixtures: 'AA' (t1) vs 'aa' (t2) — distinct under MACHINE, equal under GENERAL.
        foreach (var dir in new[] { _indexed.Path, _plain.Path })
        {
            CreateOneCharTable(System.IO.Path.Combine(dir, "t1.dbf"), "AA");
            CreateOneCharTable(System.IO.Path.Combine(dir, "t2.dbf"), "aa");
        }
    }

    public void Dispose() { _indexed.Dispose(); _plain.Dispose(); }

    private static void CreateOneCharTable(string path, string value)
    {
        using var w = DbfWriter.Create(path, new[] { new DbfColumnDef("V", 'C', 4) },
            new DbfCreateOptions { Overwrite = true });
        w.AppendRecord(value);
        w.Flush();
    }

    private static EvaluationContext OracleCtx(VfpSession s) => new()
    {
        Deleted = s.Context.Deleted,
        Exact = s.Context.Exact,
        Ansi = s.Context.Ansi,
        Collation = s.Context.Collation,
        SqlSemantics = true,
    };

    private void CheckJoin(string sql, bool withIndex, bool ordered = false)
    {
        string dir = withIndex ? _indexed.Path : _plain.Path;
        using var s = new VfpSession();
        s.OpenDirectory(dir);

        var stmt = (SelectStatement)SqlParser.Parse(sql);
        var expected = SqlTestSupport.RunJoinOracle(dir, OracleCtx(s), stmt);
        var actual = SqlTestSupport.Materialize(s.Execute(sql)!);

        if (ordered) SqlTestSupport.AssertRowsEqualOrdered(expected, actual);
        else SqlTestSupport.AssertRowsEqualUnordered(expected, actual);
    }

    private void CheckJoinBoth(string sql, bool ordered = false)
    {
        CheckJoin(sql, withIndex: true, ordered: ordered);
        CheckJoin(sql, withIndex: false, ordered: ordered);
    }

    private void CheckUnion(string sql, bool ordered = false, VfpSession? session = null)
    {
        string dir = _indexed.Path;
        var s = session ?? new VfpSession();
        try
        {
            s.OpenDirectory(dir);
            var stmt = (SelectStatement)SqlParser.Parse(sql);
            var expected = SqlTestSupport.RunUnionOracle(dir, OracleCtx(s), stmt);
            var actual = SqlTestSupport.Materialize(s.Execute(sql)!);
            if (ordered) SqlTestSupport.AssertRowsEqualOrdered(expected, actual);
            else SqlTestSupport.AssertRowsEqualUnordered(expected, actual);
        }
        finally { if (session is null) s.Dispose(); }
    }

    // ======================================================================================
    //  RIGHT [OUTER] JOIN
    // ======================================================================================

    [Theory]
    // every DEPT appears; dept 4 "Empty" has no emp -> emp side NULL; emp orphan Frank excluded.
    [InlineData("SELECT emp.ename, dept.dname FROM emp RIGHT JOIN dept ON emp.deptid = dept.deptid")]
    [InlineData("SELECT e.ename, d.dname FROM emp e RIGHT OUTER JOIN dept d ON e.deptid = d.deptid")]
    // every EMP appears; orphan Frank -> dept side NULL; dept 4 "Empty" excluded.
    [InlineData("SELECT dept.dname, emp.ename FROM dept RIGHT JOIN emp ON dept.deptid = emp.deptid")]
    // SELECT * keeps FROM/JOIN declaration column order (left table's columns then right table's).
    [InlineData("SELECT * FROM emp RIGHT JOIN dept ON emp.deptid = dept.deptid")]
    public void RightJoin_Matches_Oracle(string sql) => CheckJoinBoth(sql);

    [Fact]
    public void RightJoin_Unmatched_Right_Emits_Null_Left()
    {
        // emp RIGHT JOIN dept: dept 4 "Empty" appears once, emp.ename NULL; Frank (orphan emp) absent.
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);
        var rows = SqlTestSupport.Materialize(
            s.Execute("SELECT dept.dname, emp.ename FROM emp RIGHT JOIN dept ON emp.deptid = dept.deptid")!);

        // one row per dept (1 Sales x2 emps, 2 Engineering x2 emps, 3 Marketing x1 emp, 4 Empty x0 -> 1 null row)
        var empty = rows.Where(r => ((string)r[0]!).TrimEnd() == "Empty").ToList();
        Assert.Single(empty);
        Assert.Null(empty[0][1]);
        Assert.DoesNotContain(rows, r => r[1] is string n && n.TrimEnd() == "Frank");
        Assert.Equal(SqlTestSupport.Emps.Count(e => SqlTestSupport.Depts.Any(d => d.DeptId == e.DeptId)) + 1, rows.Count);
    }

    [Fact]
    public void RightJoin_Equals_Left_With_Sides_Swapped()
    {
        // a RIGHT JOIN b == b LEFT JOIN a, with the SAME projection (so identical output column order).
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);
        var right = SqlTestSupport.Materialize(
            s.Execute("SELECT emp.ename, dept.dname FROM emp RIGHT JOIN dept ON emp.deptid = dept.deptid")!);
        var leftSwapped = SqlTestSupport.Materialize(
            s.Execute("SELECT emp.ename, dept.dname FROM dept LEFT JOIN emp ON dept.deptid = emp.deptid")!);
        SqlTestSupport.AssertRowsEqualUnordered(leftSwapped, right);
    }

    [Fact]
    public void RightJoin_With_Where_And_OrderBy_Matches_Oracle()
        => CheckJoin("SELECT emp.ename, dept.dname FROM emp RIGHT JOIN dept ON emp.deptid = dept.deptid WHERE dept.compid = 100 ORDER BY dept.dname, emp.ename",
                     withIndex: true, ordered: true);

    // ======================================================================================
    //  FULL [OUTER] JOIN
    // ======================================================================================

    [Theory]
    [InlineData("SELECT emp.ename, dept.dname FROM emp FULL JOIN dept ON emp.deptid = dept.deptid")]
    [InlineData("SELECT e.ename, d.dname FROM emp e FULL OUTER JOIN dept d ON e.deptid = d.deptid")]
    [InlineData("SELECT * FROM emp FULL JOIN dept ON emp.deptid = dept.deptid")]
    public void FullJoin_Matches_Oracle(string sql) => CheckJoinBoth(sql);

    [Fact]
    public void FullJoin_Emits_Both_Unmatched_Sides_Once()
    {
        // emp FULL JOIN dept: matched emp-dept pairs + Frank (orphan emp, dept NULL) + dept 4 (childless, emp NULL).
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);
        var rows = SqlTestSupport.Materialize(
            s.Execute("SELECT emp.ename, dept.dname FROM emp FULL JOIN dept ON emp.deptid = dept.deptid")!);

        var frank = rows.Where(r => r[0] is string n && n.TrimEnd() == "Frank").ToList();
        Assert.Single(frank);
        Assert.Null(frank[0][1]); // unmatched LEFT (emp) row -> right (dept) side NULL

        var empty = rows.Where(r => r[1] is string n && n.TrimEnd() == "Empty").ToList();
        Assert.Single(empty);
        Assert.Null(empty[0][0]); // unmatched RIGHT (dept) row -> left (emp) side NULL

        int matched = SqlTestSupport.Emps.Count(e => SqlTestSupport.Depts.Any(d => d.DeptId == e.DeptId));
        Assert.Equal(matched + 1 /*Frank*/ + 1 /*Empty*/, rows.Count);
    }

    [Fact]
    public void FullJoin_GroupBy_Aggregate_Matches_Oracle()
        => CheckJoin("SELECT d.dname, COUNT(*) AS n FROM emp e FULL JOIN dept d ON e.deptid = d.deptid GROUP BY d.dname ORDER BY d.dname",
                     withIndex: true, ordered: true);

    // ======================================================================================
    //  UNION [ALL]
    // ======================================================================================

    [Fact]
    public void UnionAll_Concatenates_All_Rows()
    {
        // dept.deptid {1,2,3,4} (4 rows) ++ emp.deptid {1,1,2,2,3,99} (6 rows) = 10 rows, no dedup.
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);
        var rows = SqlTestSupport.Materialize(
            s.Execute("SELECT deptid FROM dept UNION ALL SELECT deptid FROM emp")!);
        Assert.Equal(SqlTestSupport.Depts.Length + SqlTestSupport.Emps.Length, rows.Count);
    }

    [Fact]
    public void Union_Dedups_Rows()
    {
        // distinct of {1,2,3,4} ∪ {1,1,2,2,3,99} = {1,2,3,4,99} = 5 rows.
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);
        var rows = SqlTestSupport.Materialize(
            s.Execute("SELECT deptid FROM dept UNION SELECT deptid FROM emp")!);
        Assert.Equal(5, rows.Count);
    }

    [Theory]
    [InlineData("SELECT deptid FROM dept UNION ALL SELECT deptid FROM emp")]
    [InlineData("SELECT deptid FROM dept UNION SELECT deptid FROM emp")]
    [InlineData("SELECT compid, cname FROM company UNION ALL SELECT compid, dname FROM dept")]
    [InlineData("SELECT compid, cname FROM company UNION SELECT compid, dname FROM dept")]
    // chained: ALL then dedup link
    [InlineData("SELECT deptid FROM dept UNION ALL SELECT deptid FROM emp UNION SELECT deptid FROM dept")]
    // per-branch WHERE / DISTINCT survive into the union
    [InlineData("SELECT deptid FROM emp WHERE salary > 4500 UNION SELECT deptid FROM dept WHERE compid = 200")]
    [InlineData("SELECT DISTINCT deptid FROM emp UNION ALL SELECT deptid FROM dept")]
    public void Union_Matches_Oracle(string sql) => CheckUnion(sql);

    [Fact]
    public void Union_ColumnNames_Come_From_First_Select()
    {
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);
        var r = s.Execute("SELECT compid AS theid, cname AS thename FROM company UNION ALL SELECT deptid, dname FROM dept")!;
        Assert.Equal(new[] { "THEID", "THENAME" },
            r.Columns.Select(c => c.Name.ToUpperInvariant()).ToArray());
    }

    [Fact]
    public void Union_Mismatched_Column_Count_Throws()
    {
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);
        Assert.Throws<FoxDbfSqlException>(() =>
            SqlTestSupport.Materialize(s.Execute("SELECT deptid FROM dept UNION SELECT deptid, dname FROM dept")!));
    }

    [Fact]
    public void Union_OrderBy_Applies_To_Whole_Result_Matches_Oracle()
        => CheckUnion("SELECT deptid FROM dept UNION SELECT deptid FROM emp ORDER BY 1", ordered: true);

    [Fact]
    public void Union_OrderBy_By_Name_Equals_By_Ordinal()
    {
        // ORDER BY a column NAME (from the first select) orders identically to the 1-based ordinal.
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);
        var byName = SqlTestSupport.Materialize(
            s.Execute("SELECT deptid FROM dept UNION SELECT deptid FROM emp ORDER BY deptid")!);
        var byOrd = SqlTestSupport.Materialize(
            s.Execute("SELECT deptid FROM dept UNION SELECT deptid FROM emp ORDER BY 1")!);
        SqlTestSupport.AssertRowsEqualOrdered(byOrd, byName);
    }

    [Fact]
    public void Union_Top_Applies_To_Whole_Result_Matches_Oracle()
        // {1,2,3,4,99} ordered ascending, TOP 3 -> {1,2,3} (the 3rd and 4th keys differ, so no tie spill).
        => CheckUnion("SELECT TOP 3 deptid FROM dept UNION SELECT deptid FROM emp ORDER BY 1", ordered: true);

    [Fact]
    public void UnionDistinct_IntoCursor_ReturnsDmlCountAndRegistersQueryableResult()
    {
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);

        var materialized = s.Execute(
            "SELECT deptid FROM dept UNION SELECT deptid FROM emp INTO CURSOR union_distinct")!;

        Assert.Equal(5, materialized.AffectedRecords);
        Assert.Empty(materialized.Rows);
        var ids = s.Execute("SELECT deptid FROM union_distinct ORDER BY deptid")!.Rows
            .Select(row => Convert.ToInt32(row[0])).ToArray();
        Assert.Equal(new[] { 1, 2, 3, 4, 99 }, ids);
    }

    [Fact]
    public void UnionAll_IntoCursor_PreservesDuplicates()
    {
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);

        var materialized = s.Execute(
            "SELECT deptid FROM dept UNION ALL SELECT deptid FROM emp INTO CURSOR union_all")!;

        Assert.Equal(10, materialized.AffectedRecords);
        Assert.Empty(materialized.Rows);
        var ids = s.Execute("SELECT deptid FROM union_all ORDER BY deptid")!.Rows
            .Select(row => Convert.ToInt32(row[0])).ToArray();
        Assert.Equal(new[] { 1, 1, 1, 2, 2, 2, 3, 3, 4, 99 }, ids);
    }

    [Fact]
    public void Union_IntoTable_WritesFinalStructureAndRows()
    {
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);

        var materialized = s.Execute(
            "SELECT deptid AS id FROM dept UNION SELECT deptid FROM emp INTO TABLE union_table")!;

        Assert.Equal(5, materialized.AffectedRecords);
        Assert.Empty(materialized.Rows);
        string path = System.IO.Path.Combine(_indexed.Path, "union_table.dbf");
        Assert.True(System.IO.File.Exists(path));
        using var table = DbfTable.Open(path);
        Assert.Equal(new[] { "ID" }, table.Columns.Select(c => c.Name.ToUpperInvariant()).ToArray());
        Assert.Equal('I', char.ToUpperInvariant(table.Columns[0].Type));
        Assert.Equal(new[] { 1, 2, 3, 4, 99 }, table.EnumerateAll(includeDeleted: false)
            .Select(row => Convert.ToInt32(row["ID"])).OrderBy(id => id).ToArray());
    }

    [Fact]
    public void Union_OrderBy_IsAppliedBeforeIntoCursorMaterialization()
    {
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);

        var materialized = s.Execute(
            "SELECT deptid FROM dept UNION SELECT deptid FROM emp ORDER BY 1 DESC INTO CURSOR union_ordered")!;

        Assert.Equal(5, materialized.AffectedRecords);
        var idsInPhysicalOrder = s.Execute("SELECT deptid FROM union_ordered")!.Rows
            .Select(row => Convert.ToInt32(row[0])).ToArray();
        Assert.Equal(new[] { 99, 4, 3, 2, 1 }, idsInPhysicalOrder);
    }

    [Fact]
    public void Union_IntegerTop_DoesNotExtendOrderByTiesBeforeIntoCursor()
    {
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);

        var materialized = s.Execute(
            "SELECT TOP 2 deptid FROM dept UNION ALL SELECT deptid FROM emp " +
            "ORDER BY 1 INTO CURSOR union_top")!;

        Assert.Equal(2, materialized.AffectedRecords);
        Assert.Equal(new[] { 1, 1 }, s.Execute("SELECT deptid FROM union_top")!.Rows
            .Select(row => Convert.ToInt32(row[0])).ToArray());
    }

    [Fact]
    public void Union_IntegerTop_DoesNotExtendOrderByTiesWithoutInto()
    {
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);

        var ids = s.Execute(
            "SELECT TOP 2 deptid FROM dept UNION ALL SELECT deptid FROM emp ORDER BY 1")!.Rows
            .Select(row => Convert.ToInt32(row[0])).ToArray();

        Assert.Equal(new[] { 1, 1 }, ids);
    }

    [Fact]
    public void Union_TopPercent_IsAppliedBeforeTrailingIntoCursor()
    {
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);

        var materialized = s.Execute(
            "SELECT TOP 40 PERCENT deptid FROM dept UNION SELECT deptid FROM emp " +
            "ORDER BY 1 INTO CURSOR union_percent")!;

        Assert.Equal(2, materialized.AffectedRecords);
        Assert.Equal(new[] { 1, 2 }, s.Execute("SELECT deptid FROM union_percent")!.Rows
            .Select(row => Convert.ToInt32(row[0])).ToArray());
    }

    [Fact]
    public void Union_LeadingIntoFallbackAndPlainControlsRemainSupported()
    {
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);

        var leading = s.Execute(
            "SELECT deptid FROM dept INTO CURSOR union_leading UNION SELECT deptid FROM emp")!;
        Assert.Equal(5, leading.AffectedRecords);
        Assert.Equal(5, s.Execute("SELECT deptid FROM union_leading")!.Rows.Count());

        var trailingWins = s.Execute(
            "SELECT deptid FROM dept INTO CURSOR ignored_leading " +
            "UNION SELECT deptid FROM emp INTO CURSOR union_trailing")!;
        Assert.Equal(5, trailingWins.AffectedRecords);
        Assert.Equal(5, s.Execute("SELECT deptid FROM union_trailing")!.Rows.Count());
        Assert.ThrowsAny<Exception>(() => s.Execute("SELECT deptid FROM ignored_leading"));

        var middleIgnored = s.Execute(
            "SELECT deptid FROM dept UNION SELECT deptid FROM emp INTO CURSOR ignored_middle " +
            "UNION SELECT deptid FROM dept")!;
        Assert.Equal(-1, middleIgnored.AffectedRecords);
        Assert.Equal(5, middleIgnored.Rows.Count());
        Assert.ThrowsAny<Exception>(() => s.Execute("SELECT deptid FROM ignored_middle"));

        var plainUnion = s.Execute("SELECT deptid FROM dept UNION SELECT deptid FROM emp")!;
        Assert.Equal(-1, plainUnion.AffectedRecords);
        Assert.Equal(5, plainUnion.Rows.Count());

        var singleInto = s.Execute("SELECT deptid FROM dept INTO CURSOR single_control")!;
        Assert.Equal(4, singleInto.AffectedRecords);
        Assert.Equal(4, s.Execute("SELECT deptid FROM single_control")!.Rows.Count());
    }

    [Fact]
    public void Union_Nulls_Are_Equal_For_Dedup()
    {
        // Frank (orphan emp) LEFT JOIN dept yields (Frank, NULL). Unioning that branch with itself must
        // dedup the (Frank, NULL) rows to a SINGLE row -> NULLs compare equal in the dedup.
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);
        const string sql =
            "SELECT emp.ename, dept.dname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid WHERE emp.empid = 6 " +
            "UNION " +
            "SELECT emp.ename, dept.dname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid WHERE emp.empid = 6";
        var rows = SqlTestSupport.Materialize(s.Execute(sql)!);
        Assert.Single(rows);
        Assert.Null(rows[0][1]);
    }

    [Fact]
    public void Union_Dedup_Is_Collation_Aware()
    {
        // 'AA' (t1) and 'aa' (t2): under MACHINE they are distinct (2 rows); under GENERAL equal (1 row).
        using (var machine = new VfpSession(new EvaluationContext { Collation = VfpCollations.Machine }))
        {
            machine.OpenDirectory(_indexed.Path);
            var rows = SqlTestSupport.Materialize(machine.Execute("SELECT v FROM t1 UNION SELECT v FROM t2")!);
            Assert.Equal(2, rows.Count);
        }
        using (var general = new VfpSession(new EvaluationContext { Collation = VfpCollations.General }))
        {
            general.OpenDirectory(_indexed.Path);
            var rows = SqlTestSupport.Materialize(general.Execute("SELECT v FROM t1 UNION SELECT v FROM t2")!);
            Assert.Single(rows);
        }
    }

    // ======================================================================================
    //  Regression: INNER / LEFT / comma-join + single-table unchanged
    // ======================================================================================

    [Theory]
    [InlineData("SELECT emp.ename, dept.dname FROM emp INNER JOIN dept ON emp.deptid = dept.deptid")]
    [InlineData("SELECT emp.ename, dept.dname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid")]
    [InlineData("SELECT emp.ename, dept.dname FROM emp, dept WHERE emp.deptid = dept.deptid")]
    public void Existing_Joins_Still_Match_Oracle(string sql) => CheckJoinBoth(sql);

    [Fact]
    public void SingleTable_Select_Still_Works()
    {
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);
        var rows = SqlTestSupport.Materialize(
            s.Execute("SELECT empid, ename FROM emp WHERE salary > 4500 ORDER BY empid")!);
        Assert.Equal(new[] { 1, 3, 4 }, rows.Select(r => Convert.ToInt32(r[0])).ToArray());
    }
}
