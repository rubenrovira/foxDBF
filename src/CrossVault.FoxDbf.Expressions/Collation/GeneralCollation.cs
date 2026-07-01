using System;
using System.Buffers;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>
/// VFP <c>GENERAL</c> collation (the Western / CP1252 sort sequence). The two-pass key
/// is built from the <see cref="GeneralWeights1252"/> table ported from CodeBase
/// <c>COLL4ARR.C</c>:
/// </summary>
/// <remarks>
/// <list type="number">
///   <item>a <b>head</b> (primary) byte per character — the case- AND accent-folded
///   sort weight, with ligatures expanded to two letters (<c>Œ→OE</c>, <c>Æ→AE</c>,
///   <c>Þ→TH</c>, <c>ß→SS</c>); then</item>
///   <item>a <b>tail</b> region: the diacritic weight of every character that has one.</item>
/// </list>
/// <para>
/// VFP trims the trailing 0x00 tail bytes and the host space-pads (0x20) the key field
/// to the tag key length, so <see cref="GetCollatedKey"/> returns the NATURAL key (heads
/// then non-trailing-zero tails). The stored GENERAL CDX key IS these weight bytes.
/// Examples: <c>"ADMIN"</c> → <c>60 64 6F 6A 70</c>; <c>"JMÖLCK"</c> →
/// <c>6B 6F 72 6D 62 6C 04</c>; <c>"ARÖTTGER"</c> → <c>60 75 72 77 77 68 66 75 00 04</c>.
/// </para>
/// </remarks>
public sealed class GeneralCollation : IVfpCollation
{
    /// <summary>The shared singleton (the weight table is immutable).</summary>
    public static readonly GeneralCollation Instance = new();

    private GeneralCollation() { }

    /// <inheritdoc/>
    public string Name => "GENERAL";

    /// <summary>
    /// Builds the natural GENERAL key for <paramref name="s"/>: the head pass then the
    /// diacritic-tail pass, with trailing 0x00 tail bytes trimmed and trailing source
    /// spaces ignored (VFP keys on the trimmed value). The host pads the result with
    /// spaces (0x20) to the stored tag key length for the byte-exact CDX key.
    /// </summary>
    public byte[] GetCollatedKey(ReadOnlySpan<char> s)
    {
        // Worst case: every char expands to 2 heads + 2 tails.
        int cap = s.Length * 2;
        Span<byte> heads = cap <= StackCap ? stackalloc byte[StackCap] : new byte[cap];
        Span<byte> tails = cap <= StackCap ? stackalloc byte[StackCap] : new byte[cap];

        BuildKey(s, heads, tails, out int hc, out int tc);

        var key = new byte[hc + tc];
        heads[..hc].CopyTo(key);
        tails[..tc].CopyTo(key.AsSpan(hc));
        return key;
    }

    /// <inheritdoc/>
    public int Compare(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        // Zero-allocation: build both keys into stack (or pooled) head/tail buffers and
        // compare the logical heads-then-tails sequences byte-by-byte. No byte[] per call,
        // which matters on the per-record hot path ($/=/<=/>= for Character operands).
        int capL = left.Length * 2;
        int capR = right.Length * 2;

        byte[]? rhL = null, rtL = null, rhR = null, rtR = null;
        Span<byte> hL = capL <= StackCap ? stackalloc byte[StackCap] : (rhL = ArrayPool<byte>.Shared.Rent(capL));
        Span<byte> tL = capL <= StackCap ? stackalloc byte[StackCap] : (rtL = ArrayPool<byte>.Shared.Rent(capL));
        Span<byte> hR = capR <= StackCap ? stackalloc byte[StackCap] : (rhR = ArrayPool<byte>.Shared.Rent(capR));
        Span<byte> tR = capR <= StackCap ? stackalloc byte[StackCap] : (rtR = ArrayPool<byte>.Shared.Rent(capR));
        try
        {
            BuildKey(left, hL, tL, out int hcL, out int tcL);
            BuildKey(right, hR, tR, out int hcR, out int tcR);
            return CompareKey(hL[..hcL], tL[..tcL], hR[..hcR], tR[..tcR]);
        }
        finally
        {
            if (rhL is not null) ArrayPool<byte>.Shared.Return(rhL);
            if (rtL is not null) ArrayPool<byte>.Shared.Return(rtL);
            if (rhR is not null) ArrayPool<byte>.Shared.Return(rhR);
            if (rtR is not null) ArrayPool<byte>.Shared.Return(rtR);
        }
    }

    /// <summary>Stack-buffer threshold (bytes). Inputs whose worst-case key (len*2) fits are
    /// keyed with no heap traffic at all; longer ones fall back to an <see cref="ArrayPool{T}"/>
    /// rent (Compare) or a plain array (GetCollatedKey, which must allocate the result anyway).</summary>
    private const int StackCap = 256;

    /// <summary>
    /// Shared two-pass key builder: writes the head (primary) weights into
    /// <paramref name="heads"/> and the diacritic (tail) weights into <paramref name="tails"/>,
    /// expanding ligatures (Œ→OE, Æ→AE, Þ→TH, ß→SS), trimming trailing source spaces and
    /// trailing 0x00 tail bytes. Buffers must each hold at least <c>s.Length * 2</c> bytes.
    /// The logical key is <c>heads[..hc]</c> followed by <c>tails[..tc]</c>.
    /// </summary>
    private static void BuildKey(ReadOnlySpan<char> s, Span<byte> heads, Span<byte> tails, out int hc, out int tc)
    {
        int len = s.Length;
        while (len > 0 && s[len - 1] == ' ') len--;

        var head = GeneralWeights1252.Head;
        var tail = GeneralWeights1252.Tail;
        hc = 0; tc = 0;

        for (int i = 0; i < len; i++)
        {
            byte idx = Cp1252.ToByte(s[i]);
            byte h = head[idx];
            if (h != GeneralWeights1252.Expand)
            {
                heads[hc++] = h;
                byte t = tail[idx];
                if (t != GeneralWeights1252.NoTail) tails[tc++] = t;
            }
            else
            {
                byte[] pair = GeneralWeights1252.Expansions[tail[idx]];
                for (int j = 0; j < pair.Length; j++)
                {
                    byte cc = pair[j];
                    heads[hc++] = head[cc];
                    byte t = tail[cc];
                    if (t != GeneralWeights1252.NoTail) tails[tc++] = t;
                }
            }
        }

        while (tc > 0 && tails[tc - 1] == 0) tc--;
    }

    /// <summary>Unsigned byte-order compare of two logical keys, each given as a head run
    /// followed by a tail run, without materialising either concatenation.</summary>
    private static int CompareKey(ReadOnlySpan<byte> hA, ReadOnlySpan<byte> tA,
                                  ReadOnlySpan<byte> hB, ReadOnlySpan<byte> tB)
    {
        int lenA = hA.Length + tA.Length;
        int lenB = hB.Length + tB.Length;
        int n = Math.Min(lenA, lenB);
        for (int i = 0; i < n; i++)
        {
            byte x = i < hA.Length ? hA[i] : tA[i - hA.Length];
            byte y = i < hB.Length ? hB[i] : tB[i - hB.Length];
            int d = x - y;
            if (d != 0) return d < 0 ? -1 : 1;
        }
        return lenA.CompareTo(lenB);
    }
}
