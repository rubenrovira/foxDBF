using System;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>
/// VFP <c>MACHINE</c> collation: raw, code-page (CP1252) UNSIGNED byte order — the
/// engine default and the order VFP stores in a MACHINE-collated CDX. Case- and
/// accent-sensitive. <see cref="GetCollatedKey"/> is the identity (the CP1252 bytes
/// of the string); the host space-pads to the tag key length.
/// </summary>
/// <remarks>
/// Comparison is over CP1252 BYTES, not UTF-16 char order: the two agree on ASCII and
/// the Latin-1 upper half, but diverge for the 0x80-0x9F "smart punctuation" window
/// (e.g. byte 0x96 EN-DASH maps to U+2013, which sorts AFTER U+00FF by char order yet
/// BEFORE 0xFF by byte order). VFP indexes by byte, so this collation does too.
/// </remarks>
public sealed class MachineCollation : IVfpCollation
{
    /// <summary>The shared singleton (stateless).</summary>
    public static readonly MachineCollation Instance = new();

    private MachineCollation() { }

    /// <inheritdoc/>
    public string Name => "MACHINE";

    /// <inheritdoc/>
    public int Compare(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        int n = Math.Min(left.Length, right.Length);
        for (int i = 0; i < n; i++)
        {
            int d = Cp1252.ToByte(left[i]) - Cp1252.ToByte(right[i]);
            if (d != 0) return d < 0 ? -1 : 1;
        }
        return left.Length.CompareTo(right.Length);
    }

    /// <summary>The CP1252 byte encoding of <paramref name="s"/> (identity key). The host
    /// space-pads (0x20) to the tag key length to reproduce the stored CDX key.</summary>
    public byte[] GetCollatedKey(ReadOnlySpan<char> s)
    {
        var key = new byte[s.Length];
        for (int i = 0; i < s.Length; i++) key[i] = Cp1252.ToByte(s[i]);
        return key;
    }
}
