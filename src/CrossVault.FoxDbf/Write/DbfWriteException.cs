namespace CrossVault.FoxDbf.Write;

/// <summary>
/// Thrown by the record-write layer (plan §D1) when a write cannot proceed safely:
/// principally the 2 GB file-size guard (a DBF addresses records with 32-bit offsets,
/// so <c>HeaderLength + RecordCount * RecordLength</c> must stay below
/// <see cref="int.MaxValue"/>), the §D2 PACK/ZAP exclusive-access requirement, but also any
/// other refusal-to-corrupt condition.
/// </summary>
/// <remarks>
/// Distinct from the read-side <see cref="DbfCorruptHeaderException"/> so callers can
/// catch a write refusal specifically and leave the file untouched (§D-Leitplanken:
/// "auf einen Mid-Write-Fehler keinen korrupten Count hinterlassen").
/// <para>
/// When the throw site KNOWS the Visual FoxPro error number the refusal maps to (project-review 5.3),
/// it passes it through the <see cref="VfpErrorNumber"/> overload so the microVFP interpreter's error
/// trap (<c>ErrorNumberOf</c>) can carry the exact number to <c>AERROR()</c>/<c>ERROR()</c> WITHOUT
/// sniffing the English message text — e.g. PACK/ZAP on a SHARED open surfaces VFP error 110
/// ("File must be opened exclusively.", oracle-verified against vfp9.exe).
/// </para>
/// </remarks>
public sealed class DbfWriteException : Exception, IVfpErrorCode
{
    /// <summary>The VFP9 error number this write refusal represents, or <see langword="null"/> when the
    /// throw site did not pin one (see <see cref="IVfpErrorCode"/>).</summary>
    public int? VfpErrorNumber { get; }

    public DbfWriteException() { }
    public DbfWriteException(string message) : base(message) { }
    public DbfWriteException(string message, Exception inner) : base(message, inner) { }

    /// <summary>Create a write refusal that carries the authoritative VFP9 <paramref name="vfpErrorNumber"/>
    /// (the <see cref="IVfpErrorCode"/> typed path — no message-text sniffing downstream).</summary>
    public DbfWriteException(string message, int vfpErrorNumber) : base(message)
        => VfpErrorNumber = vfpErrorNumber;
}
