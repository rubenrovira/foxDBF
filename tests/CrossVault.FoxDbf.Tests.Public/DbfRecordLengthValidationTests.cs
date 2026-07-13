using System.Buffers.Binary;
using System.Text;

namespace CrossVault.FoxDbf.Tests;

public sealed class DbfRecordLengthValidationTests
{
    [Fact]
    public void Open_RecordLengthShorterThanPhysicalColumns_ThrowsAtOpenWithRecoveryGuidance()
    {
        using var dir = new MicroVfpTestSupport.TempDir("short_record_length");
        string path = WriteStandardTable(dir, declaredRecordLength: 2);

        var ex = Assert.Throws<DbfCorruptHeaderException>(() => DbfTable.Open(path));

        Assert.Contains("declared RecordLength=2", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("required=11", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Recovery.Reconstruct", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryOpen_RecordLengthShorterThanPhysicalColumns_ReturnsNullWithDiagnostic()
    {
        using var dir = new MicroVfpTestSupport.TempDir("short_record_tryopen");
        string path = WriteStandardTable(dir, declaredRecordLength: 2);

        using var table = DbfTable.TryOpen(path, out string? diagnostic);

        Assert.Null(table);
        Assert.NotNull(diagnostic);
        Assert.Contains("RecordLength=2", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("required=11", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Recovery.Reconstruct", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Tolerant_RecordLengthShorterThanPhysicalColumns_StillThrowsCorruptHeader()
    {
        using var dir = new MicroVfpTestSupport.TempDir("short_record_tolerant");
        string path = WriteStandardTable(dir, declaredRecordLength: 2);

        var ex = Assert.Throws<DbfCorruptHeaderException>(() =>
            DbfTable.Open(path, new DbfOptions { Recovery = Recovery.Tolerant }));

        Assert.Contains("RecordLength=2", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("required=11", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reconstruct_RecordLengthShorterThanPhysicalColumns_RebuildsAndReadsRecord()
    {
        using var dir = new MicroVfpTestSupport.TempDir("short_record_reconstruct");
        string path = WriteStandardTable(dir, declaredRecordLength: 2);

        using var table = DbfTable.Open(path, new DbfOptions { Recovery = Recovery.Reconstruct });

        Assert.Equal(11, table.RecordLength);
        Assert.Equal(1, table.RecordCount);
        Assert.Equal("ABCDEFGHIJ", table.GetRecord(0)!.Value.GetString("NAME"));
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    public void RecordLengthEqualToOrLongerThanPhysicalColumns_OpensAndDecodes(int declaredRecordLength)
    {
        using var dir = new MicroVfpTestSupport.TempDir("record_length_slack");
        string path = WriteStandardTable(dir, declaredRecordLength);

        using var table = DbfTable.Open(path);

        Assert.Equal(declaredRecordLength, table.RecordLength);
        Assert.Equal("ABCDEFGHIJ", table.GetRecord(0)!.Value.GetString("NAME"));
    }

    [Fact]
    public void Validation_CountsHiddenNullFlagsBeforeDefaultSystemColumnHiding()
    {
        using var dir = new MicroVfpTestSupport.TempDir("record_length_nullflags");
        string path = WriteVfpTableWithNullFlags(dir, declaredRecordLength: 11);

        var ex = Assert.Throws<DbfCorruptHeaderException>(() => DbfTable.Open(path));

        Assert.Contains("RecordLength=11", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("required=12", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Open_missing_descriptor_terminator_throws_typed_corrupt_header_error()
    {
        using var dir = new MicroVfpTestSupport.TempDir("missing_descriptor_terminator");
        string path = WriteStandardTable(dir, declaredRecordLength: 11);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[64] = 0x00;
        File.WriteAllBytes(path, bytes);

        var ex = Assert.Throws<DbfCorruptHeaderException>(() => DbfTable.Open(path));

        Assert.Contains("descriptor terminator", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0x0D", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string WriteStandardTable(MicroVfpTestSupport.TempDir dir, int declaredRecordLength)
    {
        const int headerLength = 65;
        int physicalRecordLength = Math.Max(11, declaredRecordLength);
        byte[] bytes = new byte[headerLength + physicalRecordLength + 1];
        WriteHeader(bytes, version: 0x03, headerLength, declaredRecordLength);
        WriteDescriptor(bytes.AsSpan(32, 32), "NAME", 'C', length: 10);
        bytes[64] = 0x0D;
        bytes[headerLength] = 0x20;
        Encoding.ASCII.GetBytes("ABCDEFGHIJ").CopyTo(bytes.AsSpan(headerLength + 1));
        bytes[^1] = 0x1A;
        string path = dir.File("probe.dbf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string WriteVfpTableWithNullFlags(MicroVfpTestSupport.TempDir dir, int declaredRecordLength)
    {
        const int headerLength = 32 + 2 * 32 + 1 + 263;
        const int actualRecordLength = 12;
        byte[] bytes = new byte[headerLength + actualRecordLength + 1];
        WriteHeader(bytes, version: 0x30, headerLength, declaredRecordLength);
        WriteDescriptor(bytes.AsSpan(32, 32), "NAME", 'C', length: 10, flags: 0x02);
        WriteDescriptor(bytes.AsSpan(64, 32), "_NullFlags", '0', length: 1, flags: 0x01);
        bytes[96] = 0x0D;
        bytes[headerLength] = 0x20;
        Encoding.ASCII.GetBytes("ABCDEFGHIJ").CopyTo(bytes.AsSpan(headerLength + 1));
        bytes[headerLength + 11] = 0;
        bytes[^1] = 0x1A;
        string path = dir.File("nullable.dbf");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void WriteHeader(Span<byte> bytes, byte version, int headerLength, int recordLength)
    {
        bytes[0] = version;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(4, 4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(8, 2), checked((ushort)headerLength));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(10, 2), checked((ushort)recordLength));
    }

    private static void WriteDescriptor(Span<byte> descriptor, string name, char type, byte length, byte flags = 0)
    {
        Encoding.ASCII.GetBytes(name).CopyTo(descriptor);
        descriptor[11] = (byte)type;
        descriptor[16] = length;
        descriptor[18] = flags;
    }
}
