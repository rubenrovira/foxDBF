using System.Buffers.Binary;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class DbfWriterSharedMemoReservationTests
{
    [Fact]
    public void PreopenedSharedWriters_AppendingMemos_ReserveDistinctBlocks()
    {
        using var fixture = new MemoFixture();
        using var first = fixture.OpenShared();
        using var second = fixture.OpenShared();

        uint before = fixture.ReadNextFree();
        first.AppendRecord(1, "memo from first append");
        uint afterFirst = fixture.ReadNextFree();
        second.AppendRecord(2, "memo from second append");
        uint afterSecond = fixture.ReadNextFree();

        Assert.True(afterFirst > before);
        Assert.True(afterSecond > afterFirst);

        var records = fixture.ReadRecords();
        int[] pointers = fixture.ReadMemoPointers(records);
        Assert.All(pointers, pointer => Assert.True(pointer > 0));
        Assert.Equal(2, pointers.Distinct().Count());
        Assert.Equal("memo from first append", fixture.ReadMemoText(pointers[0]));
        Assert.Equal("memo from second append", fixture.ReadMemoText(pointers[1]));
        Assert.True(afterSecond > (uint)pointers.Max());

        Assert.Equal(new[] { 1, 2 }, fixture.ReadValues("ID").Select(Convert.ToInt32).ToArray());
        Assert.Equal(new[] { "memo from first append", "memo from second append" },
            fixture.ReadValues("NOTE"));
    }

    [Fact]
    public void PreopenedSharedWriters_UpdatingDifferentRows_ReserveDistinctBlocks()
    {
        using var fixture = new MemoFixture((1, "original one"), (2, "original two"));
        using var first = fixture.OpenShared();
        using var second = fixture.OpenShared();

        uint before = fixture.ReadNextFree();
        first.UpdateRecord(0, new object?[] { 1, "memo from first update" });
        uint afterFirst = fixture.ReadNextFree();
        second.UpdateRecord(1, new object?[] { 2, "memo from second update" });
        uint afterSecond = fixture.ReadNextFree();

        Assert.True(afterFirst > before);
        Assert.True(afterSecond > afterFirst);

        var records = fixture.ReadRecords();
        int[] pointers = fixture.ReadMemoPointers(records);
        Assert.All(pointers, pointer => Assert.True(pointer > 0));
        Assert.Equal(2, pointers.Distinct().Count());
        Assert.Equal("memo from first update", fixture.ReadMemoText(pointers[0]));
        Assert.Equal("memo from second update", fixture.ReadMemoText(pointers[1]));
        Assert.True(afterSecond > (uint)pointers.Max());

        Assert.Equal(new[] { "memo from first update", "memo from second update" },
            fixture.ReadValues("NOTE"));
    }

    [Fact]
    public void SharedWriter_UnrepresentableNextFree_ThrowsWithoutMutatingFiles()
    {
        using var fixture = new MemoFixture();
        fixture.WriteNextFree(uint.MaxValue);
        uint beforeNextFree = fixture.ReadNextFree();
        uint beforeRecordCount = fixture.ReadRecordCount();
        long beforeDbfLength = new FileInfo(fixture.DbfPath).Length;
        long beforeFptLength = new FileInfo(fixture.FptPath).Length;

        using var writer = fixture.OpenShared();
        Assert.Throws<DbfWriteException>(() => writer.AppendRecord(1, "must not be written"));

        Assert.Equal(beforeNextFree, fixture.ReadNextFree());
        Assert.Equal(beforeRecordCount, fixture.ReadRecordCount());
        Assert.Equal(beforeDbfLength, new FileInfo(fixture.DbfPath).Length);
        Assert.Equal(beforeFptLength, new FileInfo(fixture.FptPath).Length);
    }

    private sealed class MemoFixture : IDisposable
    {
        private readonly string _dir;
        internal string DbfPath { get; }
        internal string FptPath => Path.ChangeExtension(DbfPath, ".fpt");

        internal MemoFixture(params (int Id, string Memo)[] rows)
        {
            _dir = Path.Combine(Path.GetTempPath(), "foxdbf_shared_memo_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            DbfPath = Path.Combine(_dir, "memo.dbf");
            using var writer = DbfWriter.Create(DbfPath,
            [
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("NOTE", 'M', 4),
            ]);
            foreach (var row in rows)
                writer.AppendRecord(row.Id, row.Memo);
        }

        internal DbfWriter OpenShared()
            => DbfWriter.Open(DbfPath, new DbfOptions { LockMode = LockMode.Shared });

        internal uint ReadNextFree()
            => BinaryPrimitives.ReadUInt32BigEndian(ReadFptBytes().AsSpan(0, 4));

        internal void WriteNextFree(uint value)
        {
            Span<byte> header = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, value);
            using var stream = new FileStream(FptPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            stream.Write(header);
            stream.Flush(flushToDisk: true);
        }

        internal uint ReadRecordCount()
        {
            Span<byte> count = stackalloc byte[4];
            using var stream = new FileStream(DbfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Position = 4;
            stream.ReadExactly(count);
            return BinaryPrimitives.ReadUInt32LittleEndian(count);
        }

        internal DbfRecord[] ReadRecords()
        {
            using var table = DbfTable.Open(DbfPath);
            return table.EnumerateAll(includeDeleted: true).ToArray();
        }

        internal object?[] ReadValues(string columnName)
        {
            using var table = DbfTable.Open(DbfPath);
            return table.EnumerateAll(includeDeleted: true).Select(record => record[columnName]).ToArray();
        }

        internal int[] ReadMemoPointers(IReadOnlyList<DbfRecord> records)
        {
            using var table = DbfTable.Open(DbfPath);
            var column = table.Columns.Single(c => c.Name == "NOTE");
            return records.Select(record => BinaryPrimitives.ReadInt32LittleEndian(
                record.Raw.Slice(column.Offset + 1, 4))).ToArray();
        }

        internal string ReadMemoText(int pointer)
        {
            byte[] fpt = ReadFptBytes();
            int blockSize = BinaryPrimitives.ReadUInt16BigEndian(fpt.AsSpan(6, 2));
            int offset = checked(pointer * blockSize);
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(fpt.AsSpan(offset + 4, 4)));
            return System.Text.Encoding.GetEncoding(1252).GetString(fpt, offset + 8, length);
        }

        private byte[] ReadFptBytes()
        {
            using var stream = new FileStream(FptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            return bytes;
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }
    }
}
