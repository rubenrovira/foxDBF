using System;
using System.Collections.Generic;
using System.IO;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// microVFP P2 (RI) — ATOMICITY / BUFFER-SHARING regression guards for the three correctness gaps the
/// 3 headline cascades never exercised (they are masked by RESTRICT writing no child + OLDVAL being read
/// exactly once + a single open handle per file):
///
///   • ROLLBACK after a riopen SCRATCH-area write must restore the child table to its pre-image — the
///     re-open must go BY PATH (the scratch alias "__ri&lt;n&gt;" is not a DBC member / on-disk file).
///   • OLDVAL() must re-base on a record move so a MULTI-record UPDATE cascade reads the right OLD key
///     per record (not a stale pre-image from the previous REPLACE).
///   • USE..AGAIN handles share one buffer: a write through one handle must be visible through a sibling.
///
/// SAFETY: every data-touching case runs on a FRESH TEMP COPY of TasTrade; the committed
/// <c>Tastrade_VFPData/</c> original is read ONLY for planning, never mutated.
/// </summary>
public sealed class MicroVfpRiAtomicityTests
{
    private static string TastradeDir => Path.Combine(Fixtures.RepoRoot, "Tastrade_VFPData");

    // ─────────────────────────── (1) ROLLBACK after a scratch-area write is atomic ───────────────────────────

    [Fact]
    public void Rollback_AfterScratchAreaWrite_RestoresChildTable_Atomically()
    {
        using var dir = new MicroVfpTestSupport.TempDir("ri_scratch_rollback");
        var interp = MicroVfpTestSupport.NewTastrade(dir, out var s);
        using (s)
        {
            // riopen('products', …) opens products AGAIN in a scratch area aliased "__ri<n>"; a REPLACE
            // through that handle writes products.dbf, then ROLLBACK must restore it. Before the fix the
            // restore re-opened the scratch area BY ALIAS ("__ri<n>") and threw — leaving a broken session.
            var ex = Record.Exception(() => interp.Execute(@"
PUBLIC pcRIcursors, pnerror, pcRIolderror, pcOldDBC, pcOldCompat, pcOldDele, pcOldExact, pcOldTalk
STORE '' TO pcRIcursors, pcRIolderror, pcOldDBC
pnerror = 0
pcOldCompat = 'OFF'
pcOldDele = 'ON'
pcOldExact = 'OFF'
pcOldTalk = 'OFF'
USE tastrade!products IN 0
SELECT products
GO 1
lcBefore = ALLTRIM(category_id)
lnArea = riopen('products','category_i')
lcAlias = '__ri' + ALLTRIM(STR(lnArea))
BEGIN TRANSACTION
SELECT (lcAlias)
GO 1
REPLACE category_id WITH 'ZZ'
ROLLBACK
SELECT products
GO 1
lcAfter = ALLTRIM(category_id)"));

            Assert.Null(ex);   // before the fix the ROLLBACK threw FoxDbfSqlException (re-open BY ALIAS).
            string before = interp.Memory.Get("lcBefore").AsString;
            string after = interp.Memory.Get("lcAfter").AsString;
            Assert.NotEqual("ZZ", before);                       // sanity: the pre-image wasn't already 'ZZ'.
            Assert.Equal(before, after);                         // ROLLBACK restored the written child record.

            // And the on-disk table really is back to the pre-image (not just the live handle).
            using var db = DbfDatabase.OpenFoxpro(Path.Combine(dir.Path, "tastrade.dbc"));
            using var t = db.OpenTable("products");
            var rec = t.GetRecord(0);
            Assert.Equal(before, rec?["category_id"]?.ToString()?.TrimEnd() ?? string.Empty);
        }
    }

    // ─────────────────────────── (2) OLDVAL re-bases on a move → multi-record UPDATE cascade ───────────────────────────

    [Fact]
    public void OldVal_ReBasesOnMove_AcrossSequentialReplaces()
    {
        using var dir = new MicroVfpTestSupport.TempDir("ri_oldval_move");
        var interp = MicroVfpTestSupport.NewTastrade(dir, out var s);
        using (s)
        {
            // Two REPLACEs on the SAME field of DIFFERENT records: OLDVAL after the second must be the
            // SECOND record's pre-value, not the first's stale pre-image (the bug kept the first).
            interp.Execute(@"
USE tastrade!category IN 0
SELECT category
GO 1
lcOrig1 = ALLTRIM(category_id)
GO 2
lcOrig2 = ALLTRIM(category_id)
GO 1
REPLACE category_id WITH 'A1'
lcOld1 = ALLTRIM(OLDVAL('category_id'))
GO 2
REPLACE category_id WITH 'B2'
lcOld2 = ALLTRIM(OLDVAL('category_id'))");

            string orig1 = interp.Memory.Get("lcOrig1").AsString;
            string orig2 = interp.Memory.Get("lcOrig2").AsString;
            Assert.NotEqual(orig1, orig2);                                   // sanity: distinct records.
            Assert.Equal(orig1, interp.Memory.Get("lcOld1").AsString);      // 1st OLDVAL = rec1's pre-value.
            Assert.Equal(orig2, interp.Memory.Get("lcOld2").AsString);      // 2nd OLDVAL = rec2's (not stale).
        }
    }

    [Fact]
    public void RiUpdate_TwoCategoriesInSequence_CascadeEachToItsOwnNewKey_LikeVfp9()
    {
        var (recA, keyA, beforeA) = PlanCategoryWithProducts(skipKey: null);
        var (recB, keyB, beforeB) = PlanCategoryWithProducts(skipKey: keyA);
        Assert.True(beforeA > 0 && beforeB > 0, "fixture precondition: two categories each with products");
        Assert.NotEqual(keyA, keyB);

        string newA = FreshKey(keyA, exclude: new[] { keyA, keyB });
        string newB = FreshKey(keyB, exclude: new[] { keyA, keyB, newA });

        using var dir = new MicroVfpTestSupport.TempDir("ri_upd_multi");
        string copy = MicroVfpTestSupport.CopyDatabase(TastradeDir, dir);
        using var s = new VfpSession();
        s.OpenDatabase(Path.Combine(copy, "tastrade.dbc"));
        var interp = new VfpInterpreter(s);
        interp.LoadFile(MicroVfpTestSupport.TastradeSp);
        interp.TriggerLevel = 1;

        interp.Execute($"USE tastrade!category IN 0\nSELECT category\nGO {recA}\nREPLACE category_id WITH '{newA}'");
        interp.Call("__RI_UPDATE_category");
        interp.Execute($"SELECT category\nGO {recB}\nREPLACE category_id WITH '{newB}'");
        interp.Call("__RI_UPDATE_category");

        // Each category's products moved to ITS OWN new key (the bug cascaded keyA twice and missed keyB).
        Assert.Equal(0, ChildLive(copy, "products", "category_id", keyA));
        Assert.Equal(beforeA, ChildLive(copy, "products", "category_id", newA));
        Assert.Equal(0, ChildLive(copy, "products", "category_id", keyB));
        Assert.Equal(beforeB, ChildLive(copy, "products", "category_id", newB));
    }

    // ─────────────────────────── (3) USE..AGAIN handles share a buffer ───────────────────────────

    [Fact]
    public void UseAgain_WriteViaOneHandle_VisibleThroughSibling()
    {
        using var dir = new MicroVfpTestSupport.TempDir("ri_again_buffer");
        var interp = MicroVfpTestSupport.NewTastrade(dir, out var s);
        using (s)
        {
            interp.Execute(@"
USE tastrade!setup IN 0
USE tastrade!setup AGAIN IN 0 ALIAS setup2
SELECT setup
GO TOP
lcBefore = ALLTRIM(value)
REPLACE value WITH 'ZZ'
SELECT setup2
GO TOP
lcSibling = ALLTRIM(value)");

            Assert.NotEqual("ZZ", interp.Memory.Get("lcBefore").AsString);   // sanity: not already 'ZZ'.
            // The sibling handle must reflect the write through the first handle (shared buffer in VFP).
            Assert.Equal("ZZ", interp.Memory.Get("lcSibling").AsString);
        }
    }

    // ─────────────────────────── planning helpers (read-only over the committed original) ───────────────────────────

    private static (int rec, string key, int liveChildren) PlanCategoryWithProducts(string? skipKey)
    {
        using var db = DbfDatabase.OpenFoxpro(Path.Combine(TastradeDir, "tastrade.dbc"));
        using var p = db.OpenTable("category");
        using var c = db.OpenTable("products");

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < c.RecordCount; i++)
        {
            if (c.IsRecordDeleted(i)) continue;
            string k = Field(c, i, "category_id");
            counts[k] = counts.TryGetValue(k, out var n) ? n + 1 : 1;
        }
        for (int i = 0; i < p.RecordCount; i++)
        {
            if (p.IsRecordDeleted(i)) continue;
            string k = Field(p, i, "category_id");
            if (skipKey is not null && k == skipKey) continue;
            if (counts.TryGetValue(k, out var n) && n > 0) return (i + 1, k, n);
        }
        return (0, string.Empty, 0);
    }

    private static string FreshKey(string template, IEnumerable<string> exclude)
    {
        using var db = DbfDatabase.OpenFoxpro(Path.Combine(TastradeDir, "tastrade.dbc"));
        using var p = db.OpenTable("category");
        var taken = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < p.RecordCount; i++)
            if (!p.IsRecordDeleted(i)) taken.Add(Field(p, i, "category_id"));
        foreach (var e in exclude) taken.Add(e);

        int width = Math.Max(1, template.Length);
        for (char c = 'A'; c <= 'Z'; c++)
        {
            string cand = new string(c, width);
            if (!taken.Contains(cand)) return cand;
        }
        return new string('Z', width);
    }

    private static int ChildLive(string dbDir, string childTable, string keyField, string keyVal)
    {
        using var db = DbfDatabase.OpenFoxpro(Path.Combine(dbDir, "tastrade.dbc"));
        using var t = db.OpenTable(childTable);
        int live = 0;
        for (int i = 0; i < t.RecordCount; i++)
        {
            if (t.IsRecordDeleted(i)) continue;
            if (Field(t, i, keyField) == keyVal) live++;
        }
        return live;
    }

    private static string Field(DbfTable t, int index, string col)
        => t.GetRecord(index)?[col]?.ToString()?.TrimEnd() ?? string.Empty;
}
