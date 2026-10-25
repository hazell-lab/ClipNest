using System;
using System.Collections.Generic;

namespace ClipNest.Core.Code;

public enum TokenKind
{
    Keyword,
    Type,
    String,
    Comment,
    Number,
    Preprocessor,
    Tag,
    AttributeName,
    Property,
    Variable,
}

/// <summary>
/// a colored piece of text. text between tokens is shown plain.
/// </summary>
public readonly record struct SyntaxToken(int Start, int Length, TokenKind Kind);

/// <summary>
/// splits text into colored pieces for the code preview.
/// it only reads the text, nothing is ever run.
/// </summary>
public static class SyntaxTokenizer
{
    public const int MaxHighlightChars = 200_000;

    public static IReadOnlyList<SyntaxToken> Tokenize(string? text, string? languageId)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<SyntaxToken>();
        }

        // very long texts are only colored at the start to stay fast
        var length = Math.Min(text.Length, MaxHighlightChars);
        var tokens = new List<SyntaxToken>();
        try
        {
            switch (languageId)
            {
                case CodeLanguages.Json:
                    TokenizeJson(text, length, tokens);
                    break;
                case CodeLanguages.Xml:
                    TokenizeMarkup(text, length, tokens);
                    break;
                case CodeLanguages.Css:
                    TokenizeCss(text, length, tokens);
                    break;
                default:
                    if (LexerSpecs.TryGet(languageId, out var spec))
                    {
                        TokenizeGeneric(text, length, spec, tokens);
                    }

                    break;
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // a coloring bug must never break the preview, so fall back to plain text
            return Array.Empty<SyntaxToken>();
        }

        return tokens;
    }

    private static void TokenizeGeneric(string s, int n, LexerSpec spec, List<SyntaxToken> tokens)
    {
        var i = 0;
        while (i < n)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (spec.HashLineIsPreprocessor && c == '#' && IsLineStart(s, i))
            {
                var end = LineEnd(s, i, n);
                tokens.Add(new SyntaxToken(i, end - i, TokenKind.Preprocessor));
                i = end;
                continue;
            }

            var block = MatchBlockComment(s, i, n, spec);
            if (block > i)
            {
                tokens.Add(new SyntaxToken(i, block - i, TokenKind.Comment));
                i = block;
                continue;
            }

            if (StartsWithAny(s, i, n, spec.LineComments))
            {
                var end = LineEnd(s, i, n);
                tokens.Add(new SyntaxToken(i, end - i, TokenKind.Comment));
                i = end;
                continue;
            }

            if (spec.VerbatimAtStrings && c == '@' && i + 1 < n && s[i + 1] == '"')
            {
                var end = ScanDoubledQuoteString(s, i + 1, n, '"');
                tokens.Add(new SyntaxToken(i, end - i, TokenKind.String));
                i = end;
                continue;
            }

            if (Array.IndexOf(spec.Quotes, c) >= 0)
            {
                int end;
                if (spec.TripleQuotes && i + 2 < n && s[i + 1] == c && s[i + 2] == c)
                {
                    var close = s.IndexOf(new string(c, 3), i + 3, n - (i + 3), StringComparison.Ordinal);
                    end = close < 0 ? n : close + 3;
                }
                else if (spec.DoubledQuoteEscapes)
                {
                    end = ScanDoubledQuoteString(s, i, n, c);
                }
                else
                {
                    end = ScanEscapedString(s, i, n, c, multiLine: c == '`');
                }

                tokens.Add(new SyntaxToken(i, end - i, TokenKind.String));
                i = end;
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < n && char.IsAsciiDigit(s[i + 1])))
            {
                var end = i + 1;
                while (end < n && (char.IsAsciiLetterOrDigit(s[end]) || s[end] == '.' || s[end] == '_'))
                {
                    end++;
                }

                tokens.Add(new SyntaxToken(i, end - i, TokenKind.Number));
                i = end;
                continue;
            }

            if (spec.VariablePrefix != '\0' && c == spec.VariablePrefix && i + 1 < n
                && (IsIdentStart(s[i + 1]) || s[i + 1] == '{'))
            {
                var end = i + 1;
                if (s[end] == '{')
                {
                    var close = s.IndexOf('}', end, n - end);
                    end = close < 0 ? n : close + 1;
                }
                else
                {
                    while (end < n && (IsIdentPart(s[end]) || s[end] == ':'))
                    {
                        end++;
                    }
                }

                tokens.Add(new SyntaxToken(i, end - i, TokenKind.Variable));
                i = end;
                continue;
            }

            if (spec.Decorators && c == '@' && i + 1 < n && IsIdentStart(s[i + 1]))
            {
                var end = i + 1;
                while (end < n && (IsIdentPart(s[end]) || s[end] == '.'))
                {
                    end++;
                }

                tokens.Add(new SyntaxToken(i, end - i, TokenKind.Preprocessor));
                i = end;
                continue;
            }

            if (IsIdentStart(c))
            {
                var end = i + 1;
                while (end < n && (IsIdentPart(s[end]) || (spec.DashInWords && s[end] == '-' && end + 1 < n && IsIdentPart(s[end + 1]))))
                {
                    end++;
                }

                var word = s.Substring(i, end - i);
                var kind = Classify(word, spec);
                if (kind is { } found)
                {
                    tokens.Add(new SyntaxToken(i, end - i, found));
                }

                i = end;
                continue;
            }

            if (spec.DashOperators && c == '-' && i + 1 < n && char.IsAsciiLetter(s[i + 1]))
            {
                // powershell style operators and parameters such as -eq or -path
                var end = i + 1;
                while (end < n && IsIdentPart(s[end]))
                {
                    end++;
                }

                tokens.Add(new SyntaxToken(i, end - i, TokenKind.Keyword));
                i = end;
                continue;
            }

            i++;
        }
    }

    private static TokenKind? Classify(string word, LexerSpec spec)
    {
        var lookup = spec.CaseInsensitive ? word.ToLowerInvariant() : word;
        if (spec.Keywords.Contains(lookup))
        {
            return TokenKind.Keyword;
        }

        if (spec.Types.Contains(lookup))
        {
            return TokenKind.Type;
        }

        // verb-noun words are commands in powershell
        if (spec.DashInWords && spec.Id == CodeLanguages.PowerShell && word.Contains('-', StringComparison.Ordinal))
        {
            return TokenKind.Type;
        }

        return null;
    }

    private static void TokenizeJson(string s, int n, List<SyntaxToken> tokens)
    {
        var i = 0;
        while (i < n)
        {
            var c = s[i];
            if (c == '"')
            {
                var end = ScanEscapedString(s, i, n, '"', multiLine: false);
                var next = end;
                while (next < n && char.IsWhiteSpace(s[next]))
                {
                    next++;
                }

                var kind = next < n && s[next] == ':' ? TokenKind.Property : TokenKind.String;
                tokens.Add(new SyntaxToken(i, end - i, kind));
                i = end;
                continue;
            }

            if (c == '-' || char.IsAsciiDigit(c))
            {
                var end = i + 1;
                while (end < n && (char.IsAsciiDigit(s[end]) || s[end] is '.' or 'e' or 'E' or '+' or '-'))
                {
                    end++;
                }

                tokens.Add(new SyntaxToken(i, end - i, TokenKind.Number));
                i = end;
                continue;
            }

            if (char.IsAsciiLetter(c))
            {
                var end = i + 1;
                while (end < n && char.IsAsciiLetter(s[end]))
                {
                    end++;
                }

                var word = s.AsSpan(i, end - i);
                if (word is "true" or "false" or "null")
                {
                    tokens.Add(new SyntaxToken(i, end - i, TokenKind.Keyword));
                }

                i = end;
                continue;
            }

            i++;
        }
    }

    private static void TokenizeMarkup(string s, int n, List<SyntaxToken> tokens)
    {
        var i = 0;
        while (i < n)
        {
            if (s[i] != '<')
            {
                var next = s.IndexOf('<', i, n - i);
                i = next < 0 ? n : next;
                continue;
            }

            if (Matches(s, i, n, "<!--"))
            {
                var close = s.IndexOf("-->", i + 4, n - (i + 4), StringComparison.Ordinal);
                var end = close < 0 ? n : close + 3;
                tokens.Add(new SyntaxToken(i, end - i, TokenKind.Comment));
                i = end;
                continue;
            }

            if (Matches(s, i, n, "<![CDATA["))
            {
                var close = s.IndexOf("]]>", i + 9, n - (i + 9), StringComparison.Ordinal);
                var end = close < 0 ? n : close + 3;
                tokens.Add(new SyntaxToken(i, end - i, TokenKind.String));
                i = end;
                continue;
            }

            if (Matches(s, i, n, "<?") || Matches(s, i, n, "<!"))
            {
                var close = s.IndexOf('>', i + 2, n - (i + 2));
                var end = close < 0 ? n : close + 1;
                tokens.Add(new SyntaxToken(i, end - i, TokenKind.Preprocessor));
                i = end;
                continue;
            }

            var nameStart = i + 1;
            if (nameStart < n && s[nameStart] == '/')
            {
                nameStart++;
            }

            if (nameStart >= n || !IsIdentStart(s[nameStart]))
            {
                // a lone less than sign in text
                i++;
                continue;
            }

            var nameEnd = nameStart;
            while (nameEnd < n && (IsIdentPart(s[nameEnd]) || s[nameEnd] is ':' or '-' or '.'))
            {
                nameEnd++;
            }

            tokens.Add(new SyntaxToken(i, nameEnd - i, TokenKind.Tag));
            i = nameEnd;

            // read attributes until the tag closes
            while (i < n && s[i] != '>')
            {
                var c = s[i];
                if (c == '/' && i + 1 < n && s[i + 1] == '>')
                {
                    break;
                }

                if (c is '"' or '\'')
                {
                    var close = s.IndexOf(c, i + 1, n - (i + 1));
                    var end = close < 0 ? n : close + 1;
                    tokens.Add(new SyntaxToken(i, end - i, TokenKind.String));
                    i = end;
                    continue;
                }

                if (IsIdentStart(c))
                {
                    var end = i + 1;
                    while (end < n && (IsIdentPart(s[end]) || s[end] is ':' or '-' or '.'))
                    {
                        end++;
                    }

                    tokens.Add(new SyntaxToken(i, end - i, TokenKind.AttributeName));
                    i = end;
                    continue;
                }

                if (c == '<')
                {
                    // a broken tag, start over from here
                    break;
                }

                i++;
            }

            if (i < n && s[i] == '<')
            {
                continue;
            }

            if (i < n && s[i] == '/')
            {
                tokens.Add(new SyntaxToken(i, Math.Min(2, n - i), TokenKind.Tag));
                i += 2;
            }
            else if (i < n)
            {
                tokens.Add(new SyntaxToken(i, 1, TokenKind.Tag));
                i++;
            }
        }
    }

    private static void TokenizeCss(string s, int n, List<SyntaxToken> tokens)
    {
        var depth = 0;
        var i = 0;
        while (i < n)
        {
            var c = s[i];
            if (Matches(s, i, n, "/*"))
            {
                var close = s.IndexOf("*/", i + 2, n - (i + 2), StringComparison.Ordinal);
                var end = close < 0 ? n : close + 2;
                tokens.Add(new SyntaxToken(i, end - i, TokenKind.Comment));
                i = end;
                continue;
            }

            if (c is '"' or '\'')
            {
                var end = ScanEscapedString(s, i, n, c, multiLine: false);
                tokens.Add(new SyntaxToken(i, end - i, TokenKind.String));
                i = end;
                continue;
            }

            if (c == '{')
            {
                depth++;
                i++;
                continue;
            }

            if (c == '}')
            {
                depth = Math.Max(0, depth - 1);
                i++;
                continue;
            }

            if (c == '@' && i + 1 < n && char.IsAsciiLetter(s[i + 1]))
            {
                var end = i + 1;
                while (end < n && (char.IsAsciiLetterOrDigit(s[end]) || s[end] == '-'))
                {
                    end++;
                }

                tokens.Add(new SyntaxToken(i, end - i, TokenKind.Keyword));
                i = end;
                continue;
            }

            if (c == '!' && Matches(s, i, n, "!important"))
            {
                tokens.Add(new SyntaxToken(i, 10, TokenKind.Keyword));
                i += 10;
                continue;
            }

            if (depth > 0 && c == '#' && i + 1 < n && char.IsAsciiHexDigit(s[i + 1]))
            {
                var end = i + 1;
                while (end < n && char.IsAsciiHexDigit(s[end]))
                {
                    end++;
                }

                tokens.Add(new SyntaxToken(i, end - i, TokenKind.Number));
                i = end;
                continue;
            }

            if (char.IsAsciiDigit(c) || (c == '.' && depth > 0 && i + 1 < n && char.IsAsciiDigit(s[i + 1])))
            {
                var end = i + 1;
                while (end < n && (char.IsAsciiLetterOrDigit(s[end]) || s[end] is '.' or '%'))
                {
                    end++;
                }

                tokens.Add(new SyntaxToken(i, end - i, TokenKind.Number));
                i = end;
                continue;
            }

            if (depth == 0 && (c is '.' or '#') && i + 1 < n && (IsIdentStart(s[i + 1]) || s[i + 1] == '-'))
            {
                var end = i + 1;
                while (end < n && (IsIdentPart(s[end]) || s[end] == '-'))
                {
                    end++;
                }

                tokens.Add(new SyntaxToken(i, end - i, TokenKind.Type));
                i = end;
                continue;
            }

            if (IsIdentStart(c) || c == '-')
            {
                var end = i + 1;
                while (end < n && (IsIdentPart(s[end]) || s[end] == '-'))
                {
                    end++;
                }

                if (depth == 0)
                {
                    tokens.Add(new SyntaxToken(i, end - i, TokenKind.Tag));
                }
                else
                {
                    var next = end;
                    while (next < n && (s[next] == ' ' || s[next] == '\t'))
                    {
                        next++;
                    }

                    if (next < n && s[next] == ':')
                    {
                        tokens.Add(new SyntaxToken(i, end - i, TokenKind.Property));
                    }
                }

                i = end;
                continue;
            }

            i++;
        }
    }

    private static int MatchBlockComment(string s, int i, int n, LexerSpec spec)
    {
        foreach (var (open, close) in spec.BlockComments)
        {
            if (!Matches(s, i, n, open))
            {
                continue;
            }

            var from = i + open.Length;
            var found = s.IndexOf(close, from, n - from, StringComparison.Ordinal);
            return found < 0 ? n : found + close.Length;
        }

        return i;
    }

    private static int ScanEscapedString(string s, int start, int n, char quote, bool multiLine)
    {
        var i = start + 1;
        while (i < n)
        {
            var c = s[i];
            if (c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == quote)
            {
                return i + 1;
            }

            if (!multiLine && c == '\n')
            {
                return i;
            }

            i++;
        }

        return n;
    }

    private static int ScanDoubledQuoteString(string s, int start, int n, char quote)
    {
        var i = start + 1;
        while (i < n)
        {
            if (s[i] == quote)
            {
                if (i + 1 < n && s[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return n;
    }

    private static bool IsLineStart(string s, int i)
    {
        for (var j = i - 1; j >= 0; j--)
        {
            if (s[j] == '\n')
            {
                return true;
            }

            if (s[j] != ' ' && s[j] != '\t' && s[j] != '\r')
            {
                return false;
            }
        }

        return true;
    }

    private static int LineEnd(string s, int i, int n)
    {
        var end = s.IndexOf('\n', i, n - i);
        return end < 0 ? n : end;
    }

    private static bool StartsWithAny(string s, int i, int n, string[] options)
    {
        foreach (var option in options)
        {
            if (Matches(s, i, n, option))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Matches(string s, int i, int n, string value) =>
        i + value.Length <= n && string.CompareOrdinal(s, i, value, 0, value.Length) == 0;

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_';
}
