using System;
using System.Globalization;
using System.Text;

namespace CrossVault.FoxDbf.Expressions;

/// <summary>Binary operator tag used by the AST / runtime.</summary>
internal enum BinOp
{
    Add, Sub, Mul, Div, Mod, Pow,
    Eq, ExactEq, Ne, Lt, Le, Gt, Ge, Dollar,
    And, Or,
}

/// <summary>Unary operator tag used by the AST / runtime.</summary>
internal enum UnOp { Neg, Not }

/// <summary>
/// Single source of truth for VFP operator and function semantics. Both the
/// tree-walking interpreter and the compiled (System.Linq.Expressions) delegate
/// call into these methods, guaranteeing identical results. Nothing here throws
/// on bad/null data — it coerces or yields <c>.NULL.</c> per VFP rules.
/// </summary>
internal static class VfpRuntime
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal static bool IsNumeric(in VfpValue v) =>
        v.Type is VfpType.Numeric or VfpType.Currency or VfpType.Integer;

    // ===================================================================
    //  FIELD ACCESS
    // ===================================================================

    public static VfpValue GetField(IRowContext row, string name)
        => VfpValue.FromClr(row.GetField(name));

    // ===================================================================
    //  UNARY
    // ===================================================================

    public static VfpValue Unary(UnOp op, VfpValue v, EvaluationContext ctx)
    {
        switch (op)
        {
            case UnOp.Neg:
                if (v.IsNull) return VfpValue.Null;
                return VfpValue.Number(-v.AsDouble);
            case UnOp.Not:
                if (v.IsNull) return VfpValue.Null;
                return VfpValue.Logical(!v.AsLogical);
            default:
                return VfpValue.Null;
        }
    }

    // ===================================================================
    //  BINARY
    // ===================================================================

    public static VfpValue Binary(BinOp op, VfpValue l, VfpValue r, EvaluationContext ctx)
    {
        // Logical operators implement three-valued logic and must NOT short to .NULL.
        if (op == BinOp.And) return And(l, r);
        if (op == BinOp.Or) return Or(l, r);

        // Everything else: .NULL. propagates.
        if (l.IsNull || r.IsNull) return VfpValue.Null;

        switch (op)
        {
            case BinOp.Add: return Add(l, r);
            case BinOp.Sub: return Sub(l, r);
            case BinOp.Mul: return VfpValue.Number(l.AsDouble * r.AsDouble);
            case BinOp.Div:
                return r.AsDouble == 0d ? VfpValue.Null
                                        : VfpValue.Number(l.AsDouble / r.AsDouble);
            case BinOp.Mod: return VfpValue.Number(Modulo(l.AsDouble, r.AsDouble));
            case BinOp.Pow: return VfpValue.Number(Math.Pow(l.AsDouble, r.AsDouble));

            case BinOp.Eq: return VfpValue.Logical(ValueEquals(l, r, ctx, exact: false));
            case BinOp.ExactEq: return VfpValue.Logical(ValueEquals(l, r, ctx, exact: true));
            case BinOp.Ne: return VfpValue.Logical(!ValueEquals(l, r, ctx, exact: false));
            case BinOp.Lt: return VfpValue.Logical(Compare(l, r, ctx) < 0);
            case BinOp.Le: return VfpValue.Logical(Compare(l, r, ctx) <= 0);
            case BinOp.Gt: return VfpValue.Logical(Compare(l, r, ctx) > 0);
            case BinOp.Ge: return VfpValue.Logical(Compare(l, r, ctx) >= 0);
            case BinOp.Dollar:
                return VfpValue.Logical(Contains(l.AsString, r.AsString, ctx));
            default:
                return VfpValue.Null;
        }
    }

    private static VfpValue And(VfpValue l, VfpValue r)
    {
        bool? a = l.IsNull ? null : l.AsLogical;
        bool? b = r.IsNull ? null : r.AsLogical;
        if (a == false || b == false) return VfpValue.Logical(false);
        if (a is null || b is null) return VfpValue.Null;
        return VfpValue.Logical(true);
    }

    private static VfpValue Or(VfpValue l, VfpValue r)
    {
        bool? a = l.IsNull ? null : l.AsLogical;
        bool? b = r.IsNull ? null : r.AsLogical;
        if (a == true || b == true) return VfpValue.Logical(true);
        if (a is null || b is null) return VfpValue.Null;
        return VfpValue.Logical(false);
    }

    private static VfpValue Add(VfpValue l, VfpValue r)
    {
        if (l.Type == VfpType.Character || r.Type == VfpType.Character)
            return VfpValue.Character(l.AsString + r.AsString);
        if (l.Type == VfpType.Date && IsNumeric(r))
        {
            if (l.AsDate == default) return VfpValue.Date(default);
            try { return VfpValue.Date(l.AsDate.AddDays((int)r.AsDouble)); }
            catch (ArgumentOutOfRangeException) { return VfpValue.Date(default); }
        }
        if (IsNumeric(l) && r.Type == VfpType.Date)
        {
            if (r.AsDate == default) return VfpValue.Date(default);
            try { return VfpValue.Date(r.AsDate.AddDays((int)l.AsDouble)); }
            catch (ArgumentOutOfRangeException) { return VfpValue.Date(default); }
        }
        if (l.Type == VfpType.DateTime && IsNumeric(r))
        {
            if (l.AsDateTime == default) return VfpValue.DateTime(default);
            try { return VfpValue.DateTime(l.AsDateTime.AddSeconds(r.AsDouble)); }
            catch (ArgumentOutOfRangeException) { return VfpValue.DateTime(default); }
        }
        if (IsNumeric(l) && r.Type == VfpType.DateTime)
        {
            if (r.AsDateTime == default) return VfpValue.DateTime(default);
            try { return VfpValue.DateTime(r.AsDateTime.AddSeconds(l.AsDouble)); }
            catch (ArgumentOutOfRangeException) { return VfpValue.DateTime(default); }
        }
        return VfpValue.Number(l.AsDouble + r.AsDouble);
    }

    private static VfpValue Sub(VfpValue l, VfpValue r)
    {
        if (l.Type == VfpType.Character || r.Type == VfpType.Character)
            return VfpValue.Character(MinusConcat(l.AsString, r.AsString));
        if (l.Type == VfpType.Date && r.Type == VfpType.Date)
            return VfpValue.Number((double)(l.AsDate.DayNumber - r.AsDate.DayNumber));
        if (l.Type == VfpType.DateTime && r.Type == VfpType.DateTime)
            return VfpValue.Number((l.AsDateTime - r.AsDateTime).TotalSeconds);
        if (l.Type == VfpType.Date && IsNumeric(r))
        {
            if (l.AsDate == default) return VfpValue.Date(default);
            try { return VfpValue.Date(l.AsDate.AddDays(-(int)r.AsDouble)); }
            catch (ArgumentOutOfRangeException) { return VfpValue.Date(default); }
        }
        if (l.Type == VfpType.DateTime && IsNumeric(r))
        {
            if (l.AsDateTime == default) return VfpValue.DateTime(default);
            try { return VfpValue.DateTime(l.AsDateTime.AddSeconds(-r.AsDouble)); }
            catch (ArgumentOutOfRangeException) { return VfpValue.DateTime(default); }
        }
        return VfpValue.Number(l.AsDouble - r.AsDouble);
    }

    /// <summary>VFP string "-": move the left operand's trailing blanks to the end.</summary>
    private static string MinusConcat(string l, string r)
    {
        int trimmed = l.Length;
        while (trimmed > 0 && l[trimmed - 1] == ' ') trimmed--;
        int trailing = l.Length - trimmed;
        return string.Concat(l.AsSpan(0, trimmed), r, new string(' ', trailing));
    }

    /// <summary>VFP MOD/% : the result takes the sign of the divisor.</summary>
    internal static double Modulo(double a, double b)
    {
        if (b == 0d) return 0d;
        return a - b * Math.Floor(a / b);
    }

    // ===================================================================
    //  EQUALITY + ORDERING
    // ===================================================================

    private static bool ValueEquals(VfpValue l, VfpValue r, EvaluationContext ctx, bool exact)
    {
        if (l.Type == VfpType.Character && r.Type == VfpType.Character)
        {
            if (exact) return StrEqExact(l.AsString, r.AsString, ctx);
            // SQL '=' (governed by SET ANSI) is selected ONLY when the host opts in via
            // EvaluationContext.SqlSemantics; otherwise the Xbase SET EXACT path is unchanged.
            if (ctx.SqlSemantics) return StrEqSql(l.AsString, r.AsString, ctx);
            return StrEqInexact(l.AsString, r.AsString, ctx);
        }
        if (IsNumeric(l) && IsNumeric(r)) return l.AsDouble == r.AsDouble;
        if (l.Type == VfpType.Logical && r.Type == VfpType.Logical)
            return l.AsLogical == r.AsLogical;
        if ((l.Type is VfpType.Date or VfpType.DateTime) &&
            (r.Type is VfpType.Date or VfpType.DateTime))
            return l.AsDateTime == r.AsDateTime;
        return false;
    }

    /// <summary>True when the active collation is plain ordinal (MACHINE) — the fast path.</summary>
    private static bool IsMachine(EvaluationContext ctx)
        => string.Equals(ctx.Collation.Name, "MACHINE", StringComparison.Ordinal);

    // The "==" operator (and EXACT-ON / INLIST): exact, but collation-aware. Under
    // GENERAL it is case- and accent-insensitive and ignores trailing blanks; under
    // MACHINE it is character-for-character ordinal.
    private static bool StrEqExact(string l, string r, EvaluationContext ctx)
    {
        if (IsMachine(ctx))
            return string.Equals(l, r, StringComparison.Ordinal);
        return ctx.Collation.Compare(l.AsSpan(), r.AsSpan()) == 0;
    }

    internal static bool StrEqInexact(string l, string r, EvaluationContext ctx)
    {
        if (ctx.Exact)
        {
            // SET EXACT ON: equal apart from trailing blanks (collation-aware).
            if (IsMachine(ctx))
                return string.Equals(l.TrimEnd(' '), r.TrimEnd(' '), StringComparison.Ordinal);
            return ctx.Collation.Compare(l.AsSpan(), r.AsSpan()) == 0;
        }
        // SET EXACT OFF: r must be a prefix of l.
        if (r.Length == 0) return true;
        if (IsMachine(ctx))
        {
            // MACHINE keys are one CP1252 byte per char, so char count == weight count.
            if (r.Length > l.Length) return false;
            return l.AsSpan(0, r.Length).SequenceEqual(r.AsSpan());
        }
        // GENERAL (any weighted collation): compare on the collated KEY length, NOT the
        // char length. Expansion chars (Æ/æ, Œ/œ, ß, Þ/þ) emit two weights each, so char
        // count diverges from weight count — e.g. "AEtest" = "Æ" must be TRUE because the
        // weights of "Æ" are exactly the prefix weights of "AEtest".
        byte[] kr = ctx.Collation.GetCollatedKey(r.AsSpan());
        if (kr.Length == 0) return true;
        byte[] kl = ctx.Collation.GetCollatedKey(l.AsSpan());
        if (kr.Length > kl.Length) return false;
        return kl.AsSpan(0, kr.Length).SequenceEqual(kr);
    }

    /// <summary>
    /// The SQL <c>=</c> operator's STRING comparison, governed by SET ANSI (used ONLY when
    /// <see cref="EvaluationContext.SqlSemantics"/> is on — the SQL executor's predicate path).
    /// ANSI OFF: compare up to the SHORTER operand's length, ORDER-INDEPENDENT
    /// (<c>"Smith" = "Sm"</c> and <c>"Sm" = "Smith"</c> are BOTH true). ANSI ON: pad the shorter
    /// operand with blanks for a full-length compare (equivalently, ignore trailing-blank
    /// differences). Collation-aware in both modes; <c>==</c> is never routed here.
    /// </summary>
    internal static bool StrEqSql(string l, string r, EvaluationContext ctx)
    {
        if (ctx.Ansi)
        {
            // ANSI ON: blank-pad the shorter to the longer, then full-length compare. This is
            // equivalent to comparing with trailing-blank differences ignored.
            if (IsMachine(ctx))
                return string.Equals(l.TrimEnd(' '), r.TrimEnd(' '), StringComparison.Ordinal);
            return ctx.Collation.Compare(l.AsSpan(), r.AsSpan()) == 0;
        }

        // ANSI OFF: the SHORTER operand must be a prefix of the longer (order-independent).
        if (IsMachine(ctx))
        {
            string shorter = l.Length <= r.Length ? l : r;
            string longer = l.Length <= r.Length ? r : l;
            if (shorter.Length == 0) return true;
            return longer.AsSpan(0, shorter.Length).SequenceEqual(shorter.AsSpan());
        }
        // Weighted collation: compare on the collated KEY bytes, shorter key as a prefix of the
        // longer (expansion chars emit multiple weights, so char count != weight count).
        byte[] kl = ctx.Collation.GetCollatedKey(l.AsSpan());
        byte[] kr = ctx.Collation.GetCollatedKey(r.AsSpan());
        byte[] shortK = kl.Length <= kr.Length ? kl : kr;
        byte[] longK = kl.Length <= kr.Length ? kr : kl;
        if (shortK.Length == 0) return true;
        return longK.AsSpan(0, shortK.Length).SequenceEqual(shortK);
    }

    /// <summary>The "$" operator: is <paramref name="l"/> contained in <paramref name="r"/>,
    /// honouring the active collation (case/accent-insensitive under GENERAL).</summary>
    internal static bool Contains(string l, string r, EvaluationContext ctx)
    {
        if (IsMachine(ctx))
            return r.Contains(l, StringComparison.Ordinal);
        if (l.Length == 0) return true;
        // Collate the needle ONCE (not re-collated per window position), then look for its
        // weight run inside the haystack's collated key. Sliding by weight-units — not
        // char-units — is required so expansion chars match: "AE" $ "Ætest" is TRUE because
        // the weights of "AE" appear contiguously in the weights of "Ætest".
        byte[] kl = ctx.Collation.GetCollatedKey(l.AsSpan());
        if (kl.Length == 0) return true;
        byte[] kr = ctx.Collation.GetCollatedKey(r.AsSpan());
        if (kl.Length > kr.Length) return false;
        var needle = kl.AsSpan();
        for (int i = 0; i + kl.Length <= kr.Length; i++)
            if (kr.AsSpan(i, kl.Length).SequenceEqual(needle))
                return true;
        return false;
    }

    internal static int Compare(VfpValue l, VfpValue r, EvaluationContext ctx)
    {
        if (l.Type == VfpType.Character && r.Type == VfpType.Character)
            return ctx.Collation.Compare(l.AsString.AsSpan(), r.AsString.AsSpan());
        if (IsNumeric(l) && IsNumeric(r)) return l.AsDouble.CompareTo(r.AsDouble);
        if ((l.Type is VfpType.Date or VfpType.DateTime) &&
            (r.Type is VfpType.Date or VfpType.DateTime))
            return l.AsDateTime.CompareTo(r.AsDateTime);
        if (l.Type == VfpType.Logical && r.Type == VfpType.Logical)
            return l.AsLogical.CompareTo(r.AsLogical);
        // Fallback: numeric coercion.
        return l.AsDouble.CompareTo(r.AsDouble);
    }

    // ===================================================================
    //  FUNCTION DISPATCH
    // ===================================================================

    /// <summary>
    /// Dispatches a VFP function. <paramref name="name"/> MUST already be uppercase
    /// (the AST uppercases once at build time), so the per-record hot path performs
    /// no string casing work here.
    /// </summary>
    public static VfpValue CallFunction(string name, VfpValue[] args, EvaluationContext ctx, IRowContext row)
    {
        // HOST HOOK (microVFP): a live interpreter may own the runtime STATE functions
        // (SEEK/ALIAS/SELECT/USED/RECNO(alias)/PCOUNT/SYS/EVALUATE/TYPE …) and user-defined
        // procedure/function calls. Consulted FIRST; false falls through to the built-ins below.
        if (row is IVfpFunctionHost host && host.TryInvoke(name, args, ctx, out var hostResult))
            return hostResult;

        // Functions that must SEE a .NULL. argument rather than propagate it.
        switch (name)
        {
            case "ISNULL": return VfpValue.Logical(Arg(args, 0).IsNull);
            case "EMPTY": return Empty(Arg(args, 0));
            case "IIF":
            {
                var c = Arg(args, 0);
                bool t = !c.IsNull && c.AsLogical;
                return t ? Arg(args, 1) : Arg(args, 2);
            }
        }

        // Default .NULL. propagation for everything else.
        for (int i = 0; i < args.Length; i++)
            if (args[i].IsNull) return VfpValue.Null;

        switch (name)
        {
            // ---- string ----
            case "UPPER": return VfpValue.Character(Arg(args, 0).AsString.ToUpperInvariant());
            case "LOWER": return VfpValue.Character(Arg(args, 0).AsString.ToLowerInvariant());
            case "ALLTRIM": return VfpValue.Character(Arg(args, 0).AsString.Trim(' '));
            case "TRIM":
            case "RTRIM": return VfpValue.Character(Arg(args, 0).AsString.TrimEnd(' '));
            case "LTRIM": return VfpValue.Character(Arg(args, 0).AsString.TrimStart(' '));
            case "LEFT": return Left(args);
            case "RIGHT": return Right(args);
            case "SUBSTR": return Substr(args);
            case "STR": return Str(args);
            case "STRZERO": return Strzero(args);
            case "PADL": return Pad(args, 'L');
            case "PADR": return Pad(args, 'R');
            case "PADC": return Pad(args, 'C');
            case "AT": return VfpValue.Integer(At(args, fromEnd: false));
            case "RAT": return VfpValue.Integer(At(args, fromEnd: true));
            case "STUFF": return Stuff(args);
            case "CHRTRAN": return Chrtran(args);
            case "STRTRAN": return Strtran(args);
            case "REPLICATE": return Replicate(args);
            case "SPACE": return VfpValue.Character(new string(' ', Math.Max(0, IntArg(args, 0))));
            case "LEN": return VfpValue.Integer(Arg(args, 0).AsString.Length);
            case "CHR":
            {
                int code = IntArg(args, 0) & 0xFF;
                char ch = ctx.Encoding is { } e1 ? e1.GetChars(new[] { (byte)code })[0]
                                                 : Cp1252.ToChar((byte)code);
                return VfpValue.Character(ch.ToString());
            }
            case "ASC":
            {
                var s = Arg(args, 0).AsString;
                if (s.Length == 0) return VfpValue.Integer(0);
                byte b = ctx.Encoding is { } e2 ? e2.GetBytes(new[] { s[0] })[0]
                                                : Cp1252.ToByte(s[0]);
                return VfpValue.Integer(b);
            }

            // ---- convert ----
            case "VAL": return VfpValue.Number(Val(Arg(args, 0).AsString));
            case "CTOD": return Ctod(Arg(args, 0).AsString);
            case "CTOT": return Ctot(Arg(args, 0).AsString);
            case "DTOC":
            {
                var d = Arg(args, 0).AsDate;
                return VfpValue.Character(d == default ? new string(' ', 10)
                                                       : d.ToString("MM/dd/yyyy", Inv));
            }
            case "DTOS":
            {
                var d = Arg(args, 0).AsDate;
                // VFP: an empty/blank date yields 8 spaces, NOT "00010101".
                return VfpValue.Character(d == default ? new string(' ', 8)
                                                       : d.ToString("yyyyMMdd", Inv));
            }
            case "TTOC": return VfpValue.Character(Arg(args, 0).AsDateTime.ToString("MM/dd/yyyy hh:mm:ss tt", Inv));

            // ---- date ----
            case "DATE": return VfpValue.Date(DateOnly.FromDateTime(DateTime.Today));
            case "DATETIME": return VfpValue.DateTime(DateTime.Now);
            case "DAY": return VfpValue.Integer(Arg(args, 0).AsDate.Day);
            case "MONTH": return VfpValue.Integer(Arg(args, 0).AsDate.Month);
            case "YEAR": return VfpValue.Integer(Arg(args, 0).AsDate.Year);
            case "DOW": return VfpValue.Integer((int)Arg(args, 0).AsDate.DayOfWeek + 1);
            case "CDOW": return VfpValue.Character(Inv.DateTimeFormat.GetDayName(Arg(args, 0).AsDate.DayOfWeek));
            case "CMONTH": return VfpValue.Character(Inv.DateTimeFormat.GetMonthName(Arg(args, 0).AsDate.Month));
            case "GOMONTH": return Gomonth(args);

            // ---- numeric ----
            case "INT": return VfpValue.Number(Math.Truncate(Arg(args, 0).AsDouble));
            case "ROUND": return VfpValue.Number(Round(Arg(args, 0).AsDouble, IntArg(args, 1)));
            case "ABS": return VfpValue.Number(Math.Abs(Arg(args, 0).AsDouble));
            case "MOD": return VfpValue.Number(Modulo(Arg(args, 0).AsDouble, Arg(args, 1).AsDouble));
            case "MAX": return MaxMin(args, ctx, max: true);
            case "MIN": return MaxMin(args, ctx, max: false);

            // ---- system / logical ----
            case "RECNO": return VfpValue.Integer(row.RecNo);
            case "RECCOUNT": return VfpValue.Integer(row.RecCount);
            case "DELETED": return VfpValue.Logical(row.Deleted);
            case "BETWEEN": return Between(args, ctx);
            case "INLIST": return Inlist(args, ctx);

            default:
                // The §C.17/§C.18 string/DBCS-variant extras are dispatched in a SEPARATE method (below),
                // NOT inline in this giant switch: keeping CallFunction's body — and hence its native stack
                // frame, which the JIT allocates in full on entry even though a user-function call returns
                // early at the host hook above — at its prior size is what preserves the MaxCallDepth
                // recursion headroom (CallFunction sits on EVERY nested UDF call). Same rationale as the
                // interpreter's TryInvokeFileIo split. Genuinely unknown names still fall through to .NULL.
                return CallStringExtras(name, args);
        }
    }

    /// <summary>The §C.17/§C.18 string / DBCS-variant additions (LEFTC/RIGHTC/SUBSTRC/STUFFC/CHRTRANC,
    /// AT_C/RATC/ATCC, ISLEADBYTE, LIKE/LIKEC, NORMALIZE, TXTWIDTH), split out of <see cref="CallFunction"/>'s
    /// hot switch on purpose (see its <c>default</c> arm). On a single-byte code page (CP1252, our target)
    /// one char == one byte, so each C-variant is byte-identical to its base function (verified against the
    /// VFP9 runtime); they exist purely for VFP9 name compatibility (FLAGGED: real double-byte behaviour is
    /// untested). Returns <c>.NULL.</c> for a genuinely unknown name (CallFunction's former default).</summary>
    private static VfpValue CallStringExtras(string name, VfpValue[] args)
    {
        switch (name)
        {
            case "LEFTC": return Left(args);
            case "RIGHTC": return Right(args);
            case "SUBSTRC": return Substr(args);
            case "STUFFC": return Stuff(args);
            case "CHRTRANC": return Chrtran(args);
            case "AT_C": return VfpValue.Integer(At(args, fromEnd: false));
            case "RATC": return VfpValue.Integer(At(args, fromEnd: true));
            case "ATCC": return VfpValue.Integer(At(args, fromEnd: false, StringComparison.OrdinalIgnoreCase));
            // ISLEADBYTE(cExpr): the first byte is a DBCS lead byte. Always .F. on a single-byte code page
            // (VFP9-verified). Takes ONE argument only (a 2nd arg raises an error in VFP9); we ignore extras.
            case "ISLEADBYTE": return VfpValue.Logical(false);
            // LIKE(cPattern, cString) / LIKEC (its DBCS twin, identical on CP1252): anchored wildcard match,
            // '*' = any run (incl. empty), '?' = exactly one char, everything else literal, case-SENSITIVE,
            // trailing blanks significant on both sides (FOXPLUS default). No bracket classes.
            case "LIKE":
            case "LIKEC": return VfpValue.Logical(Like(Arg(args, 0).AsString, Arg(args, 1).AsString));
            // NORMALIZE(cExpr): a PRAGMATIC canonicaliser — upper-cases outside string literals, drops
            // whitespace, rewrites the `->` alias operator to `.`, and re-emits '/" literals double-quoted
            // (content verbatim). FLAGGED (needs a full expression compiler, out of P3 scope): VFP also folds
            // constant arithmetic ('1+2'→'3') and treats trailing text after a complete expression as a
            // comment ('x y'→'X') — those two behaviours are deliberately NOT reproduced.
            case "NORMALIZE": return VfpValue.Character(Normalize(Arg(args, 0).AsString));
            // TXTWIDTH(cString [, cFont, nSize, cStyle]): headless — no GDI/font engine. Returns the CHARACTER
            // COUNT, which is EXACT for a fixed-pitch font (VFP: TXTWIDTH(s,'Courier New',10) == LEN(s)) and a
            // FLAGGED approximation for the proportional desktop font (that value is GUI-/machine-bound).
            case "TXTWIDTH": return VfpValue.Integer(Arg(args, 0).AsString.Length);
            // Unknown function: never throw at evaluation time.
            default: return VfpValue.Null;
        }
    }

    private static VfpValue Arg(VfpValue[] a, int i) => i < a.Length ? a[i] : VfpValue.Null;
    private static int IntArg(VfpValue[] a, int i) => i < a.Length ? (int)a[i].AsDouble : 0;

    /// <summary>Truthiness of a condition value the SAME way IIF/ICASE treat it: a <c>.NULL.</c> is false,
    /// otherwise the logical value. Used by the compiled + tree-walk ICASE special form.</summary>
    internal static bool AsCondition(VfpValue v) => !v.IsNull && v.AsLogical;

    private static VfpValue Left(VfpValue[] a)
    {
        var s = Arg(a, 0).AsString;
        int n = Math.Clamp(IntArg(a, 1), 0, s.Length);
        return VfpValue.Character(s.Substring(0, n));
    }

    private static VfpValue Right(VfpValue[] a)
    {
        var s = Arg(a, 0).AsString;
        int n = Math.Clamp(IntArg(a, 1), 0, s.Length);
        return VfpValue.Character(s.Substring(s.Length - n));
    }

    private static VfpValue Substr(VfpValue[] a)
    {
        var s = Arg(a, 0).AsString;
        int start = IntArg(a, 1);
        if (start < 1) start = 1;
        int i = start - 1;
        if (i >= s.Length) return VfpValue.Character(string.Empty);
        int len = a.Length > 2 ? IntArg(a, 2) : s.Length - i;
        if (len < 0) len = 0;
        if (i + len > s.Length) len = s.Length - i;
        return VfpValue.Character(s.Substring(i, len));
    }

    private static VfpValue Str(VfpValue[] a)
    {
        double n = Arg(a, 0).AsDouble;
        int len = a.Length > 1 ? IntArg(a, 1) : 10;
        int dec = a.Length > 2 ? IntArg(a, 2) : 0;
        return VfpValue.Character(StrCore(n, len, dec));
    }

    /// <summary>
    /// VFP STR(): right-justified, leading-space padded. On overflow VFP only returns
    /// asterisks when the value HAS a decimal point and the width cannot even hold the
    /// integer digits; otherwise it REDUCES the decimal count to fit. So
    /// STR(1234.5,6,2) = "1234.5", not "******".
    /// </summary>
    private static string StrCore(double n, int len, int dec)
    {
        if (len < 0) len = 0;
        if (dec < 0) dec = 0;

        // Integer-digit count of the rounded-to-zero magnitude, including a sign slot.
        double absInt = Math.Floor(Math.Abs(n));
        int intDigits = absInt < 1 ? 1 : (int)Math.Floor(Math.Log10(absInt)) + 1;
        if (n < 0) intDigits++; // room for '-'

        if (dec > 0 && len <= intDigits)
            return new string('*', len);

        // Shrink decimals to whatever fits: width - intDigits - 1 (the '.').
        int effectiveDec = dec > 0 ? Math.Clamp(len - intDigits - 1, 0, dec) : 0;
        double rounded = Round(n, effectiveDec);
        string s = rounded.ToString("F" + effectiveDec, Inv);
        if (s.Length > len) return new string('*', len);
        return s.PadLeft(len);
    }

    private static VfpValue Strzero(VfpValue[] a)
    {
        double n = Arg(a, 0).AsDouble;
        int len = a.Length > 1 ? IntArg(a, 1) : 10;
        int dec = a.Length > 2 ? IntArg(a, 2) : 0;
        // VFP STRZERO == STR() then replace each LEADING space with '0' (keeping the
        // sign in place): STRZERO(-1234,8) = "000-1234"; STRZERO(-1234,4) = "****".
        string s = StrCore(n, len, dec);
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length && chars[i] == ' '; i++)
            chars[i] = '0';
        return VfpValue.Character(new string(chars));
    }

    private static VfpValue Pad(VfpValue[] a, char mode)
    {
        string s = Arg(a, 0).AsString;
        int len = IntArg(a, 1);
        if (len < 0) len = 0;
        char pad = ' ';
        if (a.Length > 2)
        {
            var p = a[2].AsString;
            if (p.Length > 0) pad = p[0];
        }
        if (s.Length >= len)
            return VfpValue.Character(mode == 'L' ? s.Substring(s.Length - len) : s.Substring(0, len));
        int total = len - s.Length;
        switch (mode)
        {
            case 'L': return VfpValue.Character(new string(pad, total) + s);
            case 'R': return VfpValue.Character(s + new string(pad, total));
            default:
                int left = total / 2;
                int right = total - left;
                return VfpValue.Character(new string(pad, left) + s + new string(pad, right));
        }
    }

    private static int At(VfpValue[] a, bool fromEnd, StringComparison cmp = StringComparison.Ordinal)
    {
        string f = Arg(a, 0).AsString;
        string w = Arg(a, 1).AsString;
        int occ = a.Length > 2 ? IntArg(a, 2) : 1;
        if (occ < 1) occ = 1;
        if (f.Length == 0) return 0;
        int idx = -1;
        if (!fromEnd)
        {
            int from = 0;
            for (int k = 0; k < occ; k++)
            {
                idx = w.IndexOf(f, from, cmp);
                if (idx < 0) break;
                from = idx + 1;
            }
        }
        else
        {
            int start = w.Length - 1;
            for (int k = 0; k < occ; k++)
            {
                if (start < 0) { idx = -1; break; }
                idx = w.LastIndexOf(f, start, cmp);
                if (idx < 0) break;
                start = idx - 1;
            }
        }
        return idx < 0 ? 0 : idx + 1;
    }

    private static VfpValue Stuff(VfpValue[] a)
    {
        string s = Arg(a, 0).AsString;
        int start = IntArg(a, 1);
        int len = IntArg(a, 2);
        string repl = Arg(a, 3).AsString;
        int i = start - 1;
        if (i < 0) i = 0;
        if (i > s.Length) i = s.Length;
        if (len < 0) len = 0;
        if (i + len > s.Length) len = s.Length - i;
        return VfpValue.Character(string.Concat(s.AsSpan(0, i), repl, s.AsSpan(i + len)));
    }

    private static VfpValue Chrtran(VfpValue[] a)
    {
        string s = Arg(a, 0).AsString;
        string from = Arg(a, 1).AsString;
        string to = Arg(a, 2).AsString;
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
        {
            int idx = from.IndexOf(ch);
            if (idx < 0) sb.Append(ch);
            else if (idx < to.Length) sb.Append(to[idx]);
            // else: char is deleted (replacement shorter than search).
        }
        return VfpValue.Character(sb.ToString());
    }

    private static VfpValue Strtran(VfpValue[] a)
    {
        string source = Arg(a, 0).AsString;
        string sought = Arg(a, 1).AsString;
        string replacement = a.Length >= 3 ? Arg(a, 2).AsString : string.Empty;
        return VfpValue.Character(sought.Length == 0
            ? source
            : source.Replace(sought, replacement, StringComparison.Ordinal));
    }

    /// <summary>VFP <c>LIKE()</c> wildcard match: <c>*</c> matches any run (including empty), <c>?</c>
    /// matches exactly one character, every other character is literal. Case-SENSITIVE and fully anchored
    /// (the whole string must match), so trailing blanks are significant on both sides. Iterative
    /// backtracking matcher (no recursion, linear-ish).</summary>
    private static bool Like(string pat, string s)
    {
        int p = 0, si = 0, star = -1, ss = 0;
        while (si < s.Length)
        {
            if (p < pat.Length && (pat[p] == '?' || pat[p] == s[si])) { p++; si++; }
            else if (p < pat.Length && pat[p] == '*') { star = p; ss = si; p++; }
            else if (star != -1) { p = star + 1; ss++; si = ss; }
            else return false;
        }
        while (p < pat.Length && pat[p] == '*') p++;
        return p == pat.Length;
    }

    /// <summary>VFP <c>NORMALIZE()</c> — pragmatic canonicaliser (see the dispatch comment for the FLAGGED
    /// limits). Outside string literals: upper-case letters, drop spaces/tabs, rewrite <c>-&gt;</c> to
    /// <c>.</c>. A <c>'</c>- or <c>"</c>-delimited literal is re-emitted double-quoted with its content
    /// verbatim (case preserved, inner spaces preserved).</summary>
    private static string Normalize(string e)
    {
        var sb = new StringBuilder(e.Length);
        int i = 0;
        while (i < e.Length)
        {
            char c = e[i];
            if (c == '\'' || c == '"')
            {
                char q = c;
                i++;
                sb.Append('"');
                while (i < e.Length && e[i] != q) { sb.Append(e[i]); i++; }
                if (i < e.Length) i++;                 // consume the closing delimiter
                sb.Append('"');
            }
            else if (c == ' ' || c == '\t') i++;       // whitespace dropped
            else if (c == '-' && i + 1 < e.Length && e[i + 1] == '>') { sb.Append('.'); i += 2; }
            else { sb.Append(char.ToUpperInvariant(c)); i++; }
        }
        return sb.ToString();
    }

    private static VfpValue Replicate(VfpValue[] a)
    {
        string s = Arg(a, 0).AsString;
        int n = Math.Max(0, IntArg(a, 1));
        if (n == 0 || s.Length == 0) return VfpValue.Character(string.Empty);
        var sb = new StringBuilder(s.Length * n);
        for (int k = 0; k < n; k++) sb.Append(s);
        return VfpValue.Character(sb.ToString());
    }

    private static double Val(string s)
    {
        int i = 0, n = s.Length;
        while (i < n && s[i] == ' ') i++;
        int start = i;
        bool any = false;
        if (i < n && (s[i] == '+' || s[i] == '-')) i++;
        while (i < n && char.IsAsciiDigit(s[i])) { i++; any = true; }
        if (i < n && s[i] == '.')
        {
            i++;
            while (i < n && char.IsAsciiDigit(s[i])) { i++; any = true; }
        }
        if (!any) return 0d;
        return double.TryParse(s.AsSpan(start, i - start), NumberStyles.Float, Inv, out var d) ? d : 0d;
    }

    private static VfpValue Ctod(string s)
    {
        s = s.Trim();
        string[] formats = { "MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-dd", "yyyy/MM/dd", "dd.MM.yyyy" };
        if (DateTime.TryParseExact(s, formats, Inv, DateTimeStyles.None, out var dt))
            return VfpValue.Date(DateOnly.FromDateTime(dt));
        return VfpValue.Date(default);
    }

    private static VfpValue Ctot(string s)
    {
        s = s.Trim();
        if (DateTime.TryParse(s, Inv, DateTimeStyles.None, out var dt))
            return VfpValue.DateTime(dt);
        return VfpValue.DateTime(default);
    }

    private static VfpValue Gomonth(VfpValue[] a)
    {
        var d = Arg(a, 0);
        int m = IntArg(a, 1);
        if (d.Type == VfpType.DateTime)
        {
            // VFP9 returns an empty Date (not DateTime) for GOMONTH(CTOT(''), n).
            if (d.AsDateTime == default) return VfpValue.Date(default);
            try { return VfpValue.DateTime(d.AsDateTime.AddMonths(m)); }
            catch (ArgumentOutOfRangeException) { return VfpValue.DateTime(default); }
        }
        if (d.Type == VfpType.Date)
        {
            if (d.AsDate == default) return VfpValue.Date(default);
            try { return VfpValue.Date(d.AsDate.AddMonths(m)); }
            catch (ArgumentOutOfRangeException) { return VfpValue.Date(default); }
        }
        return VfpValue.Date(d.AsDate.AddMonths(m));
    }

    internal static double Round(double n, int dec)
    {
        if (dec >= 0)
            return Math.Round(n, Math.Min(dec, 15), MidpointRounding.AwayFromZero);
        double factor = Math.Pow(10, -dec);
        return Math.Round(n / factor, 0, MidpointRounding.AwayFromZero) * factor;
    }

    private static VfpValue MaxMin(VfpValue[] a, EvaluationContext ctx, bool max)
    {
        if (a.Length == 0) return VfpValue.Null;
        var best = a[0];
        for (int i = 1; i < a.Length; i++)
        {
            int cmp = Compare(a[i], best, ctx);
            if (max ? cmp > 0 : cmp < 0) best = a[i];
        }
        return best;
    }

    private static VfpValue Empty(VfpValue v)
    {
        switch (v.Type)
        {
            case VfpType.Null: return VfpValue.Logical(true);
            case VfpType.Character: return VfpValue.Logical(v.AsString.Trim().Length == 0);
            case VfpType.Numeric:
            case VfpType.Currency:
            case VfpType.Integer: return VfpValue.Logical(v.AsDouble == 0d);
            case VfpType.Logical: return VfpValue.Logical(!v.AsLogical);
            case VfpType.Date: return VfpValue.Logical(v.AsDate == default);
            case VfpType.DateTime: return VfpValue.Logical(v.AsDateTime == default);
            default: return VfpValue.Logical(true);
        }
    }

    private static VfpValue Between(VfpValue[] a, EvaluationContext ctx)
    {
        var v = Arg(a, 0); var lo = Arg(a, 1); var hi = Arg(a, 2);
        return VfpValue.Logical(Compare(v, lo, ctx) >= 0 && Compare(v, hi, ctx) <= 0);
    }

    private static VfpValue Inlist(VfpValue[] a, EvaluationContext ctx)
    {
        if (a.Length == 0) return VfpValue.Logical(false);
        var v = a[0];
        for (int i = 1; i < a.Length; i++)
            if (ValueEquals(v, a[i], ctx, exact: true)) return VfpValue.Logical(true);
        return VfpValue.Logical(false);
    }
}
