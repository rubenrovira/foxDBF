using System;
using System.Text;

namespace CrossVault.FoxDbf.MicroVfp;

// ─────────────────────────────────────────────────────────────────────────────
//  microVFP — expression-source NORMALISATION for memory ARRAYS.
//
//  The shared expression engine (CrossVault.FoxDbf.Expressions) has no concept of
//  memory arrays: it treats `[ … ]` purely as VFP's third STRING delimiter, and it
//  evaluates every function argument by VALUE. Two VFP array idioms therefore need a
//  small, semantics-preserving source rewrite BEFORE the text reaches the engine:
//
//   1. ARRAY SUBSCRIPTS — `a[i]` / `a[i,j]` (the bracket form) become `a(i)` / `a(i,j)`
//      so the engine parses them as a call the interpreter resolves to an element. A `[`
//      is a subscript ONLY when it immediately follows (no space) an identifier char,
//      `)` or `]` — exactly VFP's rule that distinguishes `a[1]` (subscript) from a
//      value-position `[string]` literal (e.g. `x = [abc]`, `… AND [abc]`).
//
//   2. ARRAY-NAME ARGUMENTS — `ALEN(a …)` / `AERROR(a)` take an array REFERENCE, not a
//      value; their first bare-identifier argument is wrapped in quotes (`ALEN('a' …)`)
//      so the interpreter's function host receives the NAME and can read the dimensions
//      / (re)dimension + fill the array. (VFP array functions always take a name here.)
//
//  Both passes are STRING-LITERAL aware (`'…'`, `"…"`, and an already-recognised `[…]`
//  literal are copied verbatim). The rewrite is confined to microVFP .prg expression
//  fragments — it never touches the SQL parser.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Source-text normalisation that lets the shared expression engine evaluate microVFP memory
/// arrays (bracket subscripts → parens; ALEN/AERROR array-name argument → quoted name).</summary>
internal static class MicroVfpExprRewrite
{
    private static bool IsIdentChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>Apply both array rewrites to <paramref name="s"/>; a no-op for text with neither a
    /// subscript bracket nor an ALEN/AERROR call.</summary>
    public static string Normalize(string s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
        s = ConvertSubscripts(s);
        s = QuoteArrayFnArg(s, "ALEN");
        s = QuoteArrayFnArg(s, "AERROR");
        return s;
    }

    /// <summary>Convert array-subscript brackets <c>name[…]</c> to <c>name(…)</c>, leaving value-position
    /// <c>[ … ]</c> string literals and quoted strings untouched.</summary>
    private static string ConvertSubscripts(string s)
    {
        if (s.IndexOf('[') < 0) return s;
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\'' || c == '"')                       // quoted string: copy verbatim.
            {
                char q = c; sb.Append(c); i++;
                while (i < s.Length) { sb.Append(s[i]); if (s[i] == q) break; i++; }
                continue;
            }
            if (c == '[')
            {
                char prev = i > 0 ? s[i - 1] : '\0';
                bool subscript = prev == ')' || prev == ']' || IsIdentChar(prev);
                if (subscript) { sb.Append('('); continue; }
                // value-position bracket string literal: copy through the matching ']'.
                sb.Append(c); i++;
                while (i < s.Length && s[i] != ']') { sb.Append(s[i]); i++; }
                if (i < s.Length) sb.Append(s[i]);
                continue;
            }
            if (c == ']') { sb.Append(')'); continue; }       // close of a converted subscript.
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Wrap the first bare-identifier argument of every <paramref name="fn"/><c>( … )</c> call in
    /// quotes, so the host receives the array NAME. String-literal aware.</summary>
    private static string QuoteArrayFnArg(string s, string fn)
    {
        if (s.Length == 0) return s;
        var sb = new StringBuilder(s.Length + 8);
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\'' || c == '"')                        // copy quoted string verbatim.
            {
                char q = c; sb.Append(c); i++;
                while (i < s.Length) { sb.Append(s[i]); if (s[i] == q) break; i++; }
                i++;
                continue;
            }
            if (char.IsAsciiLetter(c) || c == '_')            // an identifier run.
            {
                int start = i;
                i++;
                while (i < s.Length && IsIdentChar(s[i])) i++;
                string ident = s.Substring(start, i - start);
                sb.Append(ident);
                if (ident.Equals(fn, StringComparison.OrdinalIgnoreCase))
                    i = EmitQuotedFirstArg(s, i, sb);
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>Having just emitted the function name (cursor at <paramref name="i"/>), if a <c>(</c> +
    /// bare-identifier-first-argument follows, emit <c>('ident'</c> and return the cursor past the
    /// identifier; otherwise emit nothing and return <paramref name="i"/> unchanged.</summary>
    private static int EmitQuotedFirstArg(string s, int i, StringBuilder sb)
    {
        int p = i;
        while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
        if (p >= s.Length || s[p] != '(') return i;          // not a call.
        int open = p;
        p++;
        while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
        if (p >= s.Length || !(char.IsAsciiLetter(s[p]) || s[p] == '_')) return i;  // first arg not bare.
        int idStart = p;
        p++;
        while (p < s.Length && IsIdentChar(s[p])) p++;
        int idEnd = p;
        while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
        if (p >= s.Length || (s[p] != ',' && s[p] != ')')) return i;   // not a lone identifier arg.
        sb.Append(s, i, open - i + 1);                        // any spaces + '('
        sb.Append('\'').Append(s, idStart, idEnd - idStart).Append('\'');
        return idEnd;                                         // resume right after the identifier.
    }
}
