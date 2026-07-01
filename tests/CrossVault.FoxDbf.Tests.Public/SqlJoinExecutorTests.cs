using System;
using System.Collections.Generic;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Phase-2 (tasks 1-4, 6): the multi-table / JOIN executor's rows must EQUAL the independent
/// brute-force nested-loop ORACLE (<see cref="SqlTestSupport.RunJoinOracle"/>) across INNER JOIN,
/// LEFT JOIN, comma cross-join + WHERE, 3-table chains, qualified <c>alias.field</c> + <c>alias.*</c> +
/// unqualified fields, WHERE spanning sources, GROUP BY / aggregates / ORDER BY over the join,
/// DISTINCT and TOP — with the inner join key both INDEXED (accelerated) and NON-indexed (scan).
///
/// These are RED until the multi-table executor exists (the executor currently throws
/// <see cref="NotSupportedException"/> for JOINs / comma multi-table FROM).
/// SAFETY: throwaway temp tables only — never a committed fixture.
/// </summary>
public sealed class SqlJoinExecutorTests : IDisposable
{
    private readonly SqlTestSupport.TempDir _indexed = new();
    private readonly SqlTestSupport.TempDir _plain = new();

    public SqlJoinExecutorTests()
    {
        SqlTestSupport.CreateJoinTables(_indexed.Path, withIndex: true);
        SqlTestSupport.CreateJoinTables(_plain.Path, withIndex: false);
    }

    public void Dispose() { _indexed.Dispose(); _plain.Dispose(); }

    private static EvaluationContext OracleCtx(VfpSession s) => new()
    {
        Deleted = s.Context.Deleted,
        Exact = s.Context.Exact,
        Ansi = s.Context.Ansi,
        Collation = s.Context.Collation,
        SqlSemantics = true,
    };

    private void Check(string sql, bool ordered, bool withIndex)
    {
        string dir = withIndex ? _indexed.Path : _plain.Path;
        using var s = new VfpSession();
        s.OpenDirectory(dir);

        var stmt = (SelectStatement)SqlParser.Parse(sql);
        var expected = SqlTestSupport.RunJoinOracle(dir, OracleCtx(s), stmt);

        var result = s.Execute(sql);
        Assert.NotNull(result);
        var actual = SqlTestSupport.Materialize(result!);

        if (ordered) SqlTestSupport.AssertRowsEqualOrdered(expected, actual);
        else SqlTestSupport.AssertRowsEqualUnordered(expected, actual);
    }

    private void CheckBoth(string sql, bool ordered = false)
    {
        Check(sql, ordered, withIndex: true);
        Check(sql, ordered, withIndex: false);
    }

    // ---- (1) INNER JOIN on an equality key (indexed AND non-indexed) ----------------------

    [Theory]
    [InlineData("SELECT emp.empid, emp.ename, dept.dname FROM emp INNER JOIN dept ON emp.deptid = dept.deptid")]
    [InlineData("SELECT e.ename, d.dname FROM emp e INNER JOIN dept d ON e.deptid = d.deptid")]
    [InlineData("SELECT * FROM emp INNER JOIN dept ON emp.deptid = dept.deptid")]
    public void InnerJoin_Matches_Oracle(string sql) => CheckBoth(sql);

    [Fact]
    public void InnerJoin_Excludes_Orphan_Outer_Row()
    {
        // Frank (deptid 99) has no matching dept → must NOT appear in an INNER JOIN.
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);
        var rows = SqlTestSupport.Materialize(
            s.Execute("SELECT emp.ename FROM emp INNER JOIN dept ON emp.deptid = dept.deptid")!);
        Assert.Equal(5, rows.Count);
        Assert.DoesNotContain(rows, r => ((string)r[0]!).TrimEnd() == "Frank");
    }

    // ---- (2) LEFT JOIN: unmatched outer rows appear once with inner fields NULL -----------

    [Theory]
    [InlineData("SELECT emp.ename, dept.dname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid")]
    [InlineData("SELECT dept.dname, emp.ename FROM dept LEFT JOIN emp ON dept.deptid = emp.deptid")]
    [InlineData("SELECT * FROM dept LEFT JOIN emp ON dept.deptid = emp.deptid")]
    public void LeftJoin_Matches_Oracle(string sql) => CheckBoth(sql);

    [Fact]
    public void LeftJoin_Orphan_Outer_Emits_Null_Inner()
    {
        // emp LEFT JOIN dept: Frank (deptid 99) appears once, dept.dname NULL.
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);
        var rows = SqlTestSupport.Materialize(
            s.Execute("SELECT emp.ename, dept.dname FROM emp LEFT JOIN dept ON emp.deptid = dept.deptid")!);
        Assert.Equal(SqlTestSupport.Emps.Length, rows.Count); // every emp once
        var frank = rows.Single(r => ((string)r[0]!).TrimEnd() == "Frank");
        Assert.Null(frank[1]);
    }

    [Fact]
    public void LeftJoin_Childless_Outer_Emits_Null_Inner()
    {
        // dept LEFT JOIN emp: dept 4 "Empty" appears once, emp.ename NULL.
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);
        var rows = SqlTestSupport.Materialize(
            s.Execute("SELECT dept.dname, emp.ename FROM dept LEFT JOIN emp ON dept.deptid = emp.deptid")!);
        var empty = rows.Where(r => ((string)r[0]!).TrimEnd() == "Empty").ToList();
        Assert.Single(empty);
        Assert.Null(empty[0][1]);
    }

    // ---- (3) comma cross-join + WHERE -----------------------------------------------------

    [Theory]
    [InlineData("SELECT emp.ename, dept.dname FROM emp, dept WHERE emp.deptid = dept.deptid")]
    [InlineData("SELECT emp.ename, dept.dname FROM emp, dept WHERE emp.deptid = dept.deptid AND dept.dname = 'Sales'")]
    [InlineData("SELECT e.ename, d.dname FROM emp e, dept d WHERE e.deptid = d.deptid AND e.salary > 4500")]
    public void CommaJoin_Where_Matches_Oracle(string sql) => CheckBoth(sql);

    [Fact]
    public void CommaJoin_NoWhere_Is_Full_Cross_Product()
    {
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);
        var rows = SqlTestSupport.Materialize(s.Execute("SELECT emp.empid, dept.deptid FROM emp, dept")!);
        Assert.Equal(SqlTestSupport.Emps.Length * SqlTestSupport.Depts.Length, rows.Count);
    }

    // ---- (4) 3-table chain + qualified / star / unqualified + post-processing -------------

    [Theory]
    [InlineData("SELECT emp.ename, dept.dname, company.cname FROM emp JOIN dept ON emp.deptid = dept.deptid JOIN company ON dept.compid = company.compid")]
    [InlineData("SELECT e.ename, d.dname, c.cname FROM emp e JOIN dept d ON e.deptid = d.deptid JOIN company c ON d.compid = c.compid")]
    public void ThreeTableChain_Matches_Oracle(string sql) => CheckBoth(sql);

    [Fact]
    public void AliasStar_Expands_To_That_Source_Columns()
    {
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);
        var r = s.Execute("SELECT e.* FROM emp e JOIN dept d ON e.deptid = d.deptid")!;
        Assert.Equal(new[] { "EMPID", "ENAME", "DEPTID", "SALARY" },
            r.Columns.Select(c => c.Name.ToUpperInvariant()).ToArray());
    }

    [Fact]
    public void Unqualified_Field_Resolves_When_Unambiguous()
        => CheckBoth("SELECT ename, dname FROM emp JOIN dept ON emp.deptid = dept.deptid");

    [Fact]
    public void Unqualified_Ambiguous_Field_Is_An_Error()
    {
        // DEPTID exists in BOTH emp and dept → a bare 'deptid' projection must be rejected.
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);
        Assert.Throws<FoxDbfSqlException>(() =>
            SqlTestSupport.Materialize(s.Execute("SELECT deptid FROM emp JOIN dept ON emp.deptid = dept.deptid")!));
    }

    [Fact]
    public void Where_Spanning_Two_Sources_Matches_Oracle()
        => CheckBoth("SELECT e.ename, d.dname FROM emp e JOIN dept d ON e.deptid = d.deptid WHERE e.salary >= 5000 AND d.dname = 'Engineering'");

    [Fact]
    public void GroupBy_Aggregate_Over_Join_Matches_Oracle()
        => Check("SELECT d.dname, COUNT(*) AS n, SUM(e.salary) AS tot FROM emp e JOIN dept d ON e.deptid = d.deptid GROUP BY d.dname ORDER BY d.dname",
                 ordered: true, withIndex: true);

    [Fact]
    public void OrderBy_Over_Join_Matches_Oracle()
        => Check("SELECT e.ename, e.salary FROM emp e JOIN dept d ON e.deptid = d.deptid ORDER BY e.salary DESC, e.ename",
                 ordered: true, withIndex: true);

    [Fact]
    public void Distinct_Over_Join_Matches_Oracle()
        => CheckBoth("SELECT DISTINCT d.dname FROM emp e JOIN dept d ON e.deptid = d.deptid");

    [Fact]
    public void Top_Over_Join_Matches_Oracle()
        => Check("SELECT TOP 2 e.ename, e.salary FROM emp e JOIN dept d ON e.deptid = d.deptid ORDER BY e.salary DESC",
                 ordered: true, withIndex: true);

    // ---- (6) regression guard: single-table SELECT through the session is unchanged --------

    [Fact]
    public void SingleTable_Select_Still_Works()
    {
        using var s = new VfpSession();
        s.OpenDirectory(_indexed.Path);
        var rows = SqlTestSupport.Materialize(s.Execute("SELECT empid, ename FROM emp WHERE salary > 4500 ORDER BY empid")!);
        Assert.Equal(new[] { 1, 3, 4 }, rows.Select(r => Convert.ToInt32(r[0])).ToArray());
    }
}
