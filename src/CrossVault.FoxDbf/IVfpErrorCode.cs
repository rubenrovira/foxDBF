namespace CrossVault.FoxDbf;

/// <summary>
/// Carries the authoritative Visual FoxPro error number an exception REPRESENTS, set at the throw
/// site where the cause is known (project-review 5.3). The microVFP interpreter's error-number
/// mapping reads this FIRST — a typed number is authoritative — and only falls back to English
/// message-text sniffing for FOREIGN (BCL / third-party) exceptions that cannot carry it.
/// <para>
/// Deliberately tiny and <see langword="internal"/>: it lives in the Core assembly (which every other
/// assembly references), so exception types across MicroVfp / Sql / Data can all advertise their VFP
/// number through ONE shared surface without any assembly having to reference the others' types. A
/// <see langword="null"/> value means "this throw site did not pin a number" — the reader then applies
/// its known-type / text-fallback rules.
/// </para>
/// </summary>
internal interface IVfpErrorCode
{
    /// <summary>The VFP9 error number this exception stands for, or <see langword="null"/> when the
    /// throw site did not pin one.</summary>
    int? VfpErrorNumber { get; }
}
