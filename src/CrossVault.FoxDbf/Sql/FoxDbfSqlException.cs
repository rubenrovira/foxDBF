using System;

namespace CrossVault.FoxDbf.Sql;

/// <summary>
/// Thrown for malformed VFP-SQL command text (lex/parse errors). Carries the 0-based
/// character <see cref="Position"/> in the source where the problem was detected, when known.
/// Errors that originate while parsing an embedded scalar/predicate fragment via the
/// expression engine are wrapped (the inner exception is the original
/// <c>ExpressionException</c>) and the position is translated back into SQL-source coordinates.
/// </summary>
public sealed class FoxDbfSqlException : Exception
{
    /// <summary>0-based character position in the SQL source text, or -1 when unknown.</summary>
    public int Position { get; }

    public FoxDbfSqlException(string message, int position = -1) : base(message)
        => Position = position;

    public FoxDbfSqlException(string message, Exception inner, int position = -1)
        : base(message, inner) => Position = position;
}
