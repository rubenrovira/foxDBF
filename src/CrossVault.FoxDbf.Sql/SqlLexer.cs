using System.Collections.Generic;

namespace CrossVault.FoxDbf.Sql;

internal enum TokKind
{
    Word,       // identifier or (case-insensitive) keyword
    Number,     // integer / decimal literal
    String,     // 'xx' or "xx" literal (content protects keywords inside it)
    Date,       // { ... } date/datetime literal
    Comma,
    Dot,
    LParen,
    RParen,
    Star,       // *
    Eq,         // =
    Bang,       // !   (db!table separator; also expression NOT)
    Question,   // ?   (USE ?)
    Semicolon,  // statement separator
    Op,         // any other operator char (+ - / ^ < > # % & : | etc.)
    Eof,
}

/// <summary>A lexical token carrying its half-open source span [<see cref="Start"/>,<see cref="End"/>).</summary>
internal readonly record struct Token(TokKind Kind, string Text, int Start, int End);

/// <summary>
/// Tokenizes VFP-SQL source. Strings (<c>'…'</c>/<c>"…"</c>) and date literals (<c>{…}</c>)
/// are single tokens so SQL keywords appearing inside them never terminate an expression
/// fragment. Operators not needed by the SQL skeleton are emitted as <see cref="TokKind.Op"/>;
/// the parser reconstructs fragment text from raw source spans, so their exact grouping is
/// irrelevant — they merely keep fragment scanning paren/comma aware.
/// </summary>
internal static class SqlLexer
{
    public static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        int i = 0, n = s.Length;
        while (i < n)
        {
            char c = s[i];

            // whitespace
            if (c is ' ' or '\t' or '\r' or '\n' or '\f')
            {
                i++;
                continue;
            }

            // && line comment
            if (c == '&' && i + 1 < n && s[i + 1] == '&')
            {
                i += 2;
                while (i < n && s[i] != '\n') i++;
                continue;
            }

            int start = i;

            // string literal: ' or "
            if (c == '\'' || c == '"')
            {
                char q = c;
                i++;
                while (i < n && s[i] != q) i++;
                if (i >= n)
                    throw new FoxDbfSqlException("Unterminated string literal.", start);
                i++; // closing quote
                tokens.Add(new Token(TokKind.String, s.Substring(start, i - start), start, i));
                continue;
            }

            // bracket-string literal: [ ... ]  (VFP's third string delimiter)
            if (c == '[')
            {
                i++;
                while (i < n && s[i] != ']') i++;
                if (i >= n)
                    throw new FoxDbfSqlException("Unterminated string literal.", start);
                i++; // closing bracket
                tokens.Add(new Token(TokKind.String, s.Substring(start, i - start), start, i));
                continue;
            }

            // date / datetime literal: { ... }
            if (c == '{')
            {
                i++;
                while (i < n && s[i] != '}') i++;
                if (i >= n)
                    throw new FoxDbfSqlException("Unterminated date literal.", start);
                i++; // closing brace
                tokens.Add(new Token(TokKind.Date, s.Substring(start, i - start), start, i));
                continue;
            }

            // word: identifier / keyword
            if (IsIdentStart(c))
            {
                i++;
                while (i < n && IsIdentPart(s[i])) i++;
                tokens.Add(new Token(TokKind.Word, s.Substring(start, i - start), start, i));
                continue;
            }

            // number: digits [. digits]
            if (char.IsDigit(c))
            {
                i++;
                while (i < n && char.IsDigit(s[i])) i++;
                if (i < n && s[i] == '.' && i + 1 < n && char.IsDigit(s[i + 1]))
                {
                    i++; // '.'
                    while (i < n && char.IsDigit(s[i])) i++;
                }
                tokens.Add(new Token(TokKind.Number, s.Substring(start, i - start), start, i));
                continue;
            }

            // single-char punctuation
            TokKind kind = c switch
            {
                ',' => TokKind.Comma,
                '.' => TokKind.Dot,
                '(' => TokKind.LParen,
                ')' => TokKind.RParen,
                '*' => TokKind.Star,
                '=' => TokKind.Eq,
                '!' => TokKind.Bang,
                '?' => TokKind.Question,
                ';' => TokKind.Semicolon,
                _ => TokKind.Op,
            };
            i++;
            tokens.Add(new Token(kind, s.Substring(start, i - start), start, i));
        }

        tokens.Add(new Token(TokKind.Eof, string.Empty, n, n));
        return tokens;
    }

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_';
}
