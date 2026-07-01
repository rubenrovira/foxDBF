using System;

namespace CrossVault.FoxDbf.MicroVfp;

/// <summary>
/// Thrown by <see cref="PrgParser"/> on truly-malformed PRG input (unbalanced block,
/// stray block terminator, missing <c>ENDIF/ENDDO/...</c>). Carries the 1-based source
/// <see cref="Line"/> so callers can point at the offending statement. Well-formed but
/// unrecognised commands do NOT throw — they parse to <see cref="UnknownCommand"/>.
/// </summary>
public sealed class MicroVfpSyntaxException : Exception
{
    /// <summary>1-based line number in the original source, or 0 when unknown.</summary>
    public int Line { get; }

    public MicroVfpSyntaxException(string message, int line = 0)
        : base(line > 0 ? $"Line {line}: {message}" : message)
        => Line = line;
}
