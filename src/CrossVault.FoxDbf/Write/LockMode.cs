namespace CrossVault.FoxDbf.Write;

/// <summary>
/// Controls how a <see cref="DbfWriter"/> opens the underlying <c>.dbf</c> handle and
/// whether it participates in Visual FoxPro's byte-range locking protocol (plan §D3).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Shared"/> is the default: the file is opened
/// <see cref="System.IO.FileShare.ReadWrite"/> and every mutating operation brackets its
/// write with the exact CodeBase/VFP byte-range lock (record / append / file), so a .NET
/// writer can coexist with a running VFP application on the same table. This is the safe
/// choice for multi-process access.
/// </para>
/// <para>
/// <see cref="Exclusive"/> opens the file <see cref="System.IO.FileShare.None"/> — no other
/// process may open it at all — and therefore SKIPS the byte-range locks entirely (they are
/// redundant when the OS already grants exclusive access). Use this for bulk import / PACK /
/// ZAP where no concurrent VFP access is expected; it avoids the per-operation lock overhead.
/// </para>
/// </remarks>
public enum LockMode
{
    /// <summary>
    /// Open <see cref="System.IO.FileShare.ReadWrite"/> and acquire/release the VFP byte-range
    /// locks around each mutation (record / append / file). The default — enables coexistence
    /// with a running VFP runtime.
    /// </summary>
    Shared = 0,

    /// <summary>
    /// Open <see cref="System.IO.FileShare.None"/> (deny all other handles) and skip byte-range
    /// locks. Lowest overhead, single-process only.
    /// </summary>
    Exclusive = 1,
}
