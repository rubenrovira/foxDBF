using System.Buffers.Binary;

namespace CrossVault.FoxDbf.Tests;

public sealed class DbfHeaderTruncationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(12)]
    [InlineData(20)]
    [InlineData(31)]
    public void Truncated_standard_header_path_has_typed_open_failure_and_try_open_diagnostic(int length)
    {
        string path = Path.Combine(Path.GetTempPath(), $"foxdbf-short-header-{Guid.NewGuid():N}.dbf");
        File.WriteAllBytes(path, CreateStandardHeader()[..length]);

        try
        {
            using var table = DbfTable.TryOpen(path, out string? diagnostic);

            Assert.Null(table);
            Assert.False(string.IsNullOrWhiteSpace(diagnostic));
            Assert.Contains("header", diagnostic, StringComparison.OrdinalIgnoreCase);
            Assert.Throws<DbfCorruptHeaderException>(() => DbfTable.Open(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(12)]
    [InlineData(20)]
    [InlineData(31)]
    public void Truncated_standard_header_span_throws_corrupt_header(int length)
    {
        byte[] header = CreateStandardHeader();

        Assert.Throws<DbfCorruptHeaderException>(() => DbfHeader.Read(header.AsSpan(0, length)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(12)]
    [InlineData(20)]
    [InlineData(31)]
    public void Truncated_standard_header_stream_throws_corrupt_header(int length)
    {
        using var stream = new MemoryStream(CreateStandardHeader()[..length]);

        Assert.Throws<DbfCorruptHeaderException>(() => DbfHeader.Read(stream));
    }

    [Fact]
    public void FoxBase_header_requires_eight_bytes()
    {
        byte[] header = CreateFoxBaseHeader();

        Assert.Throws<DbfCorruptHeaderException>(() => DbfHeader.Read(header.AsSpan(0, 7)));

        DbfHeader parsed = DbfHeader.Read(header);
        Assert.Equal(0x02, parsed.VersionByte);
        Assert.Equal(3, parsed.RecordCount);
        Assert.Equal(9, parsed.RecordLength);
    }

    [Fact]
    public void Standard_header_requires_thirty_two_bytes()
    {
        byte[] header = CreateStandardHeader();

        Assert.Throws<DbfCorruptHeaderException>(() => DbfHeader.Read(header.AsSpan(0, 31)));

        DbfHeader parsed = DbfHeader.Read(header);
        Assert.Equal(0x03, parsed.VersionByte);
        Assert.Equal(5, parsed.RecordCount);
        Assert.Equal(33, parsed.HeaderLength);
        Assert.Equal(11, parsed.RecordLength);
    }

    private static byte[] CreateStandardHeader()
    {
        var header = new byte[32];
        header[0] = 0x03;
        header[1] = 126;
        header[2] = 7;
        header[3] = 11;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), 5);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8, 2), 33);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10, 2), 11);
        return header;
    }

    private static byte[] CreateFoxBaseHeader()
    {
        var header = new byte[8];
        header[0] = 0x02;
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(1, 2), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6, 2), 9);
        return header;
    }
}
