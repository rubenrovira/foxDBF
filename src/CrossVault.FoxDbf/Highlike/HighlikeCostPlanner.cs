using System.Globalization;
using CrossVault.FoxDbf;
using CrossVault.FoxDbf.Index;

namespace CrossVault.FoxDbf.Highlike;

/// <summary>
/// The Phase A cost-based planner (design §7). Consumes the <c>.stx</c> statistics
/// (<see cref="StxStatistics"/>) to estimate, for a VFP boolean filter, how many records it will
/// match — and from that drives the three plan decisions: AND-ordering (cheapest leaf first),
/// index-vs-scan (a leaf estimated to match more than
/// <see cref="HighlikeOptions.IndexVsScanThreshold"/> of the table is full-scanned), and
/// low-selectivity guard lifting (a Core-skipped NOT-keyed / boolean / low-NDV tag the stats prove
/// selective may be driven).
/// </summary>
/// <remarks>
/// NON-NEGOTIABLE INVARIANT: statistics are HINTS ONLY. <see cref="EstimateRows"/> and every decision
/// it feeds may only change the PLAN (speed) — never the result set. Stale, missing, or deliberately
/// WRONG stats can only make the planner pick a worse plan; the residual still confirms every
/// candidate, so the answer is always the Core optimizer's answer (and a full scan's).
///
/// Estimation model (design §7):
/// <list type="bullet">
///   <item>equality <c>expr = const</c>: <c>const</c> outside <c>[min,max]</c> ⇒ 0 (exact pruning);
///   else <c>reccount / max(ndv,1)</c>.</item>
///   <item>range (<c>BETWEEN</c> / one-sided): clamp to <c>[min,max]</c> then linear interpolation.</item>
///   <item>AND: <c>reccount · Π frac_i</c> (independence).</item>
///   <item>OR: <c>reccount · (1 − Π(1 − frac_i))</c> (inclusion–exclusion).</item>
///   <item>a non-optimizable leaf ⇒ <see cref="HighlikeOptions.DefaultLeafFraction"/>.</item>
/// </list>
/// </remarks>
public sealed class HighlikeCostPlanner
{
    /// <summary>The statistics backing the estimates (null ⇒ no stats; the planner degrades to defaults).</summary>
    public StxStatistics? Statistics { get; }

    /// <summary>The table record count the estimates are scaled against.</summary>
    public int RecordCount { get; }

    /// <summary>The plan-influencing options (thresholds / default fractions). Never affects the result.</summary>
    public HighlikeOptions Options { get; }

    /// <summary>The optional CDX whose tags the planner maps predicates onto (null ⇒ key-expression lookup only).</summary>
    public CdxFile? Cdx { get; }

    /// <summary>Create a planner over the given <paramref name="statistics"/> (null ⇒ defaults-only).</summary>
    public HighlikeCostPlanner(StxStatistics? statistics, int recordCount,
        HighlikeOptions? options = null, CdxFile? cdx = null)
    {
        Statistics = statistics;
        RecordCount = recordCount;
        Options = options ?? HighlikeOptions.Default;
        Cdx = cdx;
    }

    /// <summary>
    /// Estimate how many records <paramref name="predicate"/> matches (design §7). A pure HINT —
    /// it may only influence the plan, never the result set. Never throws on a malformed predicate
    /// (degrades to the default-fraction estimate).
    /// </summary>
    public double EstimateRows(string predicate)
    {
        double rc = Math.Max(0, RecordCount);
        if (rc <= 0) return 0;
        double frac;
        try { frac = Estimate(predicate ?? string.Empty).Frac; }
        catch { frac = Options.DefaultLeafFraction; }
        if (double.IsNaN(frac) || frac < 0) frac = 0;
        if (frac > 1) frac = 1;
        return rc * frac;
    }

    /// <summary>
    /// The estimated selectivity fraction of <paramref name="predicate"/> —
    /// <see cref="EstimateRows"/> divided by <see cref="RecordCount"/> (clamped to <c>[0,1]</c>).
    /// </summary>
    public double EstimateFraction(string predicate)
    {
        if (RecordCount <= 0) return 0;
        double f = EstimateRows(predicate) / RecordCount;
        return f < 0 ? 0 : (f > 1 ? 1 : f);
    }

    /// <summary>
    /// The INDEX-vs-SCAN decision (design §7, decision 2): true when <paramref name="predicate"/> is
    /// estimated to match MORE than <see cref="HighlikeOptions.IndexVsScanThreshold"/> of the table, so
    /// a full scan is preferred over driving the index. PLAN-only.
    /// </summary>
    /// <remarks>
    /// THE PRINCIPLE (design §7): a cost-based full scan may be preferred ONLY when the high estimate is
    /// BACKED BY REAL STATISTICS (a tag with a usable <c>.stx</c> entry whose stats-based fraction
    /// exceeds the threshold). When a leaf is NOT estimatable (no stats for the key / an unknown literal
    /// / the <see cref="HighlikeOptions.DefaultLeafFraction"/> had to be used) this returns false — the
    /// default fraction is for ORDERING only, never the index-vs-scan cutoff. Ignorance must never
    /// DISABLE a usable index.
    /// </remarks>
    public bool PrefersFullScan(string predicate)
    {
        Est e;
        try { e = Estimate(predicate ?? string.Empty); }
        catch { return false; }
        if (!e.StatsBacked) return false; // ignorance keeps the index (default fraction never demotes)
        return Clamp01(e.Frac) > Options.IndexVsScanThreshold;
    }

    /// <summary>
    /// The LOW-SELECTIVITY GUARD-LIFT decision (design §11): true when <paramref name="predicate"/> is
    /// estimated SELECTIVE enough (its fraction is at or below
    /// <see cref="HighlikeOptions.IndexVsScanThreshold"/>) that driving a Core-skipped NOT-keyed /
    /// boolean / low-NDV tag pays. PLAN-only — the residual still confirms every candidate.
    /// </summary>
    public bool ShouldLiftGuard(string predicate)
        => EstimateFraction(predicate) <= Options.IndexVsScanThreshold;

    // ============================================================ predicate fraction estimator

    /// <summary>
    /// A selectivity estimate plus whether it is BACKED BY REAL STATISTICS. <see cref="StatsBacked"/> is
    /// true only when the fraction was derived from a usable <c>.stx</c> entry (NDV / min / max); it is
    /// false whenever any contributing leaf had to fall back to
    /// <see cref="HighlikeOptions.DefaultLeafFraction"/> (no stats / unknown literal / unrecognised
    /// shape). The index-vs-scan cutoff may only act on a stats-backed estimate.
    /// </summary>
    private readonly struct Est
    {
        public readonly double Frac;
        public readonly bool StatsBacked;
        public Est(double frac, bool statsBacked) { Frac = frac; StatsBacked = statsBacked; }
    }

    /// <summary>
    /// The recursive selectivity estimator: OR (inclusion–exclusion) over AND (product) over an optional
    /// NOT prefix over a leaf (comparison / BETWEEN / INLIST). Returns the fraction in <c>[0,1]</c> AND
    /// whether it is stats-backed; a leaf with no usable statistic degrades to
    /// <see cref="HighlikeOptions.DefaultLeafFraction"/> (NOT stats-backed). A compound is stats-backed
    /// only when EVERY contributing leaf is — any ignorance taints the whole estimate.
    /// </summary>
    private Est Estimate(string s)
    {
        s = StripOuter(s.Trim());
        if (s.Length == 0) return new Est(Options.DefaultLeafFraction, false);

        // OR (lowest precedence) — inclusion/exclusion 1 − Π(1 − f_i).
        var ors = SplitTop(s, "OR");
        if (ors.Count > 1)
        {
            double prod = 1.0; bool backed = true;
            foreach (var part in ors) { var e = Estimate(part); prod *= (1.0 - Clamp01(e.Frac)); backed &= e.StatsBacked; }
            return new Est(Clamp01(1.0 - prod), backed);
        }

        // AND — product of fractions (independence assumption).
        var ands = SplitTop(s, "AND");
        if (ands.Count > 1)
        {
            double prod = 1.0; bool backed = true;
            foreach (var part in ands) { var e = Estimate(part); prod *= Clamp01(e.Frac); backed &= e.StatsBacked; }
            return new Est(Clamp01(prod), backed);
        }

        // NOT prefix — complement.
        int notLen = LeadingNot(s);
        if (notLen > 0)
        {
            var e = Estimate(s.Substring(notLen));
            return new Est(Clamp01(1.0 - Clamp01(e.Frac)), e.StatsBacked);
        }

        return LeafEst(s);
    }

    /// <summary>Estimate a single leaf (comparison / BETWEEN / INLIST / bare term).</summary>
    private Est LeafEst(string leaf)
    {
        leaf = StripOuter(leaf.Trim());
        if (leaf.Length == 0) return new Est(Options.DefaultLeafFraction, false);

        // A depth-0 comparison operator → a (key)(op)(const) leaf.
        int opStart = FindTopComparison(leaf, out int opLen);
        if (opStart >= 0)
        {
            string left = leaf.Substring(0, opStart).Trim();
            string opTok = leaf.Substring(opStart, opLen);
            string right = leaf.Substring(opStart + opLen).Trim();
            return ComparisonEst(left, opTok, right);
        }

        // A BETWEEN / INLIST function leaf.
        if (TryFunction(leaf, out string fname, out var args))
        {
            if (fname == "BETWEEN" && args.Count == 3)
                return BetweenEst(args[0], args[1], args[2]);
            if (fname == "INLIST" && args.Count >= 2)
                return InListEst(args[0], args.GetRange(1, args.Count - 1));
        }

        // Anything else (bare boolean term, unrecognised shape) → the residual default (NOT stats-backed).
        return new Est(Options.DefaultLeafFraction, false);
    }

    private Est ComparisonEst(string left, string opTok, string right)
    {
        // Identify the constant side; the other side is the key expression.
        string keyExpr; CmpOp op; double? num; string? str;

        if (TryConst(right, out num, out str))
        {
            keyExpr = left; op = MapOp(opTok);
        }
        else if (TryConst(left, out num, out str))
        {
            keyExpr = right; op = Flip(MapOp(opTok));
        }
        else
        {
            // field-to-field or unrecognised → no usable estimate.
            return new Est(Options.DefaultLeafFraction, false);
        }

        var stat = Statistics?.ForKey(keyExpr);
        if (stat is null) return new Est(Options.DefaultLeafFraction, false);

        double ndvFrac = 1.0 / Math.Max(1, stat.Ndv);

        switch (op)
        {
            case CmpOp.Eq:
                // EXACT min/max pruning: a constant outside the observed range matches nothing.
                if (PrunesToZero(stat, num, str)) return new Est(0.0, true);

                // PHASE C: MCV (Most-Common-Values) — check if the constant is in the MCV list.
                if (stat.Mcv is { Count: > 0 })
                {
                    // For numeric constants, format and check MCV list.
                    if (num is not null)
                    {
                        string numStr = num.Value.ToString(CultureInfo.InvariantCulture);
                        foreach (var mcv in stat.Mcv)
                        {
                            if (string.Equals(mcv.Value, numStr, StringComparison.Ordinal))
                                return new Est(Clamp01((double)mcv.Count / RecordCount), true);
                        }
                        // Value is NOT in the MCV list → it's a rare value.
                        // Estimate: (total_rows - sum_mcv_counts) / max(ndv - mcv_count, 1)
                        long mcvSum = stat.Mcv.Sum(m => m.Count);
                        long rareNdv = Math.Max(stat.Ndv - stat.Mcv.Count, 1);
                        double rareCount = (RecordCount - mcvSum) / (double)rareNdv;
                        return new Est(Clamp01(rareCount / RecordCount), true);
                    }
                    // For string constants, check if it's in the MCV list (for MACHINE collation only).
                    else if (str is not null && IsIdentityCollation(stat.Collation))
                    {
                        foreach (var mcv in stat.Mcv)
                        {
                            if (string.Equals(mcv.Value, str, StringComparison.Ordinal))
                                return new Est(Clamp01((double)mcv.Count / RecordCount), true);
                        }
                        // Rare string value.
                        long mcvSum = stat.Mcv.Sum(m => m.Count);
                        long rareNdv = Math.Max(stat.Ndv - stat.Mcv.Count, 1);
                        double rareCount = (RecordCount - mcvSum) / (double)rareNdv;
                        return new Est(Clamp01(rareCount / RecordCount), true);
                    }
                }

                return new Est(ndvFrac, true);

            case CmpOp.Ne:
                if (PrunesToZero(stat, num, str)) return new Est(1.0, true); // everything is != a never-present value
                return new Est(Clamp01(1.0 - ndvFrac), true);

            case CmpOp.Lt:
            case CmpOp.Le:
            case CmpOp.Gt:
            case CmpOp.Ge:
                // num is set for numeric AND Date/DateTime literals (the latter on the OADate scale);
                // string ranges have no histogram → KISS default (v2: histograms).
                if (num is null) return new Est(Options.DefaultLeafFraction, false);
                if (!TryBounds(stat, out _, out _)) return new Est(Options.DefaultLeafFraction, false);
                return new Est(RangeFrac(stat, op, num.Value), true);

            default:
                return new Est(Options.DefaultLeafFraction, false);
        }
    }

    private Est BetweenEst(string keyArg, string loArg, string hiArg)
    {
        if (!TryConst(loArg, out double? lo, out _) || lo is null) return new Est(Options.DefaultLeafFraction, false);
        if (!TryConst(hiArg, out double? hi, out _) || hi is null) return new Est(Options.DefaultLeafFraction, false);

        var stat = Statistics?.ForKey(keyArg.Trim());
        if (stat is null) return new Est(Options.DefaultLeafFraction, false);
        if (!TryBounds(stat, out double min, out double max)) return new Est(1.0 / Math.Max(1, stat.Ndv), true);

        double span = max - min;
        if (span <= 0) return new Est((lo <= min && hi >= min) ? 1.0 : 0.0, true);

        double l = Math.Max(lo.Value, min);
        double h = Math.Min(hi.Value, max);
        if (h < l) return new Est(0.0, true); // window entirely outside [min,max]
        return new Est(Clamp01((h - l) / span), true);
    }

    private Est InListEst(string keyArg, List<string> valueArgs)
    {
        var stat = Statistics?.ForKey(keyArg.Trim());
        if (stat is null) return new Est(Options.DefaultLeafFraction, false);
        // Each value contributes ≈ one distinct bucket (after pruning out-of-range ones).
        int inRange = 0;
        foreach (var v in valueArgs)
        {
            if (!TryConst(v, out double? num, out string? str)) { inRange++; continue; }
            if (!PrunesToZero(stat, num, str)) inRange++;
        }
        return new Est(Clamp01(inRange * (1.0 / Math.Max(1, stat.Ndv))), true);
    }

    private double RangeFrac(StxTagStats stat, CmpOp op, double c)
    {
        if (!TryBounds(stat, out double min, out double max))
            return Options.DefaultLeafFraction;

        double span = max - min;
        if (span <= 0)
        {
            // Degenerate [min,min]: the single value either passes the bound or it does not.
            bool pass = op switch
            {
                CmpOp.Lt => min < c,
                CmpOp.Le => min <= c,
                CmpOp.Gt => min > c,
                CmpOp.Ge => min >= c,
                _ => true,
            };
            return pass ? 1.0 : 0.0;
        }

        // PHASE C: Use equi-depth histogram if available (design §9).
        if (stat.Histogram is { Count: > 0 })
        {
            return HistogramFrac(stat, op, c, min, max);
        }

        // Linear min/max interpolation (design §7). The covered fraction of the value range.
        double frac = op switch
        {
            CmpOp.Lt or CmpOp.Le => (c - min) / span,
            CmpOp.Gt or CmpOp.Ge => (max - c) / span,
            _ => 1.0,
        };
        return Clamp01(frac);
    }

    /// <summary>
    /// Estimate range selectivity using an equi-depth histogram (design §9). The histogram has
    /// buckets+1 boundaries; we find which buckets the constant falls into and sum their coverage.
    /// </summary>
    private double HistogramFrac(StxTagStats stat, CmpOp op, double c,
        double min, double max)
    {
        // Parse the histogram boundaries.
        var boundaries = new List<double>();
        foreach (var bound in stat.Histogram!)
        {
            if (TryStatNumber(bound, out double b))
                boundaries.Add(b);
        }

        if (boundaries.Count < 2) return Options.DefaultLeafFraction; // not enough boundaries

        // Find the bucket(s) that the constant falls into.
        long count = RecordCount;
        long covered = 0;

        // Each bucket represents ≈ count / (boundaries.Count - 1) rows.
        long bucketSize = Math.Max(1, count / (boundaries.Count - 1));

        for (int j = 0; j < boundaries.Count - 1; j++)
        {
            double lo = boundaries[j];
            double hi = boundaries[j + 1];

            // Check if this bucket is covered by the range query.
            bool bucketCovered = op switch
            {
                CmpOp.Lt => hi < c,
                CmpOp.Le => hi <= c,
                CmpOp.Gt => lo > c,
                CmpOp.Ge => lo >= c,
                _ => true,
            };

            if (bucketCovered)
                covered += bucketSize;
        }

        return Clamp01((double)covered / count);
    }

    /// <summary>True when an equality/inlist constant falls strictly outside the tag's observed [min,max].</summary>
    private static bool PrunesToZero(StxTagStats stat, double? num, string? str)
    {
        if (num is not null && TryBounds(stat, out double min, out double max))
            return num.Value < min || num.Value > max;

        if (str is not null && TryStringBounds(stat, out string smin, out string smax))
            return string.CompareOrdinal(str, smin) < 0 || string.CompareOrdinal(str, smax) > 0;

        return false; // no usable bound → cannot prune (never narrow the result on a missing hint)
    }

    /// <summary>
    /// Parse the tag's Min/Max onto a single comparable numeric scale: a plain number as itself, or an
    /// ISO Date / DateTime bound (the form <see cref="HighlikeStatistics"/> stores for Date / DateTime
    /// keys) as its OLE-Automation day number — the SAME scale <see cref="TryConst"/> maps a
    /// <c>{^…}</c> date literal onto, so equality pruning and range interpolation line up. False when
    /// either bound is absent or neither numeric nor an ISO date.
    /// </summary>
    private static bool TryBounds(StxTagStats stat, out double min, out double max)
    {
        min = max = 0;
        return stat.Min is not null && stat.Max is not null
            && TryStatNumber(stat.Min, out min) && TryStatNumber(stat.Max, out max);
    }

    /// <summary>A stored bound as a number: a plain numeric literal, else an ISO Date / DateTime → OADate.</summary>
    private static bool TryStatNumber(string s, out double v)
    {
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return true;
        return TryParseIsoDate(s, out v);
    }

    /// <summary>The ISO Date / DateTime forms <see cref="HighlikeStatistics"/> writes (and the VFP literal body).</summary>
    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-dd HH:mm",
    };

    /// <summary>Parse an ISO Date / DateTime string onto the OLE-Automation day-number scale (false if not a date).</summary>
    private static bool TryParseIsoDate(string s, out double oa)
    {
        oa = 0;
        if (DateTime.TryParseExact(s.Trim(), DateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out DateTime dt))
        {
            oa = dt.ToOADate();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Parse a VFP date / datetime LITERAL — the curly form <c>{^yyyy-MM-dd}</c> /
    /// <c>{^yyyy-MM-dd HH:mm:ss}</c> — onto the OADate scale (the same scale the stored ISO bounds use).
    /// False for an empty <c>{}</c> / <c>{/  /}</c> literal or any non-date body.
    /// </summary>
    private static bool TryDateLiteral(string s, out double oa)
    {
        oa = 0;
        s = s.Trim();
        if (s.Length < 2 || s[0] != '{' || s[^1] != '}') return false;
        string inner = s.Substring(1, s.Length - 2).Trim();
        if (inner.StartsWith("^", StringComparison.Ordinal)) inner = inner.Substring(1).Trim();
        if (inner.Length == 0) return false; // empty date literal — no usable constant
        return TryParseIsoDate(inner, out oa);
    }

    /// <summary>
    /// The tag's Min/Max as READABLE string bounds — only when present and NOT a <c>0x…</c> hex
    /// collation-weight blob (those are not comparable to a plain literal). False otherwise.
    /// </summary>
    private static bool TryStringBounds(StxTagStats stat, out string min, out string max)
    {
        min = max = string.Empty;
        if (stat.Min is null || stat.Max is null) return false;
        if (stat.Min.StartsWith("0x", StringComparison.Ordinal) || stat.Max.StartsWith("0x", StringComparison.Ordinal))
            return false;
        min = stat.Min; max = stat.Max;
        return true;
    }

    // ============================================================ comparison ops

    private enum CmpOp { Eq, Ne, Lt, Le, Gt, Ge }

    private static CmpOp MapOp(string t) => t switch
    {
        "=" or "==" => CmpOp.Eq,
        "<>" or "!=" or "#" or "=!" => CmpOp.Ne,
        "<" => CmpOp.Lt,
        "<=" or "=<" => CmpOp.Le,
        ">" => CmpOp.Gt,
        ">=" or "=>" => CmpOp.Ge,
        _ => CmpOp.Eq,
    };

    private static CmpOp Flip(CmpOp op) => op switch
    {
        CmpOp.Lt => CmpOp.Gt,
        CmpOp.Le => CmpOp.Ge,
        CmpOp.Gt => CmpOp.Lt,
        CmpOp.Ge => CmpOp.Le,
        _ => op,
    };

    // ============================================================ tiny string parser (paren/quote aware)

    private static double Clamp01(double x) => double.IsNaN(x) ? 0 : (x < 0 ? 0 : (x > 1 ? 1 : x));

    /// <summary>Try to read <paramref name="s"/> as a numeric or single-quoted/double-quoted string constant.</summary>
    private static bool TryConst(string s, out double? num, out string? str)
    {
        num = null; str = null;
        s = s.Trim();
        if (s.Length == 0) return false;

        // VFP date / datetime literal {^yyyy-MM-dd[ HH:mm:ss]} → a numeric constant on the OADate scale
        // (the same scale TryBounds maps the stored ISO Date/DateTime min/max onto).
        if (s[0] == '{' && s[^1] == '}')
        {
            if (TryDateLiteral(s, out double oa)) { num = oa; return true; }
            return false; // a curly literal we cannot parse is not a usable constant
        }

        // String literal: a single fully-enclosing quote pair.
        if ((s[0] == '\'' || s[0] == '"') && s.Length >= 2 && s[^1] == s[0])
        {
            // Ensure the closing quote is the last char and there is no earlier matching close.
            char q = s[0];
            for (int i = 1; i < s.Length - 1; i++) if (s[i] == q) return false;
            str = s.Substring(1, s.Length - 2);
            return true;
        }

        // Numeric (optionally signed).
        if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out double v))
        {
            num = v;
            return true;
        }
        return false;
    }

    /// <summary>Strip one fully-enclosing parenthesis pair (repeatedly) from <paramref name="s"/>.</summary>
    private static string StripOuter(string s)
    {
        s = s.Trim();
        while (s.Length >= 2 && s[0] == '(' && MatchingClose(s, 0) == s.Length - 1)
            s = s.Substring(1, s.Length - 2).Trim();
        return s;
    }

    /// <summary>Index of the ')' matching the '(' at <paramref name="open"/> (quote-aware); −1 if unbalanced.</summary>
    private static int MatchingClose(string s, int open)
    {
        int depth = 0; char q = '\0';
        for (int i = open; i < s.Length; i++)
        {
            char c = s[i];
            if (q != '\0') { if (c == q) q = '\0'; continue; }
            if (c == '\'' || c == '"') { q = c; continue; }
            if (c == '(') depth++;
            else if (c == ')') { if (--depth == 0) return i; }
        }
        return -1;
    }

    /// <summary>Split <paramref name="s"/> on the depth-0, quote-aware, word-boundaried keyword (e.g. AND / OR).</summary>
    private static List<string> SplitTop(string s, string kw)
    {
        var parts = new List<string>();
        int depth = 0; char q = '\0'; int last = 0;
        for (int i = 0; i < s.Length;)
        {
            char c = s[i];
            if (q != '\0') { if (c == q) q = '\0'; i++; continue; }
            if (c == '\'' || c == '"') { q = c; i++; continue; }
            if (c == '(') { depth++; i++; continue; }
            if (c == ')') { depth--; i++; continue; }
            if (depth == 0)
            {
                int m = MatchKeyword(s, i, kw);
                if (m > 0)
                {
                    parts.Add(s.Substring(last, i - last));
                    i += m;
                    last = i;
                    continue;
                }
            }
            i++;
        }
        parts.Add(s.Substring(last));
        return parts;
    }

    /// <summary>A leading NOT operator (word <c>NOT</c> or <c>.NOT.</c>); returns its length else 0.</summary>
    private static int LeadingNot(string s)
    {
        s = s.TrimStart();
        // Recompute against the original (caller passes an already-trimmed string).
        return MatchKeyword(s, 0, "NOT");
    }

    /// <summary>
    /// Match the boolean keyword <paramref name="kw"/> at position <paramref name="i"/> — either the
    /// bare word form (case-insensitive, with non-alphanumeric boundaries) or the dotted <c>.KW.</c>
    /// form. Returns the matched length (so the caller can skip it) or 0.
    /// </summary>
    private static int MatchKeyword(string s, int i, string kw)
    {
        // Dotted form: .KW.
        if (i + kw.Length + 2 <= s.Length && s[i] == '.'
            && string.Compare(s, i + 1, kw, 0, kw.Length, StringComparison.OrdinalIgnoreCase) == 0
            && s[i + 1 + kw.Length] == '.')
            return kw.Length + 2;

        // Word form: boundaries on both sides.
        if (i + kw.Length <= s.Length
            && string.Compare(s, i, kw, 0, kw.Length, StringComparison.OrdinalIgnoreCase) == 0)
        {
            bool leftOk = i == 0 || !IsWordChar(s[i - 1]);
            int after = i + kw.Length;
            bool rightOk = after >= s.Length || !IsWordChar(s[after]);
            if (leftOk && rightOk) return kw.Length;
        }
        return 0;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>The index of the first depth-0 comparison operator run (quote-aware); −1 if none.</summary>
    private static int FindTopComparison(string s, out int opLen)
    {
        opLen = 0;
        int depth = 0; char q = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (q != '\0') { if (c == q) q = '\0'; continue; }
            if (c == '\'' || c == '"') { q = c; continue; }
            if (c == '(') { depth++; continue; }
            if (c == ')') { depth--; continue; }
            if (depth == 0 && IsCmpChar(c))
            {
                int j = i;
                while (j < s.Length && IsCmpChar(s[j])) j++;
                opLen = j - i;
                return i;
            }
        }
        return -1;
    }

    private static bool IsCmpChar(char c) => c is '<' or '>' or '=' or '!' or '#';

    /// <summary>True when <paramref name="s"/> is exactly <c>IDENT(...)</c> with the open paren matching the last char.</summary>
    private static bool TryFunction(string s, out string fname, out List<string> args)
    {
        fname = string.Empty; args = new List<string>();
        s = s.Trim();
        int p = 0;
        while (p < s.Length && IsWordChar(s[p])) p++;
        if (p == 0 || p >= s.Length || s[p] != '(') return false;
        if (MatchingClose(s, p) != s.Length - 1) return false;

        fname = s.Substring(0, p).ToUpperInvariant();
        string inner = s.Substring(p + 1, s.Length - p - 2);
        args = SplitArgs(inner);
        return true;
    }

    /// <summary>Split a function argument list on depth-0, quote-aware commas.</summary>
    private static List<string> SplitArgs(string inner)
    {
        var res = new List<string>();
        int depth = 0; char q = '\0'; int last = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (q != '\0') { if (c == q) q = '\0'; continue; }
            if (c == '\'' || c == '"') { q = c; continue; }
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (depth == 0 && c == ',') { res.Add(inner.Substring(last, i - last).Trim()); last = i + 1; }
        }
        if (inner.Length > 0 || res.Count > 0) res.Add(inner.Substring(last).Trim());
        return res;
    }

    /// <summary>True for the MACHINE / identity collation (the only one whose stored key bytes are the original characters).</summary>
    private static bool IsIdentityCollation(string? collation)
        => string.IsNullOrEmpty(collation)
           || string.Equals(collation, "MACHINE", StringComparison.OrdinalIgnoreCase);
}
