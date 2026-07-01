namespace CrossVault.FoxDbf;

/// <summary>
/// The memo-file flavour associated with a DBF version byte. Drives which
/// memo reader (.dbt / .fpt) is used and how memo pointers are decoded (A6).
/// </summary>
public enum MemoKind
{
    /// <summary>No memo file is associated with this version.</summary>
    None = 0,

    /// <summary>dBase III memo (.dbt, fixed 512-byte blocks, 0x1A-terminated).</summary>
    Dbase3,

    /// <summary>dBase IV memo (.dbt, length-prefixed blocks).</summary>
    Dbase4,

    /// <summary>Visual FoxPro / FoxPro memo (.fpt, big-endian block header).</summary>
    Foxpro,
}
