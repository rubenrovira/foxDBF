using System.Buffers.Binary;

namespace CrossVault.FoxDbf;

/// <summary>
/// The parsed DBF table header (plan §A4). Two physical layouts are supported:
/// the 32-byte standard header (all versions except 0x02), and the 8-byte
/// FoxBase/dBase II header (version 0x02) where record count lives at offset 1,
/// record length at offset 6, the header length is the fixed constant 521 and
/// there is no code-page byte. All multi-byte integers are little-endian.
/// </summary>
public readonly record struct DbfHeader
{
    /// <summary>The fixed header length used for version 0x02 (FoxBase / dBase II).</summary>
    public const int FoxBaseHeaderLength = 521;

    /// <summary>The raw version byte (header offset 0).</summary>
    public required byte VersionByte { get; init; }

    /// <summary>The resolved version configuration (format/memo lookup).</summary>
    public required DbfVersion Version { get; init; }

    /// <summary>Number of records in the table (u32 @4, or u16 @1 for v0x02).</summary>
    public required int RecordCount { get; init; }

    /// <summary>Byte offset of the first data record (u16 @8, or fixed 521 for v0x02).</summary>
    public required int HeaderLength { get; init; }

    /// <summary>Bytes per record incl. the leading delete flag (u16 @10, or u16 @6 for v0x02).</summary>
    public required int RecordLength { get; init; }

    /// <summary>Table-flags byte (offset 28). Always 0 for v0x02 (no such field).</summary>
    public required byte TableFlags { get; init; }

    /// <summary>Code-page / language-driver byte (offset 29). Always 0 for v0x02.</summary>
    public required byte CodePage { get; init; }

    /// <summary>Table-flags bit 0: a structural .CDX index accompanies the table.</summary>
    public bool HasStructuralCdx => (TableFlags & 0x01) != 0;

    /// <summary>Table-flags bit 1: a memo file (.fpt/.dbt/.dct) accompanies the table.</summary>
    public bool HasMemoFlag => (TableFlags & 0x02) != 0;

    /// <summary>Table-flags bit 2: this file is a Visual FoxPro database container (.dbc).</summary>
    public bool IsDatabaseContainer => (TableFlags & 0x04) != 0;

    /// <summary>Parse a header from a byte span (must hold at least the version-appropriate header).</summary>
    public static DbfHeader Read(ReadOnlySpan<byte> source)
    {
        var versionByte = source[0];
        var version = DbfVersion.FromByte(versionByte);

        if (versionByte == 0x02)
        {
            // FoxBase / dBase II: 8-byte header, record count u16 @1, record length
            // u16 @6, header length is the fixed constant 521, no code-page byte.
            return new DbfHeader
            {
                VersionByte = versionByte,
                Version = version,
                RecordCount = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(1, 2)),
                HeaderLength = FoxBaseHeaderLength,
                RecordLength = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(6, 2)),
                TableFlags = 0,
                CodePage = 0,
            };
        }

        // Standard 32-byte header. All multi-byte integers are little-endian.
        return new DbfHeader
        {
            VersionByte = versionByte,
            Version = version,
            // Saturating cast: a real DBF never exceeds int.MaxValue records (2 GB file
            // limit), so a u32 above that is garbage/encrypted bytes (e.g. V_usr.dbf 0xee)
            // or a forced layout — clamp instead of throwing, so the §A12 version/recovery
            // gates in DbfTable.Open get to run rather than an OverflowException escaping.
            RecordCount = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(4, 4)), int.MaxValue),
            HeaderLength = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(8, 2)),
            RecordLength = BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(10, 2)),
            TableFlags = source[28],
            CodePage = source[29],
        };
    }

    /// <summary>Parse a header from a byte array.</summary>
    public static DbfHeader Read(byte[] bytes) => Read((ReadOnlySpan<byte>)bytes);

    /// <summary>Read and parse a header from the current position of a stream.</summary>
    public static DbfHeader Read(Stream stream)
    {
        // The standard header is 32 bytes; v0x02 only needs 8. Fill all 32 bytes
        // whenever available — this keeps the standard-header path robust against
        // chunked/short reads (GZipStream, NetworkStream, BufferedStream, a
        // constrained MemoryStream view) where a single Read may return < 32
        // bytes. With throwOnEndOfStream:false a genuinely truncated stream still
        // yields fewer bytes (tolerated by the v0x02 layout, which never exceeds
        // 8 bytes of header anyway).
        var buf = new byte[32];
        int read = stream.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
        return Read(buf.AsSpan(0, read));
    }
}
