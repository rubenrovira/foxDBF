using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.MicroVfp;

/// <summary>
/// The microVFP PRG (stored-procedure) parser: turns VFP <c>.prg</c> source into a
/// <see cref="PrgProgram"/> (procedures/functions + top-level statements). It owns only the
/// LINE/STATEMENT structure — every expression fragment is delegated to the existing
/// <c>CrossVault.FoxDbf.Expressions</c> engine via <see cref="PrgExpr"/>, and embedded
/// <c>SELECT … FROM</c> / <c>INSERT INTO</c> are routed to <c>CrossVault.FoxDbf.Sql</c>.
/// </summary>
public static class PrgParser
{
    /// <summary>Parses <paramref name="source"/> (a whole .prg) into a <see cref="PrgProgram"/>.</summary>
    public static PrgProgram Parse(string source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        var lines = BuildLogicalLines(source);
        return new Impl(lines).ParseProgram();
    }

    /// <summary>Reads <paramref name="path"/> (Windows-1252 / Latin-1, as VFP exports it) and
    /// parses it.</summary>
    public static PrgProgram ParseFile(string path)
        => Parse(File.ReadAllText(path, Encoding.Latin1));

    // ── logical-line model ───────────────────────────────────────────────────

    internal readonly record struct LogicalLine(int LineNo, string Text);

    /// <summary>
    /// Joins <c>;</c>-continuations and drops blank/comment lines, yielding the logical lines
    /// the parser consumes. Implements the VFP gotcha that a <c>;</c> on a COMMENT line swallows
    /// the next physical line into the comment (the joined line is still a comment → dropped).
    /// </summary>
    internal static List<LogicalLine> BuildLogicalLines(string source)
    {
        var phys = source.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var result = new List<LogicalLine>();
        int i = 0;
        while (i < phys.Length)
        {
            int startLine = i + 1;
            bool isComment = PrgScan.IsCommentLine(phys[i].TrimStart());
            var sb = new StringBuilder();
            while (true)
            {
                string line = phys[i];
                i++;
                bool continues;
                if (isComment)
                {
                    // The whole logical line is a comment: never accumulate, just chase the
                    // trailing ';' so a comment-continuation swallows the following line too.
                    continues = line.TrimEnd().EndsWith(';');
                }
                else
                {
                    var (code, cont) = PrgScan.StripCommentAndContinuation(line);
                    continues = cont;
                    if (code.Trim().Length > 0)
                    {
                        if (sb.Length > 0) sb.Append(' ');
                        sb.Append(code.Trim());
                    }
                }
                if (!continues || i >= phys.Length) break;
            }
            if (!isComment)
            {
                string text = sb.ToString().Trim();
                if (text.Length > 0) result.Add(new LogicalLine(startLine, text));
            }
        }
        return result;
    }

    // ── the recursive-descent parser ───────────────────────────────────────────

    private sealed class Impl
    {
        private readonly List<LogicalLine> _lines;
        private int _i;

        public Impl(List<LogicalLine> lines) => _lines = lines;

        private bool Eof => _i >= _lines.Count;
        private LogicalLine Peek => _lines[_i];
        private LogicalLine Next() => _lines[_i++];
        private string PeekWord => Eof ? string.Empty : PrgScan.FirstWord(Peek.Text);

        private static readonly HashSet<string> Terminators = new(StringComparer.Ordinal)
        {
            "ENDIF", "ELSE", "ENDDO", "ENDFOR", "NEXT", "ENDSCAN",
            "ENDCASE", "OTHERWISE", "CASE", "ENDPROC", "ENDFUNC",
        };

        // ---- top level ----

        public PrgProgram ParseProgram()
        {
            var procs = new List<ProcDef>();
            var main = new List<PrgStatement>();
            while (!Eof)
            {
                string kw = PeekWord;
                if (kw is "PROCEDURE" or "FUNCTION")
                    procs.Add(ParseProc());
                else if (Terminators.Contains(kw))
                    throw new MicroVfpSyntaxException($"Unexpected '{kw}' outside a block.", Peek.LineNo);
                else
                    main.Add(ParseStatement());
            }
            return new PrgProgram(procs, main);
        }

        private ProcDef ParseProc()
        {
            var ll = Next();
            var kind = PrgScan.FirstWord(ll.Text) == "FUNCTION" ? ProcKind.Function : ProcKind.Procedure;
            string rest = PrgScan.AfterFirstWord(ll.Text).TrimStart();

            int j = 0;
            while (j < rest.Length && (char.IsAsciiLetterOrDigit(rest[j]) || rest[j] == '_')) j++;
            string name = rest.Substring(0, j);
            if (name.Length == 0)
                throw new MicroVfpSyntaxException("PROCEDURE/FUNCTION without a name.", ll.LineNo);

            var parameters = new List<string>();
            string after = rest.Substring(j).TrimStart();
            if (after.StartsWith('('))
            {
                int close = MatchParen(after, 0);
                string inner = after.Substring(1, close - 1);
                foreach (var p in PrgScan.SplitTopCommas(inner))
                {
                    var id = LeadingIdent(p);
                    if (id.Length > 0) parameters.Add(id);
                }
            }

            var body = ParseBlock("ENDPROC", "ENDFUNC");
            if (!Eof && PeekWord is "ENDPROC" or "ENDFUNC") Next();   // explicit close (else implicit)
            return new ProcDef(name, kind, parameters, body) { Line = ll.LineNo };
        }

        /// <summary>Collects statements until a <paramref name="stop"/> keyword, a procedure
        /// boundary, or EOF — WITHOUT consuming the stopper (the block owner consumes it).</summary>
        private List<PrgStatement> ParseBlock(params string[] stop)
        {
            var stmts = new List<PrgStatement>();
            while (!Eof)
            {
                string kw = PeekWord;
                if (stop.Contains(kw)) return stmts;
                // A procedure boundary (or its explicit close) always ends a block: a missing
                // ENDIF/ENDDO/… then surfaces at the block OWNER with the opener's line number.
                if (kw is "PROCEDURE" or "FUNCTION" or "ENDPROC" or "ENDFUNC") return stmts;
                stmts.Add(ParseStatement());
            }
            return stmts;
        }

        // ---- statement dispatch (handles block-statements; delegates the rest) ----

        private PrgStatement ParseStatement()
        {
            // Cap block nesting so deeply-nested IF/FOR/SCAN/DO sources throw a catchable syntax error
            // rather than a fatal StackOverflowException.
            if (++_blockDepth > MaxBlockDepth)
            {
                _blockDepth--;
                throw new MicroVfpSyntaxException("Block nesting is too deep.", Peek.LineNo);
            }
            try { return ParseStatementCore(); }
            finally { _blockDepth--; }
        }

        private const int MaxBlockDepth = 400;
        private int _blockDepth;

        private PrgStatement ParseStatementCore()
        {
            var ll = Peek;
            string kw = PrgScan.FirstWord(ll.Text);
            switch (kw)
            {
                case "IF": return ParseIf();
                case "FOR": return ParseFor();
                case "SCAN": return ParseScan();
                case "DO":
                    string sw = PrgScan.SecondWord(ll.Text);
                    if (sw == "CASE") return ParseDoCase();
                    if (sw == "WHILE") return ParseDoWhile();
                    Next();
                    return BuildSimple(ll.Text, ll.LineNo);
                default:
                    if (Terminators.Contains(kw))
                        throw new MicroVfpSyntaxException($"Unexpected '{kw}' (no matching block opener).", ll.LineNo);
                    Next();
                    return BuildSimple(ll.Text, ll.LineNo);
            }
        }

        // ---- block statements ----

        private IfStmt ParseIf()
        {
            var ll = Next();
            string cond = PrgScan.AfterFirstWord(ll.Text);
            int thenPos = PrgScan.IndexOfKeyword(cond, "THEN");
            if (thenPos >= 0 && cond.Substring(thenPos).Trim().Equals("THEN", StringComparison.OrdinalIgnoreCase))
                cond = cond.Substring(0, thenPos);

            var thenBlock = ParseBlock("ELSE", "ENDIF");
            IReadOnlyList<PrgStatement> elseBlock = Array.Empty<PrgStatement>();
            if (!Eof && PeekWord == "ELSE")
            {
                Next();
                elseBlock = ParseBlock("ENDIF");
            }
            Require("ENDIF", ll.LineNo);
            return new IfStmt(PrgExpr.Parse(cond), thenBlock, elseBlock) { Line = ll.LineNo };
        }

        private DoCaseStmt ParseDoCase()
        {
            var ll = Next(); // DO CASE
            var cases = new List<CaseClause>();
            IReadOnlyList<PrgStatement>? otherwise = null;
            while (true)
            {
                if (Eof) throw new MicroVfpSyntaxException("Missing ENDCASE.", ll.LineNo);
                string kw = PeekWord;
                if (kw == "CASE")
                {
                    var cl = Next();
                    var body = ParseBlock("CASE", "OTHERWISE", "ENDCASE");
                    cases.Add(new CaseClause(PrgExpr.Parse(PrgScan.AfterFirstWord(cl.Text)), body));
                }
                else if (kw == "OTHERWISE")
                {
                    Next();
                    otherwise = ParseBlock("CASE", "OTHERWISE", "ENDCASE");
                }
                else if (kw == "ENDCASE")
                {
                    Next();
                    break;
                }
                else
                {
                    throw new MicroVfpSyntaxException($"Expected CASE/OTHERWISE/ENDCASE but found '{kw}'.", Peek.LineNo);
                }
            }
            return new DoCaseStmt(cases, otherwise) { Line = ll.LineNo };
        }

        private DoWhileStmt ParseDoWhile()
        {
            var ll = Next();
            string cond = PrgScan.AfterFirstWord(PrgScan.AfterFirstWord(ll.Text)); // drop DO, then WHILE
            var body = ParseBlock("ENDDO");
            Require("ENDDO", ll.LineNo);
            return new DoWhileStmt(PrgExpr.Parse(cond), body) { Line = ll.LineNo };
        }

        private ForStmt ParseFor()
        {
            var ll = Next();
            string rest = PrgScan.AfterFirstWord(ll.Text);
            int eq = PrgScan.IndexOfAssign(rest);
            if (eq < 0) throw new MicroVfpSyntaxException("Malformed FOR (missing '=').", ll.LineNo);
            string variable = rest.Substring(0, eq).Trim();
            string bounds = rest.Substring(eq + 1);
            int toPos = PrgScan.IndexOfKeyword(bounds, "TO");
            if (toPos < 0) throw new MicroVfpSyntaxException("Malformed FOR (missing TO).", ll.LineNo);
            string fromText = bounds.Substring(0, toPos);
            string afterTo = bounds.Substring(toPos + 2);
            int stepPos = PrgScan.IndexOfKeyword(afterTo, "STEP");
            string toText = stepPos < 0 ? afterTo : afterTo.Substring(0, stepPos);
            PrgExpr? step = stepPos < 0 ? null : PrgExpr.Parse(afterTo.Substring(stepPos + 4));

            var body = ParseBlock("ENDFOR", "NEXT");
            if (!Eof && PeekWord is "ENDFOR" or "NEXT") Next();
            else throw new MicroVfpSyntaxException("Missing ENDFOR/NEXT.", ll.LineNo);

            return new ForStmt(variable, PrgExpr.Parse(fromText), PrgExpr.Parse(toText), step, body) { Line = ll.LineNo };
        }

        private ScanStmt ParseScan()
        {
            var ll = Next();
            var (scope, forE, whileE) = ScopeForWhile(PrgScan.AfterFirstWord(ll.Text));
            var body = ParseBlock("ENDSCAN");
            Require("ENDSCAN", ll.LineNo);
            return new ScanStmt(scope, forE, whileE, body) { Line = ll.LineNo };
        }

        private void Require(string keyword, int openLine)
        {
            if (Eof || PeekWord != keyword)
                throw new MicroVfpSyntaxException($"Missing {keyword}.", openLine);
            Next();
        }

        // ---- simple (single-line) statements ----

        private PrgStatement BuildSimple(string text, int line)
        {
            string kw = PrgScan.FirstWord(text);
            PrgStatement node = kw switch
            {
                "LOCAL" => BuildVarDecl(DeclScope.Local, text),
                "PRIVATE" => BuildVarDecl(DeclScope.Private, text),
                "PUBLIC" => BuildVarDecl(DeclScope.Public, text),
                "RELEASE" => BuildRelease(text),
                "DIMENSION" or "REDIMENSION" => BuildDimension(text),
                "PARAMETERS" => new ParametersStmt(false, IdentList(PrgScan.AfterFirstWord(text))),
                "LPARAMETERS" => new ParametersStmt(true, IdentList(PrgScan.AfterFirstWord(text))),
                "STORE" => BuildStore(text),
                "RETURN" => BuildReturn(text),
                "EXIT" => new ExitStmt(),
                "LOOP" => new LoopStmt(),
                "DO" => BuildDoCall(text),
                "SELECT" => BuildSelect(text),
                "USE" => BuildUse(text),
                "SEEK" => BuildSeek(text),
                "INDEX" => BuildIndex(text),
                "REINDEX" => BuildReindex(text),
                "LOCATE" => BuildLocate(text),
                "CONTINUE" => new ContinueStmt(),
                "GO" or "GOTO" => BuildGo(text),
                "SKIP" => BuildSkip(text),
                "SET" => BuildSet(text, line),
                "ON" => BuildOn(text, line),
                "REPLACE" => BuildReplace(text),
                "DELETE" => BuildDelete(text),
                "RECALL" => BuildRecall(text),
                "INSERT" => new InsertStmt(text.Trim(), TrySql(text)),
                "SUM" => BuildSum(text),
                "UNLOCK" => BuildUnlock(text),
                "ROLLBACK" => new RollbackStmt(),
                "BEGIN" => PrgScan.SecondWord(text) == "TRANSACTION"
                    ? new BeginTxnStmt()
                    : new UnknownCommand(kw, PrgScan.AfterFirstWord(text)),
                "END" => PrgScan.SecondWord(text) == "TRANSACTION"
                    ? new EndTxnStmt()
                    : new UnknownCommand(kw, PrgScan.AfterFirstWord(text)),
                "=" => new ExprStatement(PrgExpr.Parse(text.TrimStart().Substring(1))),
                "&" => new MacroSubstStmt(new MacroSubst(text.Trim())),
                "#" => new DirectiveStmt(text.Trim()),
                _ => BuildDefault(text, kw),
            };
            return node with { Line = line };
        }

        private static PrgStatement BuildDefault(string text, string kw)
        {
            if (PrgScan.IndexOfAssign(text) >= 0)
            {
                int eq = PrgScan.IndexOfAssign(text);
                string target = text.Substring(0, eq).Trim();
                return new Assignment(target, PrgExpr.Parse(text.Substring(eq + 1)));
            }
            return new UnknownCommand(kw, PrgScan.AfterFirstWord(text));
        }

        private static VarDecl BuildVarDecl(DeclScope scope, string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var (all, like, except, names, dims) = ParseDeclBody(rest);
            return new VarDecl(scope, names, all, like, except, dims);
        }

        private static ReleaseStmt BuildRelease(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var (all, like, except, names, _) = ParseDeclBody(rest);
            return new ReleaseStmt(names, all, like, except);
        }

        private static DimensionStmt BuildDimension(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var (_, _, _, names, dims) = ParseDeclBody(rest);
            return new DimensionStmt(names, dims);
        }

        private static (bool All, string? Like, string? Except, IReadOnlyList<string> Names, IReadOnlyList<string?> Dimensions) ParseDeclBody(string rest)
        {
            if (PrgScan.FirstWord(rest) == "ALL")
            {
                string tail = PrgScan.AfterFirstWord(rest);
                string? like = null, except = null;
                int likePos = PrgScan.IndexOfKeyword(tail, "LIKE");
                int exceptPos = PrgScan.IndexOfKeyword(tail, "EXCEPT");
                if (likePos >= 0) like = PrgScan.AfterFirstWord(tail.Substring(likePos)).Trim();
                if (exceptPos >= 0) except = PrgScan.AfterFirstWord(tail.Substring(exceptPos)).Trim();
                return (true, like, except, Array.Empty<string>(), Array.Empty<string?>());
            }
            var names = new List<string>();
            var dims = new List<string?>();
            foreach (var piece in PrgScan.SplitTopCommas(rest))
            {
                var (name, dim) = SplitNameAndDim(piece);
                if (name.Length == 0) continue;
                names.Add(name);
                dims.Add(dim);
            }
            return (false, null, null, names, dims);
        }

        /// <summary>Splits a single declaration entry into its identifier and the captured
        /// array-dimension bracket text (incl. the brackets), e.g. <c>"gaErrors(1,12)"</c> →
        /// <c>("gaErrors", "(1,12)")</c>; a scalar yields <c>(name, null)</c>.</summary>
        private static (string Name, string? Dim) SplitNameAndDim(string piece)
        {
            string p = piece.TrimStart();
            string name = LeadingIdent(p);
            if (name.Length == 0) return (string.Empty, null);
            string after = p.Substring(name.Length).TrimStart();
            if (after.Length > 0 && (after[0] == '(' || after[0] == '['))
            {
                int close = MatchBracket(after, 0);
                if (close >= 0) return (name, after.Substring(0, close + 1));
            }
            return (name, null);
        }

        private static StoreStmt BuildStore(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            int toPos = PrgScan.IndexOfKeyword(rest, "TO");
            if (toPos < 0)
                return new StoreStmt(PrgExpr.Parse(rest), Array.Empty<string>());
            string value = rest.Substring(0, toPos);
            var targets = IdentList(rest.Substring(toPos + 2));
            return new StoreStmt(PrgExpr.Parse(value), targets);
        }

        private static ReturnStmt BuildReturn(string text)
        {
            string rest = PrgScan.AfterFirstWord(text).Trim();
            return new ReturnStmt(rest.Length == 0 ? null : PrgExpr.Parse(rest));
        }

        private static DoCall BuildDoCall(string text)
        {
            string rest = PrgScan.AfterFirstWord(text).TrimStart();
            string name = LeadingIdent(rest);
            string remainder = rest.Substring(name.Length);
            var (_, segs) = Carve(remainder, "WITH", "IN");
            var args = new List<PrgExpr>();
            NameRef? inRef = null;
            foreach (var (k, body) in segs)
            {
                if (k == "WITH")
                    args.AddRange(PrgScan.SplitTopCommas(body).Where(p => p.Length > 0).Select(PrgExpr.Parse));
                else if (k == "IN")
                    inRef = ToNameRef(FirstToken(body));
            }
            return new DoCall(name, args, inRef);
        }

        private static PrgStatement BuildSelect(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            if (PrgScan.IndexOfKeyword(rest, "FROM") >= 0)
                return new SqlSelectStmt(text.Trim(), TrySql(text));
            return new SelectAreaStmt(ToNameRef(FirstToken(rest)));
        }

        private static UseStmt BuildUse(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var toks = PrgScan.ClauseTokens(rest);
            string? database = null;
            NameRef? table = null, alias = null, order = null, inArea = null;
            bool again = false, noUpdate = false;
            var mode = UseMode.Default;

            int idx = 0;
            if (toks.Count > 0 && !IsUseKeyword(toks[0]))
            {
                string t = toks[0];
                idx = 1;
                if (!t.StartsWith('(') && t.Contains('!'))
                {
                    int bang = t.IndexOf('!');
                    database = t.Substring(0, bang);
                    table = NameRef.OfName(t.Substring(bang + 1));
                }
                else
                {
                    table = ToNameRef(t);
                }
            }
            while (idx < toks.Count)
            {
                switch (toks[idx].ToUpperInvariant())
                {
                    case "IN": inArea = ToNameRef(NextTok(toks, ref idx)); break;
                    case "AGAIN": again = true; idx++; break;
                    case "ALIAS": alias = ToNameRef(NextTok(toks, ref idx)); break;
                    case "ORDER": order = ToNameRef(NextTok(toks, ref idx)); break;
                    case "EXCLUSIVE": mode = UseMode.Exclusive; idx++; break;
                    case "SHARED":
                    case "SHARE": mode = UseMode.Shared; idx++; break;
                    case "NOUPDATE": noUpdate = true; idx++; break;
                    case "INDEX": NextTok(toks, ref idx); break; // skip index list
                    default: idx++; break;
                }
            }
            return new UseStmt(database, table, again, alias, order, inArea, mode, noUpdate, table is null);
        }

        private static SeekStmt BuildSeek(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var (key, segs) = Carve(rest, "ORDER", "IN");
            NameRef? order = null, inArea = null;
            foreach (var (k, body) in segs)
            {
                if (k == "ORDER") order = ToNameRef(FirstToken(body));
                else if (k == "IN") inArea = ToNameRef(FirstToken(body));
            }
            return new SeekStmt(PrgExpr.Parse(key), order, inArea);
        }

        /// <summary>The bare flag words of <c>INDEX ON</c> (they carry no operand, so they are stripped
        /// out before the key/TAG/OF/TO/FOR clauses are carved).</summary>
        private static readonly string[] IndexFlagWords =
            { "ASCENDING", "DESCENDING", "UNIQUE", "CANDIDATE", "ADDITIVE", "COMPACT" };

        private static PrgStatement BuildIndex(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);                          // drop INDEX
            if (PrgScan.FirstWord(rest) == "ON") rest = PrgScan.AfterFirstWord(rest); // drop ON

            // Blank out the bare flag words in place (preserving offsets) so the key expression and the
            // TAG/OF/TO/FOR clause bodies carve cleanly and never swallow a trailing DESCENDING/UNIQUE/….
            bool descending = false, unique = false, candidate = false, additive = false;
            var chars = rest.ToCharArray();
            foreach (var (start, len, upper) in PrgScan.TopWords(rest))
            {
                if (Array.IndexOf(IndexFlagWords, upper) < 0) continue;
                switch (upper)
                {
                    case "DESCENDING": descending = true; break;
                    case "UNIQUE": unique = true; break;
                    case "CANDIDATE": candidate = true; break;
                    case "ADDITIVE": additive = true; break;
                    // ASCENDING / COMPACT are the defaults — recognised so they are stripped, no flag set.
                }
                for (int i = start; i < start + len; i++) chars[i] = ' ';
            }
            string cleaned = new string(chars);

            var (key, segs) = Carve(cleaned, "TAG", "OF", "TO", "FOR");
            NameRef? tag = null, ofCdx = null, toIdx = null;
            PrgExpr? forE = null;
            foreach (var (k, body) in segs)
            {
                switch (k)
                {
                    case "TAG": tag = ToNameRef(FirstToken(body)); break;
                    case "OF": ofCdx = ToNameRef(FirstToken(body)); break;
                    case "TO": toIdx = ToNameRef(FirstToken(body)); break;
                    case "FOR": if (body.Trim().Length > 0) forE = PrgExpr.Parse(body); break;
                }
            }

            string keyText = key.Trim();
            if (keyText.Length == 0)
                return new UnknownCommand("INDEX", PrgScan.AfterFirstWord(text));
            return new IndexStmt(PrgExpr.Parse(keyText), tag, ofCdx, toIdx, forE,
                descending, unique, candidate, additive);
        }

        private static ReindexStmt BuildReindex(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var (_, segs) = Carve(rest, "IN");
            NameRef? inArea = segs.Count > 0 ? ToNameRef(FirstToken(segs[0].Body)) : null;
            return new ReindexStmt(inArea);
        }

        private static LocateStmt BuildLocate(string text)
        {
            var (scope, forE, whileE) = ScopeForWhile(PrgScan.AfterFirstWord(text));
            return new LocateStmt(scope, forE, whileE);
        }

        private static GoStmt BuildGo(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var (target, segs) = Carve(rest, "IN");
            NameRef? inArea = segs.Count > 0 ? ToNameRef(FirstToken(segs[0].Body)) : null;
            target = target.Trim();
            if (PrgScan.FirstWord(target) == "RECORD") target = PrgScan.AfterFirstWord(target).Trim();
            string up = target.ToUpperInvariant();
            if (up is "TOP" or "BOTTOM")
                return new GoStmt(up, null, inArea);
            return new GoStmt(null, PrgExpr.Parse(target), inArea);
        }

        private static SkipStmt BuildSkip(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var (count, segs) = Carve(rest, "IN");
            NameRef? inArea = segs.Count > 0 ? ToNameRef(FirstToken(segs[0].Body)) : null;
            count = count.Trim();
            return new SkipStmt(count.Length == 0 ? null : PrgExpr.Parse(count), inArea);
        }

        private static PrgStatement BuildSet(string text, int line)
        {
            string rest = PrgScan.AfterFirstWord(text).TrimStart();
            string setting = LeadingIdent(rest).ToUpperInvariant();
            string args = rest.Substring(setting.Length).Trim();
            if (setting == "ORDER")
                return BuildSetOrder(args);
            return new SetStmt(setting, args);
        }

        private static SetOrderStmt BuildSetOrder(string args)
        {
            if (PrgScan.FirstWord(args) == "TO") args = PrgScan.AfterFirstWord(args);
            var (head, segs) = Carve(args, "IN", "DESCENDING", "ASCENDING", "TAG");
            NameRef? order = head.Trim().Length == 0 ? null : ToNameRef(FirstToken(head));
            NameRef? inArea = null;
            bool? direction = null;   // null = no explicit clause; true = DESCENDING; false = ASCENDING.
            foreach (var (k, body) in segs)
            {
                if (k == "IN") inArea = ToNameRef(FirstToken(body));
                else if (k == "DESCENDING") direction = true;
                else if (k == "ASCENDING") direction = false;
                else if (k == "TAG" && order is null) order = ToNameRef(FirstToken(body));
            }
            return new SetOrderStmt(order, inArea, direction);
        }

        private PrgStatement BuildOn(string text, int line)
        {
            if (PrgScan.SecondWord(text) != "ERROR")
                return new UnknownCommand("ON", PrgScan.AfterFirstWord(text));
            string command = PrgScan.AfterFirstWord(PrgScan.AfterFirstWord(text)).Trim(); // drop ON, ERROR
            if (command.Length == 0)
                return new OnErrorStmt(OnErrorKind.Clear, null, null);
            if (command.StartsWith('&'))
                return new OnErrorStmt(OnErrorKind.Macro, null, new MacroSubst(command));
            return new OnErrorStmt(OnErrorKind.Command, BuildSimple(command, line), null, command);
        }

        private static ReplaceStmt BuildReplace(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var (body, segs) = Carve(rest, "FOR", "WHILE", "IN");
            // A leading scope (ALL / REST / NEXT n / RECORD n) precedes the first field-WITH pair;
            // peel it off so ToNameRef never builds a malformed field like "ALL setup.value".
            var (scope, fieldList) = CarveLeadingScope(body);
            var clauses = new List<ReplaceClause>();
            foreach (var piece in PrgScan.SplitTopCommas(fieldList))
            {
                if (piece.Trim().Length == 0) continue;
                int withPos = PrgScan.IndexOfKeyword(piece, "WITH");
                if (withPos < 0) continue;
                var field = ToNameRef(piece.Substring(0, withPos).Trim());
                string valText = piece.Substring(withPos + 4).Trim();
                bool additive = false;
                int addPos = PrgScan.IndexOfKeyword(valText, "ADDITIVE");
                if (addPos >= 0 && valText.Substring(addPos).Trim().Equals("ADDITIVE", StringComparison.OrdinalIgnoreCase))
                {
                    additive = true;
                    valText = valText.Substring(0, addPos).Trim();
                }
                clauses.Add(new ReplaceClause(field, PrgExpr.Parse(valText), additive));
            }
            PrgExpr? forE = null, whileE = null;
            NameRef? inArea = null;
            foreach (var (k, b) in segs)
            {
                if (k == "FOR") forE = PrgExpr.Parse(b);
                else if (k == "WHILE") whileE = PrgExpr.Parse(b);
                else if (k == "IN") inArea = ToNameRef(FirstToken(b));
            }
            return new ReplaceStmt(clauses, scope, forE, whileE, inArea);
        }

        /// <summary>Peels a leading record scope (<c>ALL</c> / <c>REST</c> / <c>NEXT n</c> /
        /// <c>RECORD n</c>) off the front of a command body, returning the scope text (or null) and
        /// the remainder. Leaves a bare field/clause body untouched.</summary>
        private static (string? Scope, string Remainder) CarveLeadingScope(string s)
        {
            s = s.TrimStart();
            string w = PrgScan.FirstWord(s);
            if (w is "ALL" or "REST")
                return (w, PrgScan.AfterFirstWord(s));
            if (w is "NEXT" or "RECORD")
            {
                string after = PrgScan.AfterFirstWord(s);
                int k = 0;
                while (k < after.Length && !char.IsWhiteSpace(after[k])) k++;
                string num = after.Substring(0, k);
                string remainder = after.Substring(k).TrimStart();
                return ($"{w} {num}".Trim(), remainder);
            }
            return (null, s);
        }

        private static DeleteStmt BuildDelete(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var (head, segs) = Carve(rest, "FOR", "WHILE", "IN");
            string? scope = head.Trim().Length == 0 ? null : head.Trim();
            PrgExpr? forE = null;
            NameRef? inArea = null;
            foreach (var (k, b) in segs)
            {
                if (k == "FOR") forE = PrgExpr.Parse(b);
                else if (k == "IN") inArea = ToNameRef(FirstToken(b));
            }
            return new DeleteStmt(scope, forE, inArea);
        }

        private static RecallStmt BuildRecall(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var (head, segs) = Carve(rest, "FOR", "WHILE");
            string? scope = head.Trim().Length == 0 ? null : head.Trim();
            PrgExpr? forE = segs.FirstOrDefault(s => s.Kw == "FOR").Body is { Length: > 0 } b ? PrgExpr.Parse(b) : null;
            return new RecallStmt(scope, forE);
        }

        private static SumStmt BuildSum(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var (head, segs) = Carve(rest, "FOR", "WHILE", "TO");
            var exprs = PrgScan.SplitTopCommas(head).Where(p => p.Length > 0).Select(PrgExpr.Parse).ToList();
            PrgExpr? forE = null, whileE = null;
            var targets = new List<string>();
            foreach (var (k, b) in segs)
            {
                if (k == "FOR") forE = PrgExpr.Parse(b);
                else if (k == "WHILE") whileE = PrgExpr.Parse(b);
                else if (k == "TO") targets.AddRange(IdentList(b));
            }
            return new SumStmt(exprs, null, forE, whileE, targets);
        }

        private static UnlockStmt BuildUnlock(string text)
        {
            string rest = PrgScan.AfterFirstWord(text);
            var toks = PrgScan.ClauseTokens(rest);
            bool all = false;
            NameRef? record = null, inArea = null;
            for (int idx = 0; idx < toks.Count;)
            {
                switch (toks[idx].ToUpperInvariant())
                {
                    case "ALL": all = true; idx++; break;
                    case "RECORD": record = ToNameRef(NextTok(toks, ref idx)); break;
                    case "IN": inArea = ToNameRef(NextTok(toks, ref idx)); break;
                    default: idx++; break;
                }
            }
            return new UnlockStmt(all, record, inArea);
        }

        // ---- shared little helpers ----

        private static (string? Scope, PrgExpr? For, PrgExpr? While) ScopeForWhile(string rest)
        {
            var (head, segs) = Carve(rest, "FOR", "WHILE");
            string? scope = head.Trim().Length == 0 ? null : head.Trim();
            PrgExpr? forE = null, whileE = null;
            foreach (var (k, b) in segs)
            {
                if (k == "FOR") forE = PrgExpr.Parse(b);
                else if (k == "WHILE") whileE = PrgExpr.Parse(b);
            }
            return (scope, forE, whileE);
        }

        /// <summary>Splits <paramref name="s"/> at top-level occurrences of the given keywords:
        /// returns the text before the first keyword plus, per keyword, the text up to the next
        /// keyword.</summary>
        private static (string Head, List<(string Kw, string Body)> Segments) Carve(string s, params string[] kws)
        {
            var set = new HashSet<string>(kws, StringComparer.Ordinal);
            var marks = new List<(int Pos, int Len, string Kw)>();
            foreach (var (start, len, upper) in PrgScan.TopWords(s))
                if (set.Contains(upper)) marks.Add((start, len, upper));

            string head = marks.Count == 0 ? s : s.Substring(0, marks[0].Pos);
            var segs = new List<(string, string)>();
            for (int m = 0; m < marks.Count; m++)
            {
                int bodyStart = marks[m].Pos + marks[m].Len;
                int bodyEnd = m + 1 < marks.Count ? marks[m + 1].Pos : s.Length;
                segs.Add((marks[m].Kw, s.Substring(bodyStart, bodyEnd - bodyStart).Trim()));
            }
            return (head.Trim(), segs);
        }

        private static SqlStatement? TrySql(string sql)
        {
            try { return SqlParser.Parse(sql.Trim()); }
            catch (Exception) { return null; }
        }

        private static IReadOnlyList<string> IdentList(string s)
            => PrgScan.SplitTopCommas(s).Select(LeadingIdent).Where(n => n.Length > 0).ToList();

        private static string LeadingIdent(string s)
        {
            s = s.TrimStart();
            int j = 0;
            while (j < s.Length && (char.IsAsciiLetterOrDigit(s[j]) || s[j] == '_')) j++;
            return s.Substring(0, j);
        }

        private static string FirstToken(string s)
        {
            var toks = PrgScan.ClauseTokens(s);
            return toks.Count > 0 ? toks[0] : string.Empty;
        }

        private static string NextTok(List<string> toks, ref int idx)
        {
            idx++; // consume the keyword
            return idx < toks.Count ? toks[idx++] : string.Empty;
        }

        private static NameRef ToNameRef(string token)
        {
            string t = token.Trim();
            if (t.Length == 0) return NameRef.OfName(string.Empty);
            if (t.StartsWith('(') && t.EndsWith(')'))
                return NameRef.OfExpr(new NameExpr(PrgExpr.Parse(t.Substring(1, t.Length - 2))));
            if ((t[0] == '\'' || t[0] == '"') && t.Length >= 2 && t[^1] == t[0])
                return NameRef.OfName(t.Substring(1, t.Length - 2));
            return NameRef.OfName(t);
        }

        private static bool IsUseKeyword(string t) => t.ToUpperInvariant() switch
        {
            "IN" or "AGAIN" or "ALIAS" or "ORDER" or "EXCLUSIVE" or "SHARED"
                or "SHARE" or "NOUPDATE" or "INDEX" or "NODATA" or "ONLINE" => true,
            _ => false,
        };

        private static int MatchParen(string s, int open)
        {
            int depth = 0;
            bool inStr = false;
            char q = '\0';
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr) { if (c == q) inStr = false; continue; }
                if (c == '\'' || c == '"') { inStr = true; q = c; continue; }
                if (c == '(') depth++;
                else if (c == ')') { depth--; if (depth == 0) return i; }
            }
            return s.Length - 1;
        }

        /// <summary>Index of the closing bracket matching the opener at <paramref name="open"/>
        /// (string-aware, supports both <c>()</c> and <c>[]</c>), or -1 if unbalanced.</summary>
        private static int MatchBracket(string s, int open)
        {
            char openCh = s[open];
            char closeCh = openCh == '(' ? ')' : ']';
            int depth = 0;
            bool inStr = false;
            char q = '\0';
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr) { if (c == q) inStr = false; continue; }
                if (c == '\'' || c == '"') { inStr = true; q = c; continue; }
                if (c == openCh) depth++;
                else if (c == closeCh) { depth--; if (depth == 0) return i; }
            }
            return -1;
        }
    }
}
