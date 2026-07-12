using System;
using System.Buffers.Binary;
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
    public void KeepValue_PreservesMemoPointers_FptLength_AndNullFlags()
    {
        string path = _dir.File("keep_memos.dbf");
        byte[] general = Enumerable.Range(0, 91).Select(i => (byte)(i * 3)).ToArray();
        byte[] picture = Enumerable.Range(0, 73).Select(i => (byte)(255 - i)).ToArray();
        using (var writer = DbfWriter.Create(path,
        [
            new DbfColumnDef("ID", 'I'),
            new DbfColumnDef("NOTE", 'C', 12),
            new DbfColumnDef("MEMO", 'M', nullable: true),
            new DbfColumnDef("GEN", 'G', nullable: true),
            new DbfColumnDef("PIC", 'P', nullable: true),
            new DbfColumnDef("OPTIONAL", 'I', nullable: true),
        ], new DbfCreateOptions { Overwrite = true }))
            writer.AppendRecord(1, "before", "memo text", general, picture, null);

        string fpt = Path.ChangeExtension(path, ".fpt");
        long fptLengthBefore = new FileInfo(fpt).Length;

        static (int[] Pointers, byte NullFlags) Snapshot(string dbf)
        {
            using var table = DbfTable.Open(dbf, new DbfOptions { ExposeSystemColumns = true });
            var record = table.GetRecord(0)!.Value;
            int Pointer(string name) => BinaryPrimitives.ReadInt32LittleEndian(
                record.GetRawField(table.Columns.Single(c => c.Name == name)));
            var nullFlags = table.Columns.Single(c => c.Name == "_NullFlags");
            return ([Pointer("MEMO"), Pointer("GEN"), Pointer("PIC")], record.GetRawField(nullFlags)[0]);
        }

        var before = Snapshot(path);
        Assert.All(before.Pointers, pointer => Assert.NotEqual(0, pointer));
        Assert.Equal(0b0000_1000, before.NullFlags);
        using (var writer = DbfWriter.Open(path))
            writer.UpdateRecord(0,
            [
                DbfWriter.KeepValue,
                "changed",
                DbfWriter.KeepValue,
                DbfWriter.KeepValue,
                DbfWriter.KeepValue,
                DbfWriter.KeepValue,
            ]);
        var after = Snapshot(path);

        Assert.Equal(before.Pointers, after.Pointers);
        Assert.Equal(before.NullFlags, after.NullFlags);
        Assert.Equal(fptLengthBefore, new FileInfo(fpt).Length);
        using var updated = DbfTable.Open(path);
        Assert.Equal("changed", ((string)updated.GetRecord(0)!.Value["NOTE"]!).TrimEnd());
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
