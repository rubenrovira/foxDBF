using System.Buffers.Binary;

namespace CrossVault.FoxDbf.Index;

/// <summary>
/// The compact-leaf info block that follows the 12-byte node header on every
/// compact leaf page (plan §C4). Layout, relative to the start of the page:
/// <list type="bullet">
///   <item><c>FreeSpace</c> u16 LE @12</item>
///   <item><c>RecnoMask</c> 4 bytes LE @14</item>
///   <item><c>DupCountMask</c> byte @18</item>
///   <item><c>TrailCountMask</c> byte @19</item>
///   <item><c>RecnoBits</c> (cRN) byte @20</item>
///   <item><c>DupBits</c> (cDC) byte @21</item>
///   <item><c>TrailBits</c> (cTC) byte @22</item>
///   <item><c>BytesPerEntry</c> (kBy) byte @23</item>
/// </list>
/// The bit widths are persisted on disk and must be read here rather than
/// recomputed from key length + record count.
/// </summary>
public readonly struct LeafInfo
{
    /// <summary>Byte offset of the info block within a leaf page.</summary>
    public const int Offset = 12;

    /// <summary>Byte offset where the fixed-width entry array begins.</summary>
    public const int EntryArrayOffset = 24;

    /// <summary>Free space available in the page, @12 LE.</summary>
    public ushort FreeSpace { get; }

    /// <summary>Record-number mask (low <see cref="RecnoBits"/> bits set), @14 LE.</summary>
    public uint RecnoMask { get; }

    /// <summary>Duplicate-count mask (low <see cref="DupBits"/> bits set), @18.</summary>
    public byte DupCountMask { get; }

    /// <summary>Trailing-count mask (low <see cref="TrailBits"/> bits set), @19.</summary>
    public byte TrailCountMask { get; }

    /// <summary>Number of bits used for the record number (cRN), @20.</summary>
    public byte RecnoBits { get; }

    /// <summary>Number of bits used for the duplicate count (cDC), @21.</summary>
    public byte DupBits { get; }

    /// <summary>Number of bits used for the trailing count (cTC), @22.</summary>
    public byte TrailBits { get; }

    /// <summary>Bytes per entry-info record (kBy), @23.</summary>
    public byte BytesPerEntry { get; }

    /// <summary>
    /// True when the three bit widths exactly fill the per-entry byte budget,
    /// i.e. <c>RecnoBits + DupBits + TrailBits == BytesPerEntry * 8</c> (plan §C4).
    /// </summary>
    public bool IsBitWidthConsistent =>
        RecnoBits + DupBits + TrailBits == BytesPerEntry * 8;

    public LeafInfo(ushort freeSpace, uint recnoMask, byte dupCountMask, byte trailCountMask,
        byte recnoBits, byte dupBits, byte trailBits, byte bytesPerEntry)
    {
        FreeSpace = freeSpace;
        RecnoMask = recnoMask;
        DupCountMask = dupCountMask;
        TrailCountMask = trailCountMask;
        RecnoBits = recnoBits;
        DupBits = dupBits;
        TrailBits = trailBits;
        BytesPerEntry = bytesPerEntry;
    }

    /// <summary>
    /// Parses the leaf info block from a full 512-byte leaf page span.
    /// </summary>
    public static LeafInfo Parse(ReadOnlySpan<byte> page)
    {
        if (page.Length < EntryArrayOffset)
            return default;

        ushort freeSpace = BinaryPrimitives.ReadUInt16LittleEndian(page[12..]);
        uint recnoMask = BinaryPrimitives.ReadUInt32LittleEndian(page[14..]);
        byte dupCountMask = page[18];
        byte trailCountMask = page[19];
        byte recnoBits = page[20];
        byte dupBits = page[21];
        byte trailBits = page[22];
        byte bytesPerEntry = page[23];

        return new LeafInfo(freeSpace, recnoMask, dupCountMask, trailCountMask,
            recnoBits, dupBits, trailBits, bytesPerEntry);
    }
}
