using System.Buffers.Binary;
using CrossVault.FoxDbf.Write;

namespace CrossVault.FoxDbf.Tests;

public sealed class DbfWriterPackPictureTests
{
    [Fact]
    public void Pack_MixedMemoAndPicture_RelocatesLivePicturesAndPreservesPayloads()
    {
        string dir = FreshTempDir();
        string dbf = Path.Combine(dir, "mixed.dbf");
        string fpt = Path.ChangeExtension(dbf, ".fpt");
        byte[] deletedPicture = Payload(0x11, 37);
        byte[] pictureTwo = Payload(0x22, 73);
        byte[] pictureThree = Payload(0x33, 700);

        try
        {
            using (var writer = DbfWriter.Create(dbf,
            [
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("NOTE", 'M', 4),
                new DbfColumnDef("PIC", 'P', 4),
            ]))
            {
                writer.AppendRecord(1, "drop memo", deletedPicture);
                writer.AppendRecord(2, "keep memo two", pictureTwo);
                writer.AppendRecord(3, "keep memo three", pictureThree);
            }

            int originalTwoPointer;
            int originalThreePointer;
            using (var table = DbfTable.Open(dbf))
            {
                var pic = table.Columns.Single(c => c.Name == "PIC");
                var records = table.EnumerateAll(includeDeleted: true).ToArray();
                originalTwoPointer = ReadPointer(records[1], pic);
                originalThreePointer = ReadPointer(records[2], pic);
            }

            using (var writer = DbfWriter.Open(dbf, new DbfOptions { LockMode = LockMode.Exclusive }))
            {
                writer.Delete(0);
                writer.Pack();
            }

            int pointerTwo;
            int pointerThree;
            using (var table = DbfTable.Open(dbf))
            {
                var pic = table.Columns.Single(c => c.Name == "PIC");
                var records = table.EnumerateAll(includeDeleted: true).ToArray();

                Assert.Equal(new[] { 2, 3 }, records.Select(r => Convert.ToInt32(r["ID"])).ToArray());
                Assert.Equal(new[] { "keep memo two", "keep memo three" },
                    records.Select(r => r["NOTE"]).ToArray());

                pointerTwo = ReadPointer(records[0], pic);
                pointerThree = ReadPointer(records[1], pic);
            }

            Assert.True(pointerTwo > 0);
            Assert.True(pointerThree > 0);
            Assert.NotEqual(originalTwoPointer, pointerTwo);
            Assert.NotEqual(originalThreePointer, pointerThree);

            byte[] rawFpt = File.ReadAllBytes(fpt);
            int blockSize = BinaryPrimitives.ReadUInt16BigEndian(rawFpt.AsSpan(6, 2));
            uint nextFree = BinaryPrimitives.ReadUInt32BigEndian(rawFpt.AsSpan(0, 4));
            Assert.Equal(pictureTwo, ReadPayload(rawFpt, pointerTwo, blockSize));
            Assert.Equal(pictureThree, ReadPayload(rawFpt, pointerThree, blockSize));
            Assert.DoesNotContain(ReadAllPayloads(rawFpt, blockSize, nextFree),
                payload => payload.AsSpan().SequenceEqual(deletedPicture));
            Assert.Equal((long)nextFree * blockSize, rawFpt.Length);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Pack_PictureOnlyTable_CompactsDeletedPictureBlock()
    {
        string dir = FreshTempDir();
        string dbf = Path.Combine(dir, "pictures.dbf");
        string fpt = Path.ChangeExtension(dbf, ".fpt");
        byte[] deletedPicture = Payload(0x44, 91);
        byte[] survivorPicture = Payload(0x55, 640);

        try
        {
            using (var writer = DbfWriter.Create(dbf,
            [
                new DbfColumnDef("ID", 'I', 4),
                new DbfColumnDef("PIC", 'P', 4),
            ]))
            {
                writer.AppendRecord(1, deletedPicture);
                writer.AppendRecord(2, survivorPicture);
                writer.AppendRecord(3, null);
            }

            int originalPointer;
            using (var table = DbfTable.Open(dbf))
            {
                var pic = table.Columns.Single(c => c.Name == "PIC");
                originalPointer = ReadPointer(table.GetRecord(1)!.Value, pic);
            }

            using (var writer = DbfWriter.Open(dbf, new DbfOptions { LockMode = LockMode.Exclusive }))
            {
                writer.Delete(0);
                writer.Pack();
            }

            int relocatedPointer;
            int nullPointer;
            using (var table = DbfTable.Open(dbf))
            {
                Assert.Equal(2, table.RecordCount);
                var records = table.EnumerateAll(includeDeleted: true).ToArray();
                Assert.Equal(new[] { 2, 3 }, records.Select(r => Convert.ToInt32(r["ID"])).ToArray());
                var pic = table.Columns.Single(c => c.Name == "PIC");
                relocatedPointer = ReadPointer(records[0], pic);
                nullPointer = ReadPointer(records[1], pic);
            }

            Assert.True(relocatedPointer > 0);
            Assert.NotEqual(originalPointer, relocatedPointer);
            Assert.Equal(0, nullPointer);

            byte[] rawFpt = File.ReadAllBytes(fpt);
            int blockSize = BinaryPrimitives.ReadUInt16BigEndian(rawFpt.AsSpan(6, 2));
            uint nextFree = BinaryPrimitives.ReadUInt32BigEndian(rawFpt.AsSpan(0, 4));
            Assert.Equal(survivorPicture, ReadPayload(rawFpt, relocatedPointer, blockSize));
            Assert.DoesNotContain(ReadAllPayloads(rawFpt, blockSize, nextFree),
                payload => payload.AsSpan().SequenceEqual(deletedPicture));
            Assert.Equal((long)nextFree * blockSize, rawFpt.Length);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static int ReadPointer(DbfRecord record, DbfColumn column)
        => BinaryPrimitives.ReadInt32LittleEndian(record.Raw.Slice(column.Offset + 1, 4));

    private static byte[] ReadPayload(byte[] fpt, int pointer, int blockSize)
    {
        int offset = checked(pointer * blockSize);
        int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(fpt.AsSpan(offset + 4, 4)));
        return fpt.AsSpan(offset + 8, length).ToArray();
    }

    private static List<byte[]> ReadAllPayloads(byte[] fpt, int blockSize, uint nextFree)
    {
        int block = Math.Max(1, (512 + blockSize - 1) / blockSize);
        var payloads = new List<byte[]>();
        while (block < nextFree)
        {
            byte[] payload = ReadPayload(fpt, block, blockSize);
            payloads.Add(payload);
            block += Math.Max(1, (8 + payload.Length + blockSize - 1) / blockSize);
        }
        return payloads;
    }

    private static byte[] Payload(byte seed, int length)
        => Enumerable.Range(0, length).Select(i => (byte)(seed + i * 17)).ToArray();

    private static string FreshTempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "foxdbf_pack_picture_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
