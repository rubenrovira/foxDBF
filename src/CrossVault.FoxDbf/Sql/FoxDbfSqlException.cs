using System;

namespace CrossVault.FoxDbf.Sql;

/// <summary>
/// Thrown for malformed VFP-SQL command text (lex/parse errors). Carries the 0-based
/// character <see cref="Position"/> in the source where the problem was detected, when known.
/// Errors that originate while parsing an embedded scalar/predicate fragment via the
/// expression engine are wrapped (the inner exception is the original
/// <c>ExpressionException</c>) and the position is translated back into SQL-source coordinates.
/// </summary>
public sealed class FoxDbfSqlException : Exception, IVfpErrorCode
{
    /// <summary>0-based character position in the SQL source text, or -1 when unknown.</summary>
    public int Position { get; }

    /// <summary>The VFP9 error number this fault represents, or <see langword="null"/> when the throw
    /// site did not pin one (project-review 5.3; see <see cref="IVfpErrorCode"/>). Set via an object
    /// initializer at the throw site, e.g. <c>new FoxDbfSqlException(msg) { VfpErrorNumber = 1 }</c>.</summary>
    public int? VfpErrorNumber { get; init; }

    public FoxDbfSqlException(string message, int position = -1) : base(message)
        => Position = position;

    public FoxDbfSqlException(string message, Exception inner, int position = -1)
        : base(message, inner) => Position = position;
}
