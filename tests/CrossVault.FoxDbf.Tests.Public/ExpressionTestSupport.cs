using System.Collections.Generic;
using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.Tests;

/// <summary>
/// In-memory <see cref="IRowContext"/> for the expression-engine tests. Field
/// names are case-insensitive (VFP is case-insensitive for identifiers).
/// </summary>
internal sealed class TestRow : IRowContext
{
    private readonly Dictionary<string, object?> _fields;

    public TestRow(int recNo = 1, bool deleted = false)
    {
        _fields = new Dictionary<string, object?>(System.StringComparer.OrdinalIgnoreCase);
        RecNo = recNo;
        Deleted = deleted;
    }

    public int RecNo { get; set; }
    public bool Deleted { get; set; }
    public int RecCount { get; set; }

    public TestRow Set(string name, object? value)
    {
        _fields[name] = value;
        return this;
    }

    public object? GetField(string name)
        => _fields.TryGetValue(name, out var v) ? v : null;

    public static readonly TestRow Empty = new();
}

/// <summary>In-memory <see cref="ISchema"/> for type-inference tests.</summary>
internal sealed class TestSchema : ISchema
{
    private readonly Dictionary<string, (char Type, int Length, int Decimals)> _cols
        = new(System.StringComparer.OrdinalIgnoreCase);

    public TestSchema Add(string name, char type, int length, int decimals = 0)
    {
        _cols[name] = (type, length, decimals);
        return this;
    }

    public bool TryGetColumn(string name, out char type, out int length, out int decimals)
    {
        if (_cols.TryGetValue(name, out var c))
        {
            type = c.Type; length = c.Length; decimals = c.Decimals;
            return true;
        }
        type = '\0'; length = 0; decimals = 0;
        return false;
    }
}

/// <summary>Shared evaluation helpers for the expression tests.</summary>
internal static class Ev
{
    public static VfpValue Eval(string expr, IRowContext? row = null, EvaluationContext? ctx = null)
        => VfpExpression.Parse(expr).Evaluate(row ?? TestRow.Empty, ctx ?? EvaluationContext.Default);

    /// <summary>EXACT-ON context (SET EXACT ON).</summary>
    public static EvaluationContext ExactOn => new() { Exact = true };

    /// <summary>EXACT-OFF context (VFP default).</summary>
    public static EvaluationContext ExactOff => new() { Exact = false };
}
