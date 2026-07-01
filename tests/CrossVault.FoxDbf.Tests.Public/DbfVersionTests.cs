namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// Pins the version-byte → format-config lookup (plan §A4 VERSIONS table).
/// Focus is on the boundaries that bite: the three header sizes (8/32/68),
/// the three descriptor widths (16/32/48), the foxpro flag, the memo flavour,
/// and — critically — that an unknown/garbage version byte resolves to a safe
/// default instead of throwing.
/// </summary>
public class DbfVersionTests
{
    // byte, description-ish, isFoxpro, headerSize, descriptorWidth, memoKind
    public static IEnumerable<object[]> KnownVersions() =>
    [
        [(byte)0x02, false, 8,  16, MemoKind.None],   // FoxBase / dBase II
        [(byte)0x03, false, 32, 32, MemoKind.None],   // dBase III, no memo
        [(byte)0x04, false, 32, 32, MemoKind.None],   // dBase IV, no memo (§A13: 32/32 default, the 68/48 Gem quirk is overridden)
        [(byte)0x05, false, 32, 32, MemoKind.None],   // dBase V, no memo
        [(byte)0x30, true,  32, 32, MemoKind.Foxpro], // Visual FoxPro
        [(byte)0x31, true,  32, 32, MemoKind.Foxpro], // VFP + AutoIncrement
        [(byte)0x32, true,  32, 32, MemoKind.Foxpro], // VFP + Varchar/Varbinary
        [(byte)0x7b, false, 32, 32, MemoKind.Dbase4], // dBase IV with memo
        [(byte)0x83, false, 32, 32, MemoKind.Dbase3], // dBase III with memo
        [(byte)0x8b, false, 32, 32, MemoKind.Dbase4], // dBase IV with memo
        [(byte)0x8c, false, 68, 48, MemoKind.Dbase4], // dBase 7
        [(byte)0xf5, true,  32, 32, MemoKind.Foxpro], // FoxPro with memo
        [(byte)0xfb, true,  32, 32, MemoKind.None],   // FoxPro without memo
    ];

    [Theory]
    [MemberData(nameof(KnownVersions))]
    public void FromByte_maps_known_versions(byte code, bool isFoxpro, int headerSize, int descriptorWidth, MemoKind memo)
    {
        var v = DbfVersion.FromByte(code);

        Assert.Equal(code, v.Code);
        Assert.Equal(isFoxpro, v.IsFoxpro);
        Assert.Equal(headerSize, v.HeaderSize);
        Assert.Equal(descriptorWidth, v.DescriptorWidth);
        Assert.Equal(memo, v.MemoKind);
        Assert.False(string.IsNullOrWhiteSpace(v.Description));
    }

    [Theory]
    [InlineData((byte)0x03, "03")]
    [InlineData((byte)0x30, "30")]
    [InlineData((byte)0xf5, "f5")]
    [InlineData((byte)0x02, "02")]
    public void Hex_is_lowercase_two_digits(byte code, string expected)
        => Assert.Equal(expected, DbfVersion.FromByte(code).Hex);

    // The four header sizes / descriptor widths are the load-bearing distinctions;
    // pin the unusual 68/48 dBase-7 case and the 8/16 FoxBase case explicitly.
    [Fact]
    public void FoxBase_v02_has_eight_byte_header_and_sixteen_byte_descriptor()
    {
        var v = DbfVersion.FromByte(0x02);
        Assert.Equal(8, v.HeaderSize);
        Assert.Equal(16, v.DescriptorWidth);
    }

    [Fact]
    public void Dbase7_0x8c_uses_68_byte_header_and_48_byte_descriptor()
    {
        // Only 0x8c keeps the 68/48 layout (verified against dbase_8c.dbf).
        var v = DbfVersion.FromByte(0x8c);
        Assert.Equal(68, v.HeaderSize);
        Assert.Equal(48, v.DescriptorWidth);
    }

    [Fact]
    public void Dbase4_0x04_uses_32_byte_header_and_32_byte_descriptor()
    {
        // §A13 (plan line 466): the 68/48 mapping for 0x04 is a Gem quirk; real
        // dBase IV 0x04 files are usually 32-byte, so 32/32 is the default path.
        var v = DbfVersion.FromByte(0x04);
        Assert.Equal(32, v.HeaderSize);
        Assert.Equal(32, v.DescriptorWidth);
    }

    // §A5b (M3 correction, empirically verified by dbase_f5.dbf): only the Visual
    // FoxPro versions 0x30/0x31/0x32 carry the 263-byte backlink block — the FoxPro
    // f5/fb versions are foxpro but have NO backlink, and neither do the dBase
    // versions. Backlink-dependent geometry (§A8/§A12) MUST key off the version byte
    // (HasBacklink), not the broad IsFoxpro family flag.
    [Theory]
    [InlineData((byte)0x30, true)]
    [InlineData((byte)0x31, true)]
    [InlineData((byte)0x32, true)]
    [InlineData((byte)0xf5, false)]
    [InlineData((byte)0xfb, false)]
    [InlineData((byte)0x83, false)]
    [InlineData((byte)0x03, false)]
    public void HasBacklink_is_scoped_to_vfp_version_bytes_not_the_foxpro_family(byte code, bool hasBacklink)
        => Assert.Equal(hasBacklink, DbfVersion.FromByte(code).HasBacklink);

    [Fact]
    public void Foxpro_fb_is_the_foxpro_no_memo_no_backlink_discriminator()
    {
        // 0xfb is the one foxpro version that is foxpro=true, memo=None AND
        // backlink=false — the discriminator that proves IsFoxpro and HasBacklink
        // are independent, and that foxpro does not imply a memo file.
        var v = DbfVersion.FromByte(0xfb);
        Assert.True(v.IsFoxpro);
        Assert.Equal(MemoKind.None, v.MemoKind);
        Assert.False(v.HasBacklink);
    }

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0x99)]
    [InlineData((byte)0xab)]
    [InlineData((byte)0xff)]
    public void Unknown_version_does_not_throw_and_falls_back_to_safe_defaults(byte garbage)
    {
        // Must NOT throw — robustness contract (§A12). A standard 32/32 no-memo
        // layout is the safe assumption for an unrecognised byte.
        var v = DbfVersion.FromByte(garbage);

        Assert.Equal(garbage, v.Code);
        Assert.Equal(32, v.HeaderSize);
        Assert.Equal(32, v.DescriptorWidth);
        Assert.Equal(MemoKind.None, v.MemoKind);
        Assert.False(v.IsFoxpro);
        Assert.False(string.IsNullOrEmpty(v.Description));
    }
}
