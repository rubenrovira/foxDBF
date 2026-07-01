using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace CrossVault.FoxDbf.Write;

/// <summary>
/// Writes a single field's .NET value into its on-disk byte layout — the exact inverse
/// of <see cref="FieldDecoder"/> (plan §A5/§A5b/§D0). One <c>switch</c> on the column's
/// type char mirrors the decoder: <c>C</c> space-pad/truncate to the table code page,
/// <c>N</c>/<c>F</c> right-justified ASCII with <see cref="DbfColumn.Decimal"/> places,
/// <c>I</c>/<c>+</c> 4-byte LE int32, <c>Y</c> <c>round(v*10000)</c> int64 LE, <c>B</c>
/// IEEE-754 double LE, <c>D</c> <c>YYYYMMDD</c>, <c>T</c>/<c>@</c> two int32 LE (Julian
/// day + ms since midnight), <c>L</c> <c>T</c>/<c>F</c>, <c>V</c>/<c>Q</c> value + trailing
/// length byte, binary/<c>G</c> raw bytes, <c>M</c> a 0 pointer (memo write deferred to D1).
/// </summary>
/// <remarks>
/// Never throws on an out-of-range value where the spec coerces (best-effort, §A10): a
/// numeric too wide for the field overflows to <c>'*'</c> fill, an impossible date clamps.
/// </remarks>
public static class FieldEncoder
{
    // The Julian Day Number of 0001-01-01 (proleptic Gregorian) — the offset between
    // FoxPro 'T' day numbers and .NET DayNumber (0 == 0001-01-01). Mirrors FieldDecoder.
    private const int JdnEpoch = 1_721_426;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Encode <paramref name="value"/> for <paramref name="column"/> into the first
    /// <see cref="DbfColumn.Length"/> bytes of <paramref name="dest"/>, using
    /// <paramref name="encoding"/> for character data. Returns <see langword="true"/>
    /// when a Varchar/Varbinary value shorter than the field width was written (so the
    /// caller must set the column's varlen bit in <c>_NullFlags</c>); otherwise
    /// <see langword="false"/>.
    /// </summary>
    public static bool Encode(DbfColumn column, object? value, Encoding encoding, Span<byte> dest)
    {
        // DBNull is treated identically to null (§A5b NULL → type-appropriate empty).
        if (value is DBNull)
            value = null;

        char t = column.Type;

        if (value is null)
            return EncodeNull(t, dest);

        switch (t)
        {
            case 'C':
                WriteCharacter(CoerceString(value), encoding, dest);
                return false;

            case 'N':
            case 'F':
                WriteAsciiRightJustified(FormatNumber(value, column.Decimal), dest);
                return false;

            case 'I':
            case '+':
                WriteInt32(dest, ToInt32(value));
                return false;

            case 'Y':
                WriteInt64(dest, ToCurrencyScaled(value));
                return false;

            case 'B':
                WriteDouble(dest, ToDouble(value));
                return false;

            case 'D':
                WriteDate(dest, ToDateOnly(value));
                return false;

            case 'T':
            case '@':
                WriteDateTime(dest, ToDateTime(value));
                return false;

            case 'L':
                if (dest.Length > 0)
                    dest[0] = ToBoolean(value) ? (byte)'T' : (byte)'F';
                return false;

            case 'M':
            case 'W':
                // 'M' (Memo) and 'W' (VFP9 Blob) are both FPT-backed 4-byte block pointers
                // decoded identically (FieldDecoder groups M/V/Q/W/G; DbfRecord dereferences
                // M/W as an FPT pointer). Memo/blob append is deferred to D1: D0 always writes
                // a 0 pointer. Writing the blob's raw bytes here would forge a garbage pointer.
                dest.Clear();
                return false;

            case 'V':
            case 'Q':
                return WriteVarlen(t, value, encoding, dest);

            // 'G' (General) and any other binary/unmapped type → raw bytes.
            default:
                WriteRawBytes(value, dest);
                return false;
        }
    }

    /// <summary>Write the §A5b type-appropriate empty for a null value.</summary>
    private static bool EncodeNull(char t, Span<byte> dest)
    {
        switch (t)
        {
            // Text/date/logical fields decode "all spaces" back to null.
            case 'C':
            case 'N':
            case 'F':
            case 'D':
            case 'L':
                dest.Fill((byte)' ');
                break;

            // Numeric/binary fields (I/Y/B/T/M/V/Q/G/binary) decode all-zero back to null.
            default:
                dest.Clear();
                break;
        }
        return false;
    }

    // ---- character -------------------------------------------------------------

    private static void WriteCharacter(string s, Encoding encoding, Span<byte> dest)
    {
        var bytes = encoding.GetBytes(s);
        int n = Math.Min(bytes.Length, dest.Length);
        bytes.AsSpan(0, n).CopyTo(dest);
        // Space-pad the remainder (the inverse of the decoder's trailing-space trim).
        dest[n..].Fill((byte)' ');
    }

    // ---- numeric (N / F): right-justified ASCII, Decimal places ----------------

    private static void WriteAsciiRightJustified(string s, Span<byte> dest)
    {
        int w = dest.Length;
        if (s.Length > w)
        {
            // Overflow (§A10 best-effort): VFP fills an over-wide numeric with '*'.
            dest.Fill((byte)'*');
            return;
        }
        int pad = w - s.Length;
        for (int i = 0; i < pad; i++)
            dest[i] = (byte)' ';
        // s is pure ASCII (digits, '.', '-'); a 1:1 byte cast is exact.
        for (int i = 0; i < s.Length; i++)
            dest[pad + i] = (byte)s[i];
    }

    private static string FormatNumber(object value, int decimals)
    {
        if (decimals <= 0)
        {
            // Integer form, no decimal point.
            return value switch
            {
                long l => l.ToString(Inv),
                int i => i.ToString(Inv),
                short sh => ((long)sh).ToString(Inv),
                byte b => ((long)b).ToString(Inv),
                decimal m => decimal.Round(m, 0, MidpointRounding.AwayFromZero).ToString("F0", Inv),
                double d => Math.Round(d, MidpointRounding.AwayFromZero).ToString("F0", Inv),
                float f => Math.Round((double)f, MidpointRounding.AwayFromZero).ToString("F0", Inv),
                _ => SafeDecimal(value).ToString("F0", Inv),
            };
        }

        string fmt = "F" + decimals.ToString(Inv);
        // Format from the value's native type to avoid lossy double→decimal conversion.
        return value switch
        {
            decimal m => m.ToString(fmt, Inv),
            double d => d.ToString(fmt, Inv),
            float f => ((double)f).ToString(fmt, Inv),
            long l => l.ToString(fmt, Inv),
            int i => i.ToString(fmt, Inv),
            _ => SafeDecimal(value).ToString(fmt, Inv),
        };
    }

    // ---- integer (I / +) -------------------------------------------------------

    private static void WriteInt32(Span<byte> dest, int v)
    {
        if (dest.Length >= 4)
            BinaryPrimitives.WriteInt32LittleEndian(dest, v);
    }

    // ---- currency (Y) ----------------------------------------------------------

    private static void WriteInt64(Span<byte> dest, long v)
    {
        if (dest.Length >= 8)
            BinaryPrimitives.WriteInt64LittleEndian(dest, v);
    }

    private static long ToCurrencyScaled(object value)
    {
        decimal m = SafeDecimal(value);
        // The *10000 scale can overflow decimal itself for a very wide currency value, and
        // the final narrowing to int64 throws on out-of-range. Saturate at every step so the
        // never-throw §A10 contract holds for 'Y' just like N/F's '*' fill (MUST-FIX).
        decimal scaled;
        try
        {
            scaled = decimal.Round(m * 10000m, 0, MidpointRounding.AwayFromZero);
        }
        catch (OverflowException)
        {
            return m >= 0m ? long.MaxValue : long.MinValue;
        }
        return SaturateToInt64(scaled);
    }

    // ---- double (B) ------------------------------------------------------------

    private static void WriteDouble(Span<byte> dest, double v)
    {
        if (dest.Length >= 8)
            BinaryPrimitives.WriteDoubleLittleEndian(dest, v);
    }

    // ---- date (D) --------------------------------------------------------------

    private static void WriteDate(Span<byte> dest, DateOnly d)
    {
        // YYYYMMDD, zero-padded; the inverse of FieldDecoder.DecodeDate.
        string s = $"{d.Year:0000}{d.Month:00}{d.Day:00}";
        int n = Math.Min(s.Length, dest.Length);
        for (int i = 0; i < n; i++)
            dest[i] = (byte)s[i];
        dest[n..].Fill((byte)' ');
    }

    // ---- datetime (T / @) ------------------------------------------------------

    private static void WriteDateTime(Span<byte> dest, DateTime dt)
    {
        if (dest.Length < 8)
            return;
        int days = DateOnly.FromDateTime(dt).DayNumber + JdnEpoch;
        int ms = (int)Math.Round(dt.TimeOfDay.TotalMilliseconds, MidpointRounding.AwayFromZero);
        BinaryPrimitives.WriteInt32LittleEndian(dest, days);
        BinaryPrimitives.WriteInt32LittleEndian(dest[4..], ms);
    }

    // ---- varchar / varbinary (V / Q) -------------------------------------------

    private static bool WriteVarlen(char t, object value, Encoding encoding, Span<byte> dest)
    {
        int w = dest.Length;
        if (w == 0)
            return false;

        byte[] bytes = t == 'V'
            ? encoding.GetBytes(CoerceString(value))
            : (value as byte[] ?? Array.Empty<byte>());

        if (bytes.Length >= w)
        {
            // Exactly (or over-) fills the field: no trailing length byte, no varlen bit.
            bytes.AsSpan(0, w).CopyTo(dest);
            return false;
        }

        bytes.CopyTo(dest);
        // Pad the gap (V → spaces to match the fixture layout, Q → zero bytes), then write
        // the effective length into the LAST byte and signal the caller to set the varlen bit.
        byte pad = t == 'V' ? (byte)' ' : (byte)0x00;
        dest[bytes.Length..(w - 1)].Fill(pad);
        dest[w - 1] = (byte)bytes.Length;
        return true;
    }

    // ---- raw bytes (G / binary) ------------------------------------------------

    private static void WriteRawBytes(object value, Span<byte> dest)
    {
        if (value is byte[] b)
        {
            int n = Math.Min(b.Length, dest.Length);
            b.AsSpan(0, n).CopyTo(dest);
            dest[n..].Clear();
        }
        else
        {
            dest.Clear();
        }
    }

    // ---- value coercions (never throw, §A10) -----------------------------------

    private static string CoerceString(object value)
        => value as string ?? Convert.ToString(value, Inv) ?? "";

    private static long ToInt64(object value) => value switch
    {
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        sbyte sb => sb,
        uint u => u,
        ushort us => us,
        ulong ul => unchecked((long)ul),
        bool bo => bo ? 1 : 0,
        // Narrowing to int64 throws on out-of-range (decimal->long always; double->long is
        // "unspecified"); saturate instead so 'I'/'+'/'Y' never throw (never-throw §A10, MUST-FIX).
        decimal m => SaturateToInt64(decimal.Round(m, 0, MidpointRounding.AwayFromZero)),
        double d => SaturateToInt64(Math.Round(d, MidpointRounding.AwayFromZero)),
        float f => SaturateToInt64(Math.Round((double)f, MidpointRounding.AwayFromZero)),
        string s => long.TryParse(s, NumberStyles.Number, Inv, out var r) ? r : 0L,
        _ => 0L,
    };

    /// <summary>Clamp a rounded <see cref="decimal"/> to the int64 range (no overflow throw).</summary>
    private static long SaturateToInt64(decimal rounded)
    {
        if (rounded >= long.MaxValue) return long.MaxValue;
        if (rounded <= long.MinValue) return long.MinValue;
        return (long)rounded;
    }

    /// <summary>Clamp a rounded <see cref="double"/> to the int64 range (NaN→0, no overflow throw).</summary>
    private static long SaturateToInt64(double rounded)
    {
        if (double.IsNaN(rounded)) return 0L;
        // (double)long.MaxValue rounds up to 2^63, so '>=' here saturates anything out of range.
        if (rounded >= 9223372036854775808.0) return long.MaxValue;
        if (rounded <= -9223372036854775808.0) return long.MinValue;
        return (long)rounded;
    }

    private static int ToInt32(object value) => unchecked((int)ToInt64(value));

    private static decimal SafeDecimal(object value) => value switch
    {
        decimal m => m,
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        double d => SafeFromDouble(d),
        float f => SafeFromDouble(f),
        string s => decimal.TryParse(s, NumberStyles.Number, Inv, out var r) ? r : 0m,
        _ => 0m,
    };

    private static decimal SafeFromDouble(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d))
            return 0m;
        try { return (decimal)d; }
        catch (OverflowException) { return 0m; }
    }

    private static double ToDouble(object value) => value switch
    {
        double d => d,
        float f => f,
        decimal m => (double)m,
        long l => l,
        int i => i,
        short s => s,
        byte b => b,
        string s => double.TryParse(s, NumberStyles.Float, Inv, out var r) ? r : 0d,
        _ => 0d,
    };

    private static bool ToBoolean(object value) => value switch
    {
        bool b => b,
        string s => s.Length > 0 && (s[0] is 'T' or 't' or 'Y' or 'y' or '1'),
        _ => ToInt64(value) != 0,
    };

    private static DateOnly ToDateOnly(object value) => value switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        _ => default,
    };

    private static DateTime ToDateTime(object value) => value switch
    {
        DateTime dt => dt,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        _ => default,
    };
}
