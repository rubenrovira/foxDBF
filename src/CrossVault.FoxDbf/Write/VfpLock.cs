namespace CrossVault.FoxDbf.Write;

/// <summary>
/// Computes the exact Visual FoxPro / CodeBase byte-range lock positions (plan §D3) so a
/// .NET writer's <see cref="System.IO.FileStream.Lock(long,long)"/> calls land on the SAME
/// bytes the VFP runtime locks — allowing the two to coexist on one table.
/// </summary>
/// <remarks>
/// <para>
/// Offsets are taken verbatim from CodeBase <c>df4lock.c</c> (the S4FOX path) and
/// <c>d4defs.h</c>: <c>L4LOCK_POS == 0x7FFFFFFE</c>, <c>L4LOCK_POS_OLD == 0x40000000</c>.
/// The record-lock formula is conditional on the table's index/version: when the table has a
/// structural <c>.cdx</c> (header byte 28 bit 0 set) OR its version byte is <c>0x30</c>, the
/// "structural" anchored scheme is used; otherwise the "classic" record-position scheme.
/// </para>
/// </remarks>
public static class VfpLock
{
    /// <summary>CodeBase <c>L4LOCK_POS</c> — the structural lock anchor (0x7FFFFFFE).</summary>
    private const long LockPos = 0x7FFFFFFEL;

    /// <summary>CodeBase <c>L4LOCK_POS_OLD</c> — the classic lock base (0x40000000).</summary>
    private const long LockPosOld = 0x40000000L;

    /// <summary>
    /// The whole-file lock length (0x3FFFFFFE): the classic base up to — but EXCLUDING — the
    /// structural append anchor 0x7FFFFFFE (last locked byte 0x7FFFFFFD).
    /// </summary>
    private const long FileLength = 0x3FFFFFFEL;

    /// <summary>
    /// True when the structural lock scheme applies: the table carries a structural <c>.cdx</c>
    /// (<paramref name="hasStructuralCdx"/>, header byte 28 bit 0) OR its version byte is
    /// <c>0x30</c>.
    /// </summary>
    public static bool UsesStructuralScheme(byte versionCode, bool hasStructuralCdx)
        => hasStructuralCdx || versionCode == 0x30;

    /// <summary>
    /// The single-byte position to lock for the 1-based record <paramref name="recNo"/>.
    /// Structural scheme: <c>0x7FFFFFFE - recNo</c>. Classic scheme:
    /// <c>0x40000000 + (headerLength + (recNo-1) * recordLength)</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="recNo"/> is not 1-based (&lt; 1).</exception>
    public static long RecordLockPosition(int recNo, bool usesStructuralScheme, int headerLength, int recordLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(recNo, 1);
        return usesStructuralScheme
            ? LockPos - recNo
            : LockPosOld + headerLength + (long)(recNo - 1) * recordLength;
    }

    /// <summary>
    /// The single-byte position to lock while appending: <c>0x7FFFFFFE</c> (structural) or
    /// <c>0x40000000</c> (classic).
    /// </summary>
    public static long AppendLockPosition(bool usesStructuralScheme)
        => usesStructuralScheme ? LockPos : LockPosOld;

    /// <summary>
    /// The whole-file (FLOCK) lock range: start <c>0x40000000</c>, length <c>0x3FFFFFFE</c>,
    /// ending one byte before the structural append anchor (<c>0x40000000 + 0x3FFFFFFE ==
    /// 0x7FFFFFFE</c>, so the last locked byte is <c>0x7FFFFFFD</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The range deliberately <b>EXCLUDES</b> the append anchor <c>0x7FFFFFFE</c>. Empirically
    /// (VFP9, measured via the concurrency battle / lock probe) a VFP <c>USE … SHARED</c> takes a
    /// momentary <b>EXCLUSIVE</b> lock on the append anchor while opening a structural (v0x30)
    /// table; ANY lock we hold on that byte — shared OR exclusive — blocks the open, so VFP's
    /// <c>USE</c> hangs/throws. This is the §D3 bug. By stopping one byte short of the anchor we let
    /// VFP open and read the table (hackfox s4g203: FLOCK must NOT prevent others from
    /// viewing/browsing), while still:
    /// </para>
    /// <list type="bullet">
    ///   <item>denying a VFP <c>FLOCK()</c> — VFP's FLOCK locks the record range
    ///   <c>[~0x7C000000 .. 0x7FFFFFFD]</c>, which overlaps ours, so its EXCLUSIVE lock is refused;</item>
    ///   <item>denying any record lock (structural <c>0x7FFFFFFE - recNo</c>, all ≤ <c>0x7FFFFFFD</c>),
    ///   which sits inside the range;</item>
    ///   <item>staying mutually exclusive with another FoxDbf writer's whole-file lock (both take an
    ///   EXCLUSIVE lock over the identical range).</item>
    /// </list>
    /// <para>
    /// A momentary append lock on the anchor itself is intentionally NOT covered: just like VFP, a
    /// whole-file lock and an open/append both need byte <c>0x7FFFFFFE</c>, so the two cannot be
    /// distinguished at the OS level — VFP itself excludes the anchor from FLOCK for exactly this
    /// reason.
    /// </para>
    /// </remarks>
    public static (long Start, long Length) FileLockRange()
        => (LockPosOld, FileLength);
}
