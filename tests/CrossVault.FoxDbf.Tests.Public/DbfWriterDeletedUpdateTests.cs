using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.Index;
using CrossVault.FoxDbf.Sql;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

/// <summary>UPDATE changes field data only; Recall is the sole operation that clears deletion.</summary>
public sealed class DbfWriterDeletedUpdateTests : IDisposable
{
    private readonly SqlTestSupport.TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string CreateDeletedTable(string name = "deleted.dbf", bool indexed = false)
    {
        string path = _dir.File(name);
        using var writer = DbfWriter.Create(path, new[]
        {
            new DbfColumnDef("ID", 'I'),
            new DbfColumnDef("NAME", 'C', 10),
        }, new DbfCreateOptions { Overwrite = true });
        writer.AppendRecord(1, "ALPHA");
        if (indexed)
            writer.CreateTag(new CdxTagDefinition("NAMETAG", "NAME"));
        writer.Delete(0);
        return path;
    }

    private static byte RawMarker(string path)
    {
        int headerLength;
        using (var table = DbfTable.Open(path))
            headerLength = table.HeaderLength;
        using var stream = File.OpenRead(path);
        stream.Position = headerLength;
        int marker = stream.ReadByte();
        Assert.NotEqual(-1, marker);
        return (byte)marker;
    }

    private static (bool Deleted, int Id, string Name) ReadOnlyRow(string path)
    {
        using var table = DbfTable.Open(path);
        var row = Assert.Single(table.EnumerateAll(includeDeleted: true));
        return (row.IsDeleted, Convert.ToInt32(row["ID"]), ((string)row["NAME"]!).TrimEnd());
    }

    [Fact]
    public void PositionalFullUpdate_PreservesDeletedMarkerAndChangesData()
    {
        string path = CreateDeletedTable();

        using (var writer = DbfWriter.Open(path))
            writer.UpdateRecord(0, new object?[] { 2, "POSITION" });

        Assert.Equal(0x2A, RawMarker(path));
        Assert.Equal((true, 2, "POSITION"), ReadOnlyRow(path));
    }

    [Fact]
    public void DictionaryUpdate_PreservesDeletedMarkerAndChangesData()
    {
        string path = CreateDeletedTable();

        using (var writer = DbfWriter.Open(path))
            writer.UpdateRecord(0, new Dictionary<string, object?>
            {
                ["ID"] = 3,
                ["NAME"] = "DICTIONARY",
            });

        Assert.Equal(0x2A, RawMarker(path));
        Assert.Equal((true, 3, "DICTIONARY"), ReadOnlyRow(path));
    }

    [Fact]
    public void KeepValuePreservesDeletion_AndRecallExplicitlyClearsIt()
    {
        string path = CreateDeletedTable();

        using (var writer = DbfWriter.Open(path))
            writer.UpdateRecord(0, new object?[] { DbfWriter.KeepValue, "KEPT" });

        Assert.Equal(0x2A, RawMarker(path));
        Assert.Equal((true, 1, "KEPT"), ReadOnlyRow(path));

        using (var writer = DbfWriter.Open(path))
            writer.Recall(0);

        Assert.Equal(0x20, RawMarker(path));
        Assert.Equal((false, 1, "KEPT"), ReadOnlyRow(path));
    }

    [Fact]
    public void IndexedFullUpdate_PreservesDeletionAndMaintainsChangedKey()
    {
        string path = CreateDeletedTable(indexed: true);

        using (var writer = DbfWriter.Open(path))
            writer.UpdateRecord(0, new object?[] { 1, "ZULU" });

        Assert.Equal(0x2A, RawMarker(path));
        Assert.True(ReadOnlyRow(path).Deleted);
        Assert.Equal((uint)1, IndexMaintTestSupport.Seek(path, "NAMETAG", (object)"ZULU"));
        Assert.Null(IndexMaintTestSupport.Seek(path, "NAMETAG", (object)"ALPHA"));
    }

    [Fact]
    public void SqlUpdate_WithDeletedOff_PreservesDeletedRowEndToEnd()
    {
        string path = CreateDeletedTable("sql_deleted.dbf");
        using var session = new VfpSession(new EvaluationContext { Deleted = false });
        session.OpenDirectory(_dir.Path);

        var result = session.Execute(
            "UPDATE sql_deleted SET name = 'SQL' WHERE id = 1")!;

        Assert.Equal(1, result.AffectedRecords);
        Assert.Equal(0x2A, RawMarker(path));
        Assert.Equal((true, 1, "SQL"), ReadOnlyRow(path));
        Assert.Single(session.Execute("SELECT name FROM sql_deleted WHERE id = 1")!.Rows);
    }
}
