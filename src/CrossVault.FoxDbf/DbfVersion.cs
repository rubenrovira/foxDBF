namespace CrossVault.FoxDbf;

/// <summary>
/// Maps a DBF version byte (header offset 0) to its on-disk format configuration:
/// header size, field-descriptor width and memo flavour (plan §A4 VERSIONS table).
/// Unknown/garbage version bytes never throw — they resolve to a safe default
/// (32-byte header, 32-byte descriptor, no memo) so header parsing stays robust (§A10/§A12).
/// </summary>
public readonly record struct DbfVersion
{
    /// <summary>The raw version byte from header offset 0.</summary>
    public required byte Code { get; init; }

    /// <summary>Human-readable description, e.g. "Visual FoxPro".</summary>
    public required string Description { get; init; }

    /// <summary>True for the FoxPro family (0x30/0x31/0x32/0xf5/0xfb).</summary>
    public required bool IsFoxpro { get; init; }

    /// <summary>Bytes before the field-descriptor array: 8 (v0x02), 32 (standard) or 68 (dBase IV/7).</summary>
    public required int HeaderSize { get; init; }

    /// <summary>Per-field descriptor width: 16 (v0x02), 32 (standard) or 48 (dBase 7).</summary>
    public required int DescriptorWidth { get; init; }

    /// <summary>The memo flavour for this version (None when no memo file applies).</summary>
    public required MemoKind MemoKind { get; init; }

    /// <summary>The version byte as a lower-case 2-digit hex string ("03", "30", "f5").</summary>
    public string Hex => Code.ToString("x2");

    /// <summary>
    /// True for the Visual FoxPro versions (0x30/0x31/0x32) that carry the 263-byte
    /// backlink block at the tail of the header. This is keyed off the version byte,
    /// NOT <see cref="IsFoxpro"/>: the FoxPro f5/fb versions are foxpro but have NO
    /// backlink (plan §A5b, M3 correction — verified by fixture dbase_f5.dbf). Use
    /// this (not IsFoxpro) for backlink-dependent geometry (§A8/§A12).
    /// </summary>
    public bool HasBacklink => Code is 0x30 or 0x31 or 0x32;

    /// <summary>
    /// True when the version byte maps to a known dBase/FoxPro layout (not the
    /// "Unknown" fallback). A normal <see cref="DbfTable.Open(string)"/> rejects an
    /// unrecognized version (e.g. the encrypted <c>V_usr.dbf</c> <c>0xee</c>) with
    /// <see cref="DbfUnsupportedVersionException"/> unless overridden via
    /// <see cref="DbfOptions.ForceVersion"/> or <see cref="Recovery.Reconstruct"/> (§A12).
    /// </summary>
    public bool IsRecognized => Description != "Unknown";

    /// <summary>Resolve a version byte to its format configuration. Never throws.</summary>
    public static DbfVersion FromByte(byte versionByte)
    {
        // (description, isFoxpro, headerSize, descriptorWidth, memoKind)
        (string Text, bool Fox, int Hdr, int Width, MemoKind Memo) m = versionByte switch
        {
            0x02 => ("FoxBase", false, 8, 16, MemoKind.None),
            0x03 => ("dBase III without memo", false, 32, 32, MemoKind.None),
            // §A13 (line 466): the 68/48 dBase IV 0x04 descriptor is a Gem quirk;
            // real 0x04 files are usually 32-byte, so 32/32 is the default path.
            // (0x8c stays 68/48 — verified against dbase_8c.dbf.)
            0x04 => ("dBase IV without memo", false, 32, 32, MemoKind.None),
            0x05 => ("dBase V without memo", false, 32, 32, MemoKind.None),
            0x07 => ("Visual Objects without memo", false, 32, 32, MemoKind.None),
            0x30 => ("Visual FoxPro", true, 32, 32, MemoKind.Foxpro),
            0x31 => ("Visual FoxPro with AutoIncrement", true, 32, 32, MemoKind.Foxpro),
            0x32 => ("Visual FoxPro with Varchar/Varbinary", true, 32, 32, MemoKind.Foxpro),
            0x43 => ("dBASE IV SQL table without memo", false, 32, 32, MemoKind.None),
            0x63 => ("dBASE IV SQL system without memo", false, 32, 32, MemoKind.None),
            0x7b => ("dBase IV with memo", false, 32, 32, MemoKind.Dbase4),
            0x83 => ("dBase III with memo", false, 32, 32, MemoKind.Dbase3),
            0x87 => ("Visual Objects with memo", false, 32, 32, MemoKind.Dbase4),
            0x8b => ("dBase IV with memo", false, 32, 32, MemoKind.Dbase4),
            0x8c => ("dBase 7", false, 68, 48, MemoKind.Dbase4),
            0x8e => ("dBASE IV SQL system with memo", false, 32, 32, MemoKind.Dbase4),
            0xcb => ("dBASE IV SQL table with memo", false, 32, 32, MemoKind.Dbase4),
            0xf5 => ("FoxPro with memo", true, 32, 32, MemoKind.Foxpro),
            0xfb => ("FoxPro without memo", true, 32, 32, MemoKind.None),
            _ => ("Unknown", false, 32, 32, MemoKind.None),
        };

        return new DbfVersion
        {
            Code = versionByte,
            Description = m.Text,
            IsFoxpro = m.Fox,
            HeaderSize = m.Hdr,
            DescriptorWidth = m.Width,
            MemoKind = m.Memo,
        };
    }
}
