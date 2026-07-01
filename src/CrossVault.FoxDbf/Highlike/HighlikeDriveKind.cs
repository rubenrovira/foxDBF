namespace CrossVault.FoxDbf.Highlike;

/// <summary>
/// How the Highlike warm index cache treats the storage that backs a table — which decides whether a
/// proactive <see cref="System.IO.FileSystemWatcher"/> is attached for eviction (design §5, Peak 2).
/// </summary>
/// <remarks>
/// INVALIDATION strategy per drive kind:
/// <list type="bullet">
///   <item><see cref="Fixed"/> — a local fixed disk: attach an FSW for PROACTIVE eviction AND keep the
///   cheap change-token poll at query entry (belt-and-braces). The FSW is disposed with the engine.</item>
///   <item><see cref="Network"/> — a UNC / network share: an FSW is UNRELIABLE over SMB, so attach NONE
///   and rely solely on the change-token poll (reccount + dbf last-update stamp + file length + last-write
///   time) validated at every query entry. Documented residual risk (design decision (a)): a foreign
///   SAME-SECOND in-place update that changes neither reccount nor the 2-second-granularity stamp cannot
///   be detected on the DBF format — our OWN writes always change reccount/stamp, so they are detected.</item>
///   <item><see cref="Auto"/> — auto-detect Fixed vs Network from the table path and behave as above.</item>
/// </list>
/// </remarks>
public enum HighlikeDriveKind
{
    /// <summary>Detect Fixed vs Network from the table path (the default).</summary>
    Auto = 0,

    /// <summary>Force LOCAL/fixed behaviour: attach a FileSystemWatcher and poll the token.</summary>
    Fixed = 1,

    /// <summary>Force NETWORK/Shared behaviour: no FileSystemWatcher; rely on the change-token poll.</summary>
    Network = 2,
}
