using System;
using System.Collections.Generic;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Scaffolding for the nested-subquery predicate tests (IN / NOT IN / EXISTS / NOT EXISTS /
/// scalar-comparison subqueries, uncorrelated + correlated, in WHERE / HAVING).
/// <para>
/// The driving data is a tiny customer→order pair with a DELIBERATELY NULLABLE foreign key
/// (<c>ord.cid</c>) so the NULL/empty three-valued-logic edges can be exercised exactly. The
/// expected results are computed by a brute-force LINQ pass over these in-memory arrays (a code
/// path completely independent of the executor) — see the per-test helpers in
/// <see cref="SqlSubqueryExecutorTests"/>. The authoritative VFP9 numbers for the same data live in
/// <see cref="SqlSubqueryVfp9OracleTests"/> (the in-memory arrays mirror that fixture row-for-row).
/// </para>
/// <para>SAFETY: throwaway temp tables only — never a committed fixture.</para>
/// </summary>
internal static partial class SqlTestSupport
{
    // ---- customer / order fixture (nullable FK on purpose) --------------------------------

    internal readonly record struct Cust(int Cid, string CName);
    internal readonly record struct Ord(int Oid, int? Cid, decimal Amt);

    /// <summary>Customers 1..4; #4 (Dave) has NO orders → NOT IN / NOT EXISTS target.</summary>
    internal static readonly Cust[] Custs =
    {
        new(1, "Alice"),
        new(2, "Bob"),
        new(3, "Carol"),
        new(4, "Dave"),
    };

    /// <summary>
    /// Orders. #13 carries a <c>.NULL.</c> customer id ON PURPOSE so that
    /// <c>SELECT cid FROM ord</c> yields a set that CONTAINS NULL — the classic NOT IN poison case
    /// (VFP9: <c>cid NOT IN (set containing NULL)</c> ⇒ empty). Amounts are crafted so the scalar
    /// AVG over all orders (1649/5 = 329.8) singles out only #14.
    /// </summary>
    internal static readonly Ord[] Ords =
    {
        new(10, 1,    100.00m),
        new(11, 1,    200.00m),
        new(12, 2,     50.00m),
        new(13, null, 300.00m),  // NULL customer id
        new(14, 3,    999.00m),
    };

    /// <summary>Writes <c>cust.dbf</c> + <c>ord.dbf</c> (ord.cid NULLABLE) to <paramref name="dir"/>,
    /// optionally with structural CDX tags on the join keys so the executor's inner lookups CAN be
    /// index-accelerated. The result set MUST be identical with or without the index.</summary>
    internal static void CreateCustOrdTables(string dir, bool withIndex = true)
    {
        string cust = System.IO.Path.Combine(dir, "cust.dbf");
        string ord = System.IO.Path.Combine(dir, "ord.dbf");

        using (var w = DbfWriter.Create(cust, new[]
        {
            new DbfColumnDef("CID", 'I', 4),
            new DbfColumnDef("CNAME", 'C', 10),
        }, new DbfCreateOptions { Overwrite = true }))
        {
            foreach (var c in Custs) w.AppendRecord(c.Cid, c.CName);
            if (withIndex) w.CreateTag(new CdxTagDefinition("TCCID", "CID"));
            w.Flush();
        }

        using (var w = DbfWriter.Create(ord, new[]
        {
            new DbfColumnDef("OID", 'I', 4),
            new DbfColumnDef("CID", 'I', 4, 0, nullable: true),
            new DbfColumnDef("AMT", 'N', 10, 2),
        }, new DbfCreateOptions { Overwrite = true }))
        {
            foreach (var o in Ords)
                w.AppendRecord(o.Oid, (object?)o.Cid, o.Amt);
            if (withIndex) w.CreateTag(new CdxTagDefinition("TOCID", "CID"));
            w.Flush();
        }
    }
}
