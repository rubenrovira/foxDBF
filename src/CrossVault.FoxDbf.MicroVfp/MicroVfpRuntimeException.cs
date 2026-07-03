using System;

namespace CrossVault.FoxDbf.MicroVfp;

/// <summary>
/// Thrown by <see cref="VfpInterpreter"/> when a stored procedure cannot be run (e.g. a call to a
/// procedure that is not loaded). Genuine VFP-runtime errors inside a procedure surface as the
/// underlying exception; this type covers the interpreter's own pre-conditions.
/// </summary>
/// <remarks>
/// When the throw site KNOWS the Visual FoxPro error number the fault maps to (project-review 5.3),
/// it passes it via the <see cref="VfpErrorNumber"/> constructor overload so the interpreter's error
/// trap (<c>ErrorNumberOf</c>) can carry the exact number to <c>AERROR()</c>/<c>ERROR()</c> WITHOUT
/// sniffing the English message text. A message-only throw leaves <see cref="VfpErrorNumber"/> null
/// (the trap then classifies it by its last-resort heuristic).
/// </remarks>
public sealed class MicroVfpRuntimeException : Exception, IVfpErrorCode
{
    /// <summary>The VFP9 error number this fault represents, or <see langword="null"/> when the throw
    /// site did not pin one (see <see cref="IVfpErrorCode"/>).</summary>
    public int? VfpErrorNumber { get; }

    public MicroVfpRuntimeException(string message) : base(message) { }

    public MicroVfpRuntimeException(string message, int vfpErrorNumber) : base(message)
        => VfpErrorNumber = vfpErrorNumber;
}
