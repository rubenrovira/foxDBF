using System;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>
/// Dependency-free Windows-1252 (CP1252) codec. The engine is self-contained and
/// must not pull in <c>System.Text.Encoding.CodePages</c>, so the single byte to
/// char mapping is embedded here. CP1252 agrees with Latin-1 on 0x00-0x7F and
/// 0xA0-0xFF; only 0x80-0x9F carry the "smart" punctuation / ligature characters.
/// Used for index-key byte production (GENERAL collation) and for CHR()/ASC(),
/// which are code-page based in VFP (not Unicode based).
/// </summary>
internal static class Cp1252
{
    // Unicode code points for the 0x80-0x9F window (0xFFFF = undefined slot).
    private static readonly char[] High = new char[32]
    {
        '\u20AC', '\uFFFF', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021',
        '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\uFFFF', '\u017D', '\uFFFF',
        '\uFFFF', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014',
        '\u02DC', '\u2122', '\u0161', '\u203A', '\u0153', '\uFFFF', '\u017E', '\u0178',
    };

    /// <summary>Maps a CP1252 byte to its Unicode char.</summary>
    public static char ToChar(byte b)
        => b is >= (byte)0x80 and <= (byte)0x9F ? High[b - 0x80] : (char)b;

    /// <summary>
    /// Maps a Unicode char to its CP1252 byte. Returns <c>0x3F</c> ('?') for any
    /// char with no CP1252 representation (matching VFP's lossy down-conversion).
    /// </summary>
    public static byte ToByte(char c)
    {
        int u = c;
        if (u == 0xFFFF) return (byte)'?';            // undefined-slot sentinel
        if (u <= 0x7F) return (byte)u;                // ASCII
        if (u is >= 0xA0 and <= 0xFF) return (byte)u; // Latin-1 upper half
        for (int i = 0; i < High.Length; i++)
            if (High[i] == c) return (byte)(0x80 + i);
        return (byte)'?';
    }
}
