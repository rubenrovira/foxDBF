using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>Compile-time helpers shared while lowering the AST to a delegate.</summary>
internal sealed class BuildContext
{
    public required ParameterExpression Row { get; init; }
    public required Expression Ctx { get; init; }   // ConstantExpression of EvaluationContext
}

/// <summary>Immutable AST node base. Provides interpret, compile and infer paths.</summary>
internal abstract class AstNode
{
    /// <summary>Tree-walking interpretation.</summary>
    public abstract VfpValue Eval(IRowContext row, EvaluationContext ctx);

    /// <summary>Lowers this node to a <see cref="Expression"/> of type <see cref="VfpValue"/>.</summary>
    public abstract Expression Build(BuildContext b);

    /// <summary>Static type inference against a schema.</summary>
    public abstract VfpTypeInfo Infer(ISchema schema);

    /// <summary>If this node is a numeric literal, returns its integer value.</summary>
    public virtual bool TryConstInt(out int value) { value = 0; return false; }
}

internal sealed class LiteralNode : AstNode
{
    private readonly VfpValue _value;
    public LiteralNode(VfpValue value) => _value = value;

    public override VfpValue Eval(IRowContext row, EvaluationContext ctx) => _value;

    public override Expression Build(BuildContext b)
        => Expression.Constant(_value, typeof(VfpValue));

    public override VfpTypeInfo Infer(ISchema schema) => _value.Type switch
    {
        VfpType.Character => new VfpTypeInfo(VfpType.Character, _value.AsString.Length),
        VfpType.Logical => new VfpTypeInfo(VfpType.Logical, 1),
        VfpType.Date => new VfpTypeInfo(VfpType.Date, 8),
        VfpType.DateTime => new VfpTypeInfo(VfpType.DateTime, 8),
        VfpType.Null => new VfpTypeInfo(VfpType.Null),
        _ => new VfpTypeInfo(VfpType.Numeric),
    };

    public override bool TryConstInt(out int value)
    {
        if (VfpRuntime.IsNumeric(_value)) { value = _value.AsInteger; return true; }
        value = 0;
        return false;
    }
}

internal sealed class FieldNode : AstNode
{
    private readonly string _name;
    public FieldNode(string name) => _name = name;

    public override VfpValue Eval(IRowContext row, EvaluationContext ctx)
        => VfpRuntime.GetField(row, _name);

    public override Expression Build(BuildContext b)
        => Expression.Call(typeof(VfpRuntime), nameof(VfpRuntime.GetField), null,
            b.Row, Expression.Constant(_name));

    public override VfpTypeInfo Infer(ISchema schema)
    {
        if (schema.TryGetColumn(_name, out char type, out int length, out int decimals))
            return new VfpTypeInfo(MapColumnType(type), length, decimals);
        return new VfpTypeInfo(VfpType.Unknown);
    }

    private static VfpType MapColumnType(char t) => char.ToUpperInvariant(t) switch
    {
        'C' or 'M' or 'V' => VfpType.Character,
        'N' or 'F' or 'B' => VfpType.Numeric,
        'I' => VfpType.Integer,
        'Y' => VfpType.Currency,
        'D' => VfpType.Date,
        'T' or '@' => VfpType.DateTime,
        'L' => VfpType.Logical,
        _ => VfpType.Unknown,
    };
}

internal sealed class UnaryNode : AstNode
{
    private readonly UnOp _op;
    private readonly AstNode _operand;
    public UnaryNode(UnOp op, AstNode operand) { _op = op; _operand = operand; }

    public override VfpValue Eval(IRowContext row, EvaluationContext ctx)
        => VfpRuntime.Unary(_op, _operand.Eval(row, ctx), ctx);

    public override Expression Build(BuildContext b)
        => Expression.Call(typeof(VfpRuntime), nameof(VfpRuntime.Unary), null,
            Expression.Constant(_op), _operand.Build(b), b.Ctx);

    public override VfpTypeInfo Infer(ISchema schema)
        => _op == UnOp.Not ? new VfpTypeInfo(VfpType.Logical, 1) : _operand.Infer(schema);
}

internal sealed class BinaryNode : AstNode
{
    private readonly BinOp _op;
    private readonly AstNode _left, _right;
    public BinaryNode(BinOp op, AstNode left, AstNode right) { _op = op; _left = left; _right = right; }

    public override VfpValue Eval(IRowContext row, EvaluationContext ctx)
        => VfpRuntime.Binary(_op, _left.Eval(row, ctx), _right.Eval(row, ctx), ctx);

    public override Expression Build(BuildContext b)
        => Expression.Call(typeof(VfpRuntime), nameof(VfpRuntime.Binary), null,
            Expression.Constant(_op), _left.Build(b), _right.Build(b), b.Ctx);

    public override VfpTypeInfo Infer(ISchema schema)
    {
        switch (_op)
        {
            case BinOp.Eq: case BinOp.ExactEq: case BinOp.Ne:
            case BinOp.Lt: case BinOp.Le: case BinOp.Gt: case BinOp.Ge:
            case BinOp.Dollar: case BinOp.And: case BinOp.Or:
                return new VfpTypeInfo(VfpType.Logical, 1);
        }

        var l = _left.Infer(schema);
        var r = _right.Infer(schema);

        if (_op == BinOp.Add)
        {
            if (l.Type == VfpType.Character || r.Type == VfpType.Character)
                return new VfpTypeInfo(VfpType.Character, l.Length + r.Length);
            if (l.Type is VfpType.Date or VfpType.DateTime) return l;
            if (r.Type is VfpType.Date or VfpType.DateTime) return r;
            return new VfpTypeInfo(VfpType.Numeric);
        }
        if (_op == BinOp.Sub)
        {
            if (l.Type == VfpType.Character || r.Type == VfpType.Character)
                return new VfpTypeInfo(VfpType.Character, l.Length + r.Length);
            if (l.Type is VfpType.Date or VfpType.DateTime &&
                r.Type is VfpType.Date or VfpType.DateTime)
                return new VfpTypeInfo(VfpType.Numeric);
            if (l.Type is VfpType.Date or VfpType.DateTime) return l;
            return new VfpTypeInfo(VfpType.Numeric);
        }
        return new VfpTypeInfo(VfpType.Numeric);
    }
}

internal sealed class FunctionNode : AstNode
{
    private readonly string _name;
    // Uppercased ONCE at parse/build time so the per-record hot path never calls
    // ToUpperInvariant. Both the interpreter and the compiled delegate dispatch on this.
    private readonly string _upper;
    private readonly AstNode[] _args;
    public FunctionNode(string name, AstNode[] args)
    {
        _name = name;
        _upper = name.ToUpperInvariant();
        _args = args;
    }

    public override VfpValue Eval(IRowContext row, EvaluationContext ctx)
    {
        var values = new VfpValue[_args.Length];
        for (int i = 0; i < _args.Length; i++) values[i] = _args[i].Eval(row, ctx);
        return VfpRuntime.CallFunction(_upper, values, ctx, row);
    }

    public override Expression Build(BuildContext b)
    {
        var elems = new Expression[_args.Length];
        for (int i = 0; i < _args.Length; i++) elems[i] = _args[i].Build(b);
        Expression array = Expression.NewArrayInit(typeof(VfpValue), elems);
        // Pass the already-uppercased name as a constant: zero string work per record.
        return Expression.Call(typeof(VfpRuntime), nameof(VfpRuntime.CallFunction), null,
            Expression.Constant(_upper), array, b.Ctx, b.Row);
    }

    public override VfpTypeInfo Infer(ISchema schema)
    {
        string n = _upper;
        switch (n)
        {
            case "UPPER": case "LOWER": case "ALLTRIM":
            case "TRIM": case "RTRIM": case "LTRIM":
                return ArgInfer(schema, 0) is var a0 && a0.Type == VfpType.Character
                    ? a0 : new VfpTypeInfo(VfpType.Character, a0.Length);
            case "LEFT": case "RIGHT":
                // LEFT/RIGHT length is argument index 1 (LEFT(str, n)).
                return new VfpTypeInfo(VfpType.Character, LiteralLen(1, ArgInfer(schema, 0).Length));
            case "SUBSTR":
                // SUBSTR length is argument index 2 (SUBSTR(str, start, n)).
                return new VfpTypeInfo(VfpType.Character, LiteralLen(2, ArgInfer(schema, 0).Length));
            case "STR": return new VfpTypeInfo(VfpType.Character, LiteralLen(1, 10));
            case "STRZERO": return new VfpTypeInfo(VfpType.Character, LiteralLen(1, 10));
            case "PADL": case "PADR": case "PADC":
                return new VfpTypeInfo(VfpType.Character, LiteralLen(1, ArgInfer(schema, 0).Length));
            case "SPACE": return new VfpTypeInfo(VfpType.Character, LiteralLen(0, 0));
            case "REPLICATE":
                return new VfpTypeInfo(VfpType.Character, ArgInfer(schema, 0).Length * LiteralLen(1, 0));
            case "CHRTRAN": case "STUFF": return ArgInfer(schema, 0);
            case "DTOC": return new VfpTypeInfo(VfpType.Character, 10);
            case "DTOS": return new VfpTypeInfo(VfpType.Character, 8);
            case "TTOC": return new VfpTypeInfo(VfpType.Character, 22);
            case "CDOW": return new VfpTypeInfo(VfpType.Character, 9);
            case "CMONTH": return new VfpTypeInfo(VfpType.Character, 9);
            case "CHR": return new VfpTypeInfo(VfpType.Character, 1);

            case "VAL": case "LEN": case "AT": case "RAT": case "ASC":
            case "INT": case "ROUND": case "ABS": case "MOD":
            case "DAY": case "MONTH": case "YEAR": case "DOW":
            case "RECNO": case "RECCOUNT":
                return new VfpTypeInfo(VfpType.Numeric);
            case "MAX": case "MIN": return ArgInfer(schema, 0);

            case "CTOD": return new VfpTypeInfo(VfpType.Date, 8);
            case "DATE": return new VfpTypeInfo(VfpType.Date, 8);
            case "GOMONTH": return ArgInfer(schema, 0);
            case "CTOT": case "DATETIME": return new VfpTypeInfo(VfpType.DateTime, 8);

            case "EMPTY": case "ISNULL": case "DELETED":
            case "BETWEEN": case "INLIST":
                return new VfpTypeInfo(VfpType.Logical, 1);

            case "IIF": return ArgInfer(schema, 1);
            default: return new VfpTypeInfo(VfpType.Unknown);
        }
    }

    private VfpTypeInfo ArgInfer(ISchema schema, int i)
        => i < _args.Length ? _args[i].Infer(schema) : new VfpTypeInfo(VfpType.Unknown);

    private int LiteralLen(int argIndex, int fallback)
        => argIndex < _args.Length && _args[argIndex].TryConstInt(out int v) ? v : fallback;
}
