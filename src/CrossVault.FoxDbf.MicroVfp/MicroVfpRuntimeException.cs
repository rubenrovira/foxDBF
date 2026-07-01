using System;

namespace CrossVault.FoxDbf.MicroVfp;

/// <summary>
/// Thrown by <see cref="VfpInterpreter"/> when a stored procedure cannot be run (e.g. a call to a
/// procedure that is not loaded). Genuine VFP-runtime errors inside a procedure surface as the
/// underlying exception; this type covers the interpreter's own pre-conditions.
/// </summary>
public sealed class MicroVfpRuntimeException : Exception
{
    public MicroVfpRuntimeException(string message) : base(message) { }
}
