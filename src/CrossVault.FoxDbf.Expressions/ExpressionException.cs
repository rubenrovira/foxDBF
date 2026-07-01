using System;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>
/// Thrown for malformed expression text (lex/parse errors). Evaluation of a
/// well-formed expression never throws on bad/null DATA — it coerces or yields
/// <c>.NULL.</c> per VFP semantics.
/// </summary>
public sealed class ExpressionException : Exception
{
    /// <summary>0-based character position in the source text, when known.</summary>
    public int Position { get; }

    public ExpressionException(string message, int position = -1) : base(message)
        => Position = position;

    public ExpressionException(string message, Exception inner, int position = -1)
        : base(message, inner) => Position = position;
}
