using System;
using System.Linq;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// AST-structure pin-down tests for the VFP-SQL parser (Phase 1a — parser only, no
/// execution). Every scalar/predicate fragment must come back as a real
/// <see cref="VfpExpression"/> whose <c>Text</c> round-trips the located source slice.
/// Malformed input must surface as <see cref="FoxDbfSqlException"/>.
/// </summary>
public sealed class SqlParserTests
{
    private static SelectStatement Sel(string sql) => Assert.IsType<SelectStatement>(SqlParser.Parse(sql));

    // ====================================================================
    //  SELECT — projection shapes
    // ====================================================================

    [Fact]
    public void Select_Star()
    {
        var s = Sel("SELECT * FROM customer");
        var item = Assert.Single(s.Items);
        Assert.True(item.IsStar);
        Assert.Null(item.StarAlias);
        Assert.Null(item.Expression);
        Assert.Equal("customer", Assert.Single(s.From).Table);
    }

    [Fact]
    public void Select_QualifiedStar()
    {
        var s = Sel("SELECT c.* FROM customer c");
        var item = Assert.Single(s.Items);
        Assert.True(item.IsStar);
        Assert.Equal("c", item.StarAlias);
    }

    [Fact]
    public void Select_ColumnList_NoAlias()
    {
        var s = Sel("SELECT cust_id, company FROM customer");
        Assert.Equal(2, s.Items.Count);
        Assert.Equal("cust_id", s.Items[0].Expression!.Text);
        Assert.Null(s.Items[0].Alias);
        Assert.Equal("company", s.Items[1].Expression!.Text);
    }

    [Fact]
    public void Select_ColumnList_WithAsAlias()
    {
        var s = Sel("SELECT cust_id AS id, company AS name FROM customer");
        Assert.Equal("cust_id", s.Items[0].Expression!.Text);
        Assert.Equal("id", s.Items[0].Alias);
        Assert.Equal("company", s.Items[1].Expression!.Text);
        Assert.Equal("name", s.Items[1].Alias);
    }

    [Fact]
    public void Select_ImplicitAlias_NoAs()
    {
        var s = Sel("SELECT cust_id id FROM customer");
        var item = Assert.Single(s.Items);
        Assert.Equal("cust_id", item.Expression!.Text);
        Assert.Equal("id", item.Alias);
    }

    [Fact]
    public void Select_AliasDotField()
    {
        var s = Sel("SELECT c.cust_id, o.order_id FROM customer c, orders o");
        Assert.Equal("c.cust_id", s.Items[0].Expression!.Text);
        Assert.Equal("o.order_id", s.Items[1].Expression!.Text);
    }

    [Fact]
    public void Select_ExpressionItem_ParsesAsVfpExpression()
    {
        var s = Sel("SELECT price * qty AS total FROM line_items");
        var e = s.Items[0].Expression!;
        Assert.IsType<VfpExpression>(e);
        Assert.Equal("price * qty", e.Text);
        Assert.Equal("total", s.Items[0].Alias);
    }

    [Fact]
    public void Select_Distinct()
    {
        var s = Sel("SELECT DISTINCT country FROM customer");
        Assert.True(s.Distinct);
        Assert.Equal("country", s.Items[0].Expression!.Text);
    }

    [Fact]
    public void Select_All_IsNotDistinct()
    {
        var s = Sel("SELECT ALL country FROM customer");
        Assert.False(s.Distinct);
    }

    [Fact]
    public void Select_Top()
    {
        var s = Sel("SELECT TOP 10 * FROM customer");
        Assert.Equal(10, s.Top);
        Assert.False(s.TopPercent);
    }

    [Fact]
    public void Select_TopPercent()
    {
        var s = Sel("SELECT TOP 5 PERCENT * FROM customer");
        Assert.Equal(5, s.Top);
        Assert.True(s.TopPercent);
    }

    /// <summary>TOP is only a SELECT-head keyword (leading position). A column literally named
    /// <c>top</c> must remain a valid ORDER BY key — the ORDER BY scan must NOT treat TOP as a clause
    /// boundary (regression guard: trailing TOP after ORDER BY is not VFP syntax and was removed).</summary>
    [Fact]
    public void OrderBy_ColumnNamed_Top_IsNotABoundary()
    {
        var s = Sel("SELECT x FROM t ORDER BY top");
        var ob = Assert.Single(s.OrderBy);
        Assert.Equal("top", ob.Expression!.Text);
        Assert.Null(s.Top);
        Assert.False(s.TopPercent);
    }

    // ====================================================================
    //  Aggregates as expression fragments
    // ====================================================================

    [Fact]
    public void Select_SumAggregate_IsExpression()
    {
        var s = Sel("SELECT SUM(amount) AS tot FROM sales");
        Assert.Equal("SUM(amount)", s.Items[0].Expression!.Text);
        Assert.Equal("tot", s.Items[0].Alias);
    }

    [Fact]
    public void Select_CountStar_SpecialAggregate()
    {
        var s = Sel("SELECT COUNT(*) AS n FROM customer");
        var item = Assert.Single(s.Items);
        Assert.Equal("COUNT", item.AggregateStarFunction);
        Assert.Null(item.Expression);
        Assert.Equal("n", item.Alias);
    }

    [Fact]
    public void Select_MixedAggregates()
    {
        var s = Sel("SELECT country, COUNT(*) cnt, MAX(amount) AS hi FROM sales GROUP BY country");
        Assert.Equal("country", s.Items[0].Expression!.Text);
        Assert.Equal("COUNT", s.Items[1].AggregateStarFunction);
        Assert.Equal("cnt", s.Items[1].Alias);
        Assert.Equal("MAX(amount)", s.Items[2].Expression!.Text);
        Assert.Single(s.GroupBy);
        Assert.Equal("country", s.GroupBy[0].Text);
    }

    // ====================================================================
    //  WHERE / GROUP BY / HAVING / ORDER BY
    // ====================================================================

    [Fact]
    public void Select_Where_IsExpression()
    {
        var s = Sel("SELECT * FROM customer WHERE country = 'USA'");
        Assert.NotNull(s.Where);
        Assert.Equal("country = 'USA'", s.Where!.Text);
    }

    [Fact]
    public void Select_GroupBy_And_Having()
    {
        var s = Sel("SELECT country, region, SUM(amount) FROM sales " +
                    "GROUP BY country, region HAVING SUM(amount) > 1000");
        Assert.Equal(2, s.GroupBy.Count);
        Assert.Equal("country", s.GroupBy[0].Text);
        Assert.Equal("region", s.GroupBy[1].Text);
        Assert.NotNull(s.Having);
        Assert.Equal("SUM(amount) > 1000", s.Having!.Text);
    }

    [Fact]
    public void Select_OrderBy_AscDesc()
    {
        var s = Sel("SELECT * FROM customer ORDER BY country ASC, company DESC");
        Assert.Equal(2, s.OrderBy.Count);
        Assert.Equal("country", s.OrderBy[0].Expression!.Text);
        Assert.False(s.OrderBy[0].Descending);
        Assert.Equal("company", s.OrderBy[1].Expression!.Text);
        Assert.True(s.OrderBy[1].Descending);
    }

    [Fact]
    public void Select_OrderBy_Ordinal()
    {
        var s = Sel("SELECT cust_id, company FROM customer ORDER BY 2 DESC, 1");
        Assert.Equal(2, s.OrderBy.Count);
        Assert.Equal(2, s.OrderBy[0].Ordinal);
        Assert.True(s.OrderBy[0].Descending);
        Assert.Null(s.OrderBy[0].Expression);
        Assert.Equal(1, s.OrderBy[1].Ordinal);
        Assert.False(s.OrderBy[1].Descending);
    }

    [Fact]
    public void Select_FullClauseChain()
    {
        var s = Sel("SELECT TOP 3 country, SUM(amount) AS tot FROM sales " +
                    "WHERE amount > 0 GROUP BY country HAVING SUM(amount) > 10 " +
                    "ORDER BY tot DESC INTO CURSOR result");
        Assert.Equal(3, s.Top);
        Assert.Equal("amount > 0", s.Where!.Text);
        Assert.Single(s.GroupBy);
        Assert.NotNull(s.Having);
        Assert.Single(s.OrderBy);
        Assert.Equal(IntoKind.Cursor, s.Into!.Kind);
        Assert.Equal("result", s.Into.Name);
    }

    // ====================================================================
    //  INTO
    // ====================================================================

    [Fact]
    public void Select_IntoCursor_WithFlags()
    {
        var s = Sel("SELECT * FROM customer INTO CURSOR tmp READWRITE NOFILTER");
        Assert.Equal(IntoKind.Cursor, s.Into!.Kind);
        Assert.Equal("tmp", s.Into.Name);
        Assert.True(s.Into.ReadWrite);
        Assert.True(s.Into.NoFilter);
    }

    [Fact]
    public void Select_IntoTable()
    {
        var s = Sel("SELECT * FROM customer INTO TABLE backup");
        Assert.Equal(IntoKind.Table, s.Into!.Kind);
        Assert.Equal("backup", s.Into.Name);
    }

    [Fact]
    public void Select_IntoDbf_IsTableKind()
    {
        var s = Sel("SELECT * FROM customer INTO DBF backup");
        Assert.Equal(IntoKind.Table, s.Into!.Kind);
    }

    [Fact]
    public void Select_IntoArray()
    {
        var s = Sel("SELECT cust_id FROM customer INTO ARRAY aIds");
        Assert.Equal(IntoKind.Array, s.Into!.Kind);
        Assert.Equal("aIds", s.Into.Name);
    }

    // ====================================================================
    //  Multi-table FROM + JOINs
    // ====================================================================

    [Fact]
    public void Select_MultiTableFrom_CommaList()
    {
        var s = Sel("SELECT * FROM customer c, orders o, line_items l");
        Assert.Equal(3, s.From.Count);
        Assert.Equal("customer", s.From[0].Table);
        Assert.Equal("c", s.From[0].Alias);
        Assert.Equal("orders", s.From[1].Table);
        Assert.Equal("o", s.From[1].Alias);
        Assert.Equal("line_items", s.From[2].Table);
        Assert.Equal("l", s.From[2].Alias);
    }

    [Fact]
    public void Select_DbBangTable()
    {
        var s = Sel("SELECT * FROM tastrade!customer");
        Assert.Equal("tastrade", s.From[0].Database);
        Assert.Equal("customer", s.From[0].Table);
    }

    [Theory]
    [InlineData("INNER JOIN", JoinType.Inner)]
    [InlineData("LEFT JOIN", JoinType.Left)]
    [InlineData("LEFT OUTER JOIN", JoinType.Left)]
    [InlineData("RIGHT JOIN", JoinType.Right)]
    [InlineData("RIGHT OUTER JOIN", JoinType.Right)]
    [InlineData("FULL JOIN", JoinType.Full)]
    [InlineData("FULL OUTER JOIN", JoinType.Full)]
    public void Select_JoinTypes(string joinKw, JoinType expected)
    {
        var s = Sel($"SELECT * FROM customer c {joinKw} orders o ON c.cust_id = o.cust_id");
        var j = Assert.Single(s.Joins);
        Assert.Equal(expected, j.JoinType);
        Assert.Equal("orders", j.Source.Table);
        Assert.Equal("o", j.Source.Alias);
        Assert.Equal("c.cust_id = o.cust_id", j.On.Text);
    }

    [Fact]
    public void Select_MultipleJoins_WithWhere()
    {
        var s = Sel("SELECT * FROM customer c " +
                    "INNER JOIN orders o ON c.cust_id = o.cust_id " +
                    "LEFT JOIN line_items l ON o.order_id = l.order_id " +
                    "WHERE c.country = 'USA'");
        Assert.Equal(2, s.Joins.Count);
        Assert.Equal(JoinType.Inner, s.Joins[0].JoinType);
        Assert.Equal(JoinType.Left, s.Joins[1].JoinType);
        Assert.Equal("o.order_id = l.order_id", s.Joins[1].On.Text);
        Assert.Equal("c.country = 'USA'", s.Where!.Text);
    }

    // ====================================================================
    //  UNION
    // ====================================================================

    [Fact]
    public void Select_Union()
    {
        var s = Sel("SELECT cust_id FROM customer UNION SELECT cust_id FROM prospect");
        Assert.NotNull(s.Union);
        Assert.False(s.Union!.All);
        Assert.Equal("prospect", s.Union.Query.From[0].Table);
    }

    [Fact]
    public void Select_UnionAll_Chained()
    {
        var s = Sel("SELECT a FROM t1 UNION ALL SELECT a FROM t2 UNION SELECT a FROM t3");
        Assert.True(s.Union!.All);
        Assert.Equal("t2", s.Union.Query.From[0].Table);
        Assert.NotNull(s.Union.Query.Union);
        Assert.False(s.Union.Query.Union!.All);
        Assert.Equal("t3", s.Union.Query.Union.Query.From[0].Table);
    }

    // ====================================================================
    //  Fragment boundary protection (keywords inside literals/parens)
    // ====================================================================

    [Fact]
    public void Where_KeywordInsideStringLiteral_DoesNotSplit()
    {
        var s = Sel("SELECT * FROM t WHERE note = 'ORDER BY group from where'");
        Assert.Equal("note = 'ORDER BY group from where'", s.Where!.Text);
        Assert.Empty(s.GroupBy);
        Assert.Empty(s.OrderBy);
    }

    [Fact]
    public void Where_KeywordInsideParens_DoesNotSplit()
    {
        var s = Sel("SELECT * FROM t WHERE (a > 1 AND b < 2) ORDER BY a");
        Assert.Equal("(a > 1 AND b < 2)", s.Where!.Text);
        Assert.Single(s.OrderBy);
    }

    [Fact]
    public void SelectItem_KeywordInsideDateLiteral_DoesNotSplit()
    {
        var s = Sel("SELECT hire_date FROM emp WHERE hire_date > {^2004-01-01} ORDER BY hire_date");
        Assert.Equal("hire_date > {^2004-01-01}", s.Where!.Text);
        Assert.Single(s.OrderBy);
    }

    [Fact]
    public void Where_FunctionCommaNotMistakenForListSeparator()
    {
        // top-level comma rule must not apply inside a function call within WHERE
        var s = Sel("SELECT * FROM t WHERE BETWEEN(x, 1, 10)");
        Assert.Equal("BETWEEN(x, 1, 10)", s.Where!.Text);
    }

    // ====================================================================
    //  INSERT
    // ====================================================================

    [Fact]
    public void Insert_WithColumnList()
    {
        var st = Assert.IsType<InsertStatement>(
            SqlParser.Parse("INSERT INTO customer (cust_id, company) VALUES ('A01', 'Acme')"));
        Assert.Equal("customer", st.Table);
        Assert.Equal(new[] { "cust_id", "company" }, st.Columns);
        Assert.Equal(2, st.Values.Count);
        Assert.Equal("'A01'", st.Values[0].Text);
        Assert.Equal("'Acme'", st.Values[1].Text);
    }

    [Fact]
    public void Insert_WithoutColumnList()
    {
        var st = Assert.IsType<InsertStatement>(
            SqlParser.Parse("INSERT INTO customer VALUES ('A01', 'Acme', 1+2)"));
        Assert.Null(st.Columns);
        Assert.Equal(3, st.Values.Count);
        Assert.Equal("1+2", st.Values[2].Text);
    }

    [Fact]
    public void Insert_DbBangTable()
    {
        var st = Assert.IsType<InsertStatement>(
            SqlParser.Parse("INSERT INTO tastrade!customer VALUES ('X')"));
        Assert.Equal("tastrade", st.Database);
        Assert.Equal("customer", st.Table);
    }

    // ====================================================================
    //  UPDATE
    // ====================================================================

    [Fact]
    public void Update_SingleColumn_WithWhere()
    {
        var st = Assert.IsType<UpdateStatement>(
            SqlParser.Parse("UPDATE customer SET company = 'New' WHERE cust_id = 'A01'"));
        Assert.Equal("customer", st.Table);
        var set = Assert.Single(st.Assignments);
        Assert.Equal("company", set.Column);
        Assert.Equal("'New'", set.Value.Text);
        Assert.Equal("cust_id = 'A01'", st.Where!.Text);
    }

    [Fact]
    public void Update_MultiColumn_NoWhere()
    {
        var st = Assert.IsType<UpdateStatement>(
            SqlParser.Parse("UPDATE sales SET amount = amount * 1.1, tax = amount * 0.2"));
        Assert.Equal(2, st.Assignments.Count);
        Assert.Equal("amount", st.Assignments[0].Column);
        Assert.Equal("amount * 1.1", st.Assignments[0].Value.Text);
        Assert.Equal("tax", st.Assignments[1].Column);
        Assert.Equal("amount * 0.2", st.Assignments[1].Value.Text);
        Assert.Null(st.Where);
    }

    // ====================================================================
    //  DELETE
    // ====================================================================

    [Fact]
    public void Delete_WithWhere()
    {
        var st = Assert.IsType<DeleteStatement>(
            SqlParser.Parse("DELETE FROM customer WHERE country = 'XX'"));
        Assert.Equal("customer", st.Table);
        Assert.Equal("country = 'XX'", st.Where!.Text);
    }

    [Fact]
    public void Delete_NoWhere()
    {
        var st = Assert.IsType<DeleteStatement>(SqlParser.Parse("DELETE FROM customer"));
        Assert.Null(st.Where);
    }

    // ====================================================================
    //  USE variants
    // ====================================================================

    [Fact]
    public void Use_Bare_ClosesArea()
    {
        var u = Assert.IsType<UseCommand>(SqlParser.Parse("USE"));
        Assert.Null(u.Table);
        Assert.False(u.Prompt);
    }

    [Fact]
    public void Use_Table()
    {
        var u = Assert.IsType<UseCommand>(SqlParser.Parse("USE customer"));
        Assert.Equal("customer", u.Table);
    }

    [Fact]
    public void Use_DbBangTable()
    {
        var u = Assert.IsType<UseCommand>(SqlParser.Parse("USE tastrade!customer"));
        Assert.Equal("tastrade", u.Database);
        Assert.Equal("customer", u.Table);
    }

    [Fact]
    public void Use_Prompt()
    {
        var u = Assert.IsType<UseCommand>(SqlParser.Parse("USE ?"));
        Assert.True(u.Prompt);
        Assert.Null(u.Table);
    }

    [Fact]
    public void Use_InArea()
    {
        var u = Assert.IsType<UseCommand>(SqlParser.Parse("USE customer IN 0"));
        Assert.Equal("customer", u.Table);
        Assert.Equal(0, u.InArea);
    }

    [Fact]
    public void Use_InAlias()
    {
        var u = Assert.IsType<UseCommand>(SqlParser.Parse("USE customer IN cust"));
        Assert.Equal("cust", u.InAlias);
    }

    [Fact]
    public void Use_AliasOption()
    {
        var u = Assert.IsType<UseCommand>(SqlParser.Parse("USE customer ALIAS cust"));
        Assert.Equal("cust", u.Alias);
    }

    [Fact]
    public void Use_Again()
    {
        var u = Assert.IsType<UseCommand>(SqlParser.Parse("USE customer AGAIN"));
        Assert.True(u.Again);
    }

    [Fact]
    public void Use_Exclusive()
    {
        var u = Assert.IsType<UseCommand>(SqlParser.Parse("USE customer EXCLUSIVE"));
        Assert.Equal(UseMode.Exclusive, u.Mode);
    }

    [Fact]
    public void Use_Shared()
    {
        var u = Assert.IsType<UseCommand>(SqlParser.Parse("USE customer SHARED"));
        Assert.Equal(UseMode.Shared, u.Mode);
    }

    [Fact]
    public void Use_NoUpdate()
    {
        var u = Assert.IsType<UseCommand>(SqlParser.Parse("USE customer NOUPDATE"));
        Assert.True(u.NoUpdate);
    }

    [Fact]
    public void Use_AllOptionsCombined()
    {
        var u = Assert.IsType<UseCommand>(
            SqlParser.Parse("USE customer IN 2 AGAIN ALIAS c SHARED NOUPDATE"));
        Assert.Equal("customer", u.Table);
        Assert.Equal(2, u.InArea);
        Assert.True(u.Again);
        Assert.Equal("c", u.Alias);
        Assert.Equal(UseMode.Shared, u.Mode);
        Assert.True(u.NoUpdate);
    }

    // ====================================================================
    //  SELECT work-area switch (disambiguation)
    // ====================================================================

    [Fact]
    public void SelectArea_Number()
    {
        var a = Assert.IsType<SelectAreaCommand>(SqlParser.Parse("SELECT 0"));
        Assert.Equal(0, a.Area);
        Assert.Null(a.Alias);
    }

    [Fact]
    public void SelectArea_Alias()
    {
        var a = Assert.IsType<SelectAreaCommand>(SqlParser.Parse("SELECT customer"));
        Assert.Equal("customer", a.Alias);
        Assert.Null(a.Area);
    }

    [Fact]
    public void Select_WithFrom_IsQueryNotArea()
    {
        Assert.IsType<SelectStatement>(SqlParser.Parse("SELECT 0 FROM customer"));
        Assert.IsType<SelectStatement>(SqlParser.Parse("SELECT name FROM customer"));
    }

    [Fact]
    public void Select_StarIsQueryNotArea()
    {
        Assert.IsType<SelectStatement>(SqlParser.Parse("SELECT * FROM customer"));
    }

    // ====================================================================
    //  Case-insensitivity & script
    // ====================================================================

    [Fact]
    public void Keywords_CaseInsensitive()
    {
        var s = Sel("select * from customer where country = 'USA' order by company");
        Assert.Single(s.From);
        Assert.NotNull(s.Where);
        Assert.Single(s.OrderBy);
    }

    [Fact]
    public void ParseScript_MultipleStatements()
    {
        var list = SqlParser.ParseScript(
            "USE customer; SELECT * FROM customer WHERE country = 'USA'; DELETE FROM customer");
        Assert.Equal(3, list.Count);
        Assert.IsType<UseCommand>(list[0]);
        Assert.IsType<SelectStatement>(list[1]);
        Assert.IsType<DeleteStatement>(list[2]);
    }

    [Fact]
    public void Parse_TrailingSemicolon_Ok()
    {
        Assert.IsType<SelectStatement>(SqlParser.Parse("SELECT * FROM customer;"));
    }

    // ====================================================================
    //  Round-trip: every fragment is a real VfpExpression
    // ====================================================================

    [Fact]
    public void Fragments_AreRealCompilableExpressions()
    {
        var s = Sel("SELECT price * qty AS total FROM line_items WHERE qty > 0 ORDER BY price");
        // select-item expression
        Assert.NotNull(s.Items[0].Expression!.Compile());
        // where + order-by are real VfpExpression instances
        Assert.IsType<VfpExpression>(s.Where);
        Assert.IsType<VfpExpression>(s.OrderBy[0].Expression);
    }

    // ====================================================================
    //  Malformed input → FoxDbfSqlException
    // ====================================================================

    [Fact]
    public void Malformed_MissingFrom_Throws()
    {
        Assert.Throws<FoxDbfSqlException>(() => SqlParser.Parse("SELECT cust_id, company"));
    }

    [Fact]
    public void Malformed_UnterminatedString_Throws()
    {
        Assert.Throws<FoxDbfSqlException>(() => SqlParser.Parse("SELECT * FROM t WHERE x = 'abc"));
    }

    [Fact]
    public void Malformed_UnterminatedDate_Throws()
    {
        Assert.Throws<FoxDbfSqlException>(() => SqlParser.Parse("SELECT * FROM t WHERE d = {^2004-01-01"));
    }

    [Fact]
    public void Malformed_JoinMissingJoinKeyword_Throws()
    {
        Assert.Throws<FoxDbfSqlException>(
            () => SqlParser.Parse("SELECT * FROM a INNER b ON a.x = b.x"));
    }

    [Fact]
    public void Malformed_JoinMissingOn_Throws()
    {
        Assert.Throws<FoxDbfSqlException>(
            () => SqlParser.Parse("SELECT * FROM a INNER JOIN b WHERE a.x = 1"));
    }

    [Fact]
    public void Malformed_InsertMissingValues_Throws()
    {
        Assert.Throws<FoxDbfSqlException>(
            () => SqlParser.Parse("INSERT INTO customer (cust_id) ('A01')"));
    }

    [Fact]
    public void Malformed_UpdateMissingEquals_Throws()
    {
        Assert.Throws<FoxDbfSqlException>(
            () => SqlParser.Parse("UPDATE customer SET company 'New'"));
    }

    [Fact]
    public void Malformed_UnknownCommand_Throws()
    {
        Assert.Throws<FoxDbfSqlException>(() => SqlParser.Parse("FROBNICATE foo"));
    }

    [Fact]
    public void Malformed_EmptyWhere_Throws()
    {
        Assert.Throws<FoxDbfSqlException>(() => SqlParser.Parse("SELECT * FROM t WHERE"));
    }

    [Fact]
    public void Malformed_GarbageExpressionInWhere_Throws()
    {
        var ex = Assert.Throws<FoxDbfSqlException>(
            () => SqlParser.Parse("SELECT * FROM t WHERE a +"));
        Assert.True(ex.Position >= 0);
    }

    [Fact]
    public void Malformed_NullInput_Throws()
    {
        Assert.Throws<FoxDbfSqlException>(() => SqlParser.Parse(null!));
    }

    // ====================================================================
    //  Non-integer / overflowing numbers in integer positions
    //  (must surface as FoxDbfSqlException, never raw FormatException/OverflowException)
    // ====================================================================

    [Theory]
    [InlineData("SELECT TOP 5.5 * FROM customer")]
    [InlineData("SELECT TOP 99999999999 * FROM customer")]
    [InlineData("SELECT 1.5")]
    [InlineData("SELECT 99999999999")]
    [InlineData("SELECT cust_id, company FROM customer ORDER BY 2.5")]
    [InlineData("SELECT cust_id, company FROM customer ORDER BY 99999999999")]
    [InlineData("USE customer IN 99999999999")]
    public void Malformed_NonIntegerOrOverflowNumber_ThrowsSqlException(string sql)
    {
        Assert.Throws<FoxDbfSqlException>(() => SqlParser.Parse(sql));
    }

    // ====================================================================
    //  Bracket-string literals [ ... ] shield fragment boundaries
    // ====================================================================

    [Fact]
    public void Where_KeywordInsideBracketLiteral_DoesNotSplit()
    {
        var s = Sel("SELECT * FROM t WHERE x = [ORDER BY]");
        Assert.Equal("x = [ORDER BY]", s.Where!.Text);
        Assert.Empty(s.OrderBy);
        Assert.Empty(s.GroupBy);
    }

    [Fact]
    public void Insert_BracketLiteralWithComma_NotSplitAsValueSeparator()
    {
        var st = Assert.IsType<InsertStatement>(
            SqlParser.Parse("INSERT INTO t VALUES ([a,b])"));
        var v = Assert.Single(st.Values);
        Assert.Equal("[a,b]", v.Text);
    }

    [Fact]
    public void Malformed_UnterminatedBracketLiteral_Throws()
    {
        Assert.Throws<FoxDbfSqlException>(() => SqlParser.Parse("SELECT * FROM t WHERE x = [abc"));
    }
}
