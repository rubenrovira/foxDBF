using System;
using System.IO;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Project-review finding 5.1 — group (1): THE core repro. A key-changing edit to a record whose key
/// is indexed must incrementally maintain the on-disk structural <c>.cdx</c>, so that WITHOUT any
/// REINDEX a SEEK finds the NEW key, no longer finds the OLD key, ordered iteration reflects the new
/// key, and the tag enumerates EXACTLY the expected key→recno set. Driven both through
/// <see cref="DbfWriter.UpdateRecord(int, object?[])"/> directly and through a microVFP REPLACE (which
/// funnels into the same writer).
///
/// RED until write-path maintenance exists: today UpdateRecord/REPLACE rewrite the row but never touch
/// the tag, so SEEK reads the STALE index (old key still points at the record, new key is absent).
///
/// SAFETY: throwaway temp dir only (deleted on dispose); no committed fixture is touched.
/// </summary>
public sealed class IndexMaintReplaceSeekTests
{
    [Fact]
    public void KeyChangingUpdate_ViaDbfWriter_MaintainsCdx_SeekFindsNewKey_NotOld()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        var cols = new[] { new DbfColumnDef("ID", 'I'), new DbfColumnDef("NAME", 'C', 10) };
        string dbf = IndexMaintTestSupport.CreateTable(dir.Path, "t.dbf", cols,
            new[]
            {
                new object?[] { 1, "ALPHA" },     // rec 1
                new object?[] { 2, "BRAVO" },     // rec 2  ← key changes below
                new object?[] { 3, "CHARLIE" },   // rec 3
            },
            new CdxTagDefinition("NAMETAG", "NAME"));

        using (var w = DbfWriter.Open(dbf))
            w.UpdateRecord(1, new object?[] { 2, "ZULU" });   // BRAVO → ZULU

        Assert.Equal((uint)2, IndexMaintTestSupport.Seek(dbf, "NAMETAG", (object)"ZULU"));  // new key found
        Assert.Null(IndexMaintTestSupport.Seek(dbf, "NAMETAG", (object)"BRAVO"));           // old key gone
        // Ordered iteration reflects the new key (ALPHA < CHARLIE < ZULU).
        Assert.Equal(new[] { 1, 3, 2 }, IndexMaintTestSupport.Recnos(dbf, "NAMETAG"));
        Assert.Equal(new[] { "ALPHA", "CHARLIE", "ZULU" }, IndexMaintTestSupport.StrKeys(dbf, "NAMETAG"));
    }

    [Fact]
    public void KeyChangingReplace_ViaMicroVfp_MaintainsCdx_SeekFindsNewKey_NotOld()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        var cols = new[] { new DbfColumnDef("id", 'I'), new DbfColumnDef("name", 'C', 10) };
        string dbf = IndexMaintTestSupport.CreateTable(dir.Path, "t.dbf", cols,
            new[]
            {
                new object?[] { 1, "ALPHA" },
                new object?[] { 2, "BRAVO" },
                new object?[] { 3, "CHARLIE" },
            });

        using (var b = new IndexMaintTestSupport.Bench(dir.Path))
        {
            b.Run("USE t\nINDEX ON name TAG nametag\nSET ORDER TO nametag");
            b.Run("GO 2\nREPLACE name WITH 'ZULU'");

            b.Run("=SEEK('ZULU', 't', 'nametag')");
            Assert.True(b.Bool("FOUND()"));    // new key seekable immediately
            b.Run("=SEEK('BRAVO', 't', 'nametag')");
            Assert.False(b.Bool("FOUND()"));   // old key no longer seekable
        }

        // And the on-disk cdx itself reflects the maintained order.
        Assert.Equal(new[] { 1, 3, 2 }, IndexMaintTestSupport.Recnos(dbf, "nametag"));
    }
}
