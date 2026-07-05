using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Write;
using Xunit;

namespace CrossVault.FoxDbf.Tests;

public sealed class InsertOnlyCacheTests
{
    private static readonly DbfColumnDef[] Columns =
    {
        new("ID", 'I'),
        new("NAME", 'C', 12),
        new("GRP", 'C', 4),
    };

    [Fact]
    public void AppendRun_EnumeratesAndMatchesFreshWriterPerRowControl()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        string seed = CreateSeed(dir.Path, "seed.dbf");
        string cached = CopyTable(seed, dir.Path, "cached.dbf");
        string control = CopyTable(seed, dir.Path, "control.dbf");
        var appended = Enumerable.Range(1000, 80).Select(Row).ToArray();

        using (var w = DbfWriter.Open(cached))
        {
            foreach (var row in appended)
                w.AppendRecord(row);
            Assert.True(w.CdxAppendPageCachePageCountForTests > 0);
        }

        foreach (var row in appended)
        {
            using var w = DbfWriter.Open(control);
            w.AppendRecord(row);
        }

        Assert.Equal(IndexMaintTestSupport.Recnos(control, "IDTAG"), IndexMaintTestSupport.Recnos(cached, "IDTAG"));
        Assert.Equal(IndexMaintTestSupport.StrKeys(control, "NAMETAG"), IndexMaintTestSupport.StrKeys(cached, "NAMETAG"));
        Assert.Equal(File.ReadAllBytes(Cdx(control)), File.ReadAllBytes(Cdx(cached)));
    }

    [Fact]
    public void AppendUpdateAppend_SwitchesModesAndMatchesFreshWriterControl()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        string seed = CreateSeed(dir.Path, "seed.dbf");
        string cached = CopyTable(seed, dir.Path, "cached.dbf");
        string control = CopyTable(seed, dir.Path, "control.dbf");

        using (var w = DbfWriter.Open(cached))
        {
            w.AppendRecord(Row(1000));
            Assert.True(w.CdxAppendPageCachePageCountForTests > 0);

            w.UpdateRecord(1, Row(2000));
            Assert.Equal(0, w.CdxAppendPageCachePageCountForTests);

            w.AppendRecord(Row(1001));
            Assert.True(w.CdxAppendPageCachePageCountForTests > 0);
        }

        using (var w = DbfWriter.Open(control))
            w.AppendRecord(Row(1000));
        using (var w = DbfWriter.Open(control))
            w.UpdateRecord(1, Row(2000));
        using (var w = DbfWriter.Open(control))
            w.AppendRecord(Row(1001));

        Assert.Equal(IndexMaintTestSupport.Recnos(control, "IDTAG"), IndexMaintTestSupport.Recnos(cached, "IDTAG"));
        Assert.Equal(IndexMaintTestSupport.StrKeys(control, "NAMETAG"), IndexMaintTestSupport.StrKeys(cached, "NAMETAG"));
        Assert.Equal(File.ReadAllBytes(Cdx(control)), File.ReadAllBytes(Cdx(cached)));
    }

    [Fact]
    public void Flush_ClearsAppendPageCache()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        string dbf = CopyTable(CreateSeed(dir.Path, "seed.dbf"), dir.Path, "t.dbf");

        using var w = DbfWriter.Open(dbf);
        w.AppendRecord(Row(1000));
        Assert.True(w.CdxAppendPageCachePageCountForTests > 0);

        w.Flush();

        Assert.Equal(0, w.CdxAppendPageCachePageCountForTests);
    }

    [Fact]
    public void BatchAppend_ClearsAppendPageCacheBeforeItsOwnEditor()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        string dbf = CopyTable(CreateSeed(dir.Path, "seed.dbf"), dir.Path, "t.dbf");

        using var w = DbfWriter.Open(dbf);
        w.AppendRecord(Row(1000));
        Assert.True(w.CdxAppendPageCachePageCountForTests > 0);

        w.AppendRecords(new[] { Row(1001), Row(1002) });

        Assert.Equal(0, w.CdxAppendPageCachePageCountForTests);
    }

    [Fact]
    public void TagDdl_ClearsAppendPageCache()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        string dbf = CopyTable(CreateSeed(dir.Path, "seed.dbf"), dir.Path, "t.dbf");

        using var w = DbfWriter.Open(dbf);
        w.AppendRecord(Row(1000));
        Assert.True(w.CdxAppendPageCachePageCountForTests > 0);

        w.CreateTag(new CdxTagDefinition("GRPTAG", "GRP"));

        Assert.Equal(0, w.CdxAppendPageCachePageCountForTests);
    }

    [Fact]
    public void Pack_ClearsAppendPageCache()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        string dbf = CopyTable(CreateSeed(dir.Path, "seed.dbf"), dir.Path, "t.dbf");

        using var w = DbfWriter.Open(dbf);
        w.AppendRecord(Row(1000));
        Assert.True(w.CdxAppendPageCachePageCountForTests > 0);

        w.Delete(0);
        w.Pack();

        Assert.Equal(0, w.CdxAppendPageCachePageCountForTests);
    }

    [Fact]
    public void Alter_ClearsAppendPageCache()
    {
        using var dir = new IndexMaintTestSupport.TempDir();
        string dbf = CopyTable(CreateSeed(dir.Path, "seed.dbf"), dir.Path, "t.dbf");

        using var w = DbfWriter.Open(dbf);
        w.AppendRecord(Row(1000));
        Assert.True(w.CdxAppendPageCachePageCountForTests > 0);

        w.Alter(new[]
        {
            new DbfColumnDef("ID", 'I'),
            new DbfColumnDef("NAME", 'C', 12),
            new DbfColumnDef("GRP", 'C', 4),
            new DbfColumnDef("NOTE", 'C', 8),
        });

        Assert.Equal(0, w.CdxAppendPageCachePageCountForTests);
    }

    private static string CreateSeed(string dir, string name)
        => IndexMaintTestSupport.CreateTable(dir, name, Columns,
            Enumerable.Range(1, 96).Select(Row),
            new CdxTagDefinition("IDTAG", "ID"),
            new CdxTagDefinition("NAMETAG", "NAME"));

    private static object?[] Row(int id)
        => new object?[] { id, Name(id), "G" + (id % 7).ToString("D2") };

    private static string Name(int id)
    {
        uint mixed = unchecked((uint)(id * 2654435761));
        return mixed.ToString("X8") + (id % 10000).ToString("D4");
    }

    private static string CopyTable(string sourceDbf, string dir, string name)
    {
        string targetDbf = Path.Combine(dir, name);
        File.Copy(sourceDbf, targetDbf, overwrite: true);
        File.Copy(Cdx(sourceDbf), Cdx(targetDbf), overwrite: true);
        return targetDbf;
    }

    private static string Cdx(string dbf) => Path.ChangeExtension(dbf, ".cdx");
}
