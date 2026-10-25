using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClipNest.Core.Code;

/// <summary>
/// guesses the language of a text by looking for typical patterns.
/// this is a best effort guess and can be wrong, the user can always pick another one.
/// </summary>
public static class LanguageDetector
{
    private const int SampleChars = 20_000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(150);

    private static readonly (string Language, Regex Pattern, int Weight)[] Signals =
    {
        (CodeLanguages.CSharp, Make(@"^\s*using\s+System(\.[\w.]+)?\s*;"), 4),
        (CodeLanguages.CSharp, Make(@"\bnamespace\s+[\w.]+"), 3),
        (CodeLanguages.CSharp, Make(@"\b(public|private|internal|protected)\s+(static\s+)?(async\s+)?(class|record|interface|void|Task|string|int|bool)\b"), 2),
        (CodeLanguages.CSharp, Make(@"\{\s*get;\s*(set;|init;)?\s*\}"), 4),
        (CodeLanguages.CSharp, Make(@"\bConsole\.Write(Line)?\("), 4),
        (CodeLanguages.CSharp, Make(@"\bvar\s+\w+\s*=\s*new\b"), 2),
        (CodeLanguages.CSharp, Make(@"\bawait\s+\w+.*Async\("), 2),

        (CodeLanguages.Java, Make(@"\bpublic\s+static\s+void\s+main\s*\(\s*String"), 5),
        (CodeLanguages.Java, Make(@"\bSystem\.out\.print(ln)?\("), 5),
        (CodeLanguages.Java, Make(@"^\s*import\s+java(x)?\.[\w.*]+;"), 5),
        (CodeLanguages.Java, Make(@"^\s*package\s+[\w.]+;"), 3),
        (CodeLanguages.Java, Make(@"@Override\b"), 2),

        (CodeLanguages.JavaScript, Make(@"\bconsole\.(log|error|warn)\("), 4),
        (CodeLanguages.JavaScript, Make(@"\b(const|let)\s+\w+\s*=\s*"), 2),
        (CodeLanguages.JavaScript, Make(@"\bfunction\s*\w*\s*\("), 2),
        (CodeLanguages.JavaScript, Make(@"=>\s*\{"), 1),
        (CodeLanguages.JavaScript, Make(@"\brequire\(['""]"), 3),
        (CodeLanguages.JavaScript, Make(@"^\s*import\s+.+\s+from\s+['""]"), 4),
        (CodeLanguages.JavaScript, Make(@"^\s*export\s+(default|const|function|class|interface|type)\b"), 3),
        (CodeLanguages.JavaScript, Make(@"\bdocument\.(getElementById|querySelector)"), 4),

        (CodeLanguages.Python, Make(@"^\s*def\s+\w+\s*\(.*\)\s*(->\s*[\w\[\], .]+)?:\s*$"), 5),
        (CodeLanguages.Python, Make(@"^\s*from\s+[\w.]+\s+import\s+"), 4),
        (CodeLanguages.Python, Make(@"^\s*import\s+\w+(\s+as\s+\w+)?\s*$"), 2),
        (CodeLanguages.Python, Make(@"^\s*(elif|except)\b.*:\s*$"), 4),
        (CodeLanguages.Python, Make(@"\bself\.\w+"), 2),
        (CodeLanguages.Python, Make(@"^\s*class\s+\w+(\(.*\))?:\s*$"), 4),
        (CodeLanguages.Python, Make(@"\bprint\(.*\)\s*$"), 1),
        (CodeLanguages.Python, Make(@"if\s+__name__\s*==\s*['""]__main__['""]"), 5),

        (CodeLanguages.Cpp, Make(@"^\s*#include\s*[<""]"), 5),
        (CodeLanguages.Cpp, Make(@"\bstd::\w+"), 4),
        (CodeLanguages.Cpp, Make(@"\bint\s+main\s*\("), 3),
        (CodeLanguages.Cpp, Make(@"\bprintf\s*\("), 2),
        (CodeLanguages.Cpp, Make(@"^\s*#define\s+\w+"), 3),

        (CodeLanguages.Css, Make(@"^\s*[.#]?[\w-]+(\s*[,>+~]?\s*[.#:]?[\w-]+)*\s*\{\s*$"), 2),
        (CodeLanguages.Css, Make(@"^\s*[\w-]+\s*:\s*[^;{}]+;\s*$"), 1),
        (CodeLanguages.Css, Make(@"@media\s|@import\s|@keyframes\s"), 4),
        (CodeLanguages.Css, Make(@"\b(color|margin|padding|display|font-size|background)\s*:"), 2),

        (CodeLanguages.Sql, MakeIgnoreCase(@"\bselect\b[\s\S]+?\bfrom\b"), 4),
        (CodeLanguages.Sql, MakeIgnoreCase(@"\binsert\s+into\b"), 5),
        (CodeLanguages.Sql, MakeIgnoreCase(@"\bupdate\s+\w+\s+set\b"), 5),
        (CodeLanguages.Sql, MakeIgnoreCase(@"\bcreate\s+(table|index|view)\b"), 5),
        (CodeLanguages.Sql, MakeIgnoreCase(@"\b(inner|left|right)\s+join\b"), 3),
        (CodeLanguages.Sql, MakeIgnoreCase(@"\bwhere\s+\w+(\.\w+)?\s*(=|<|>|like|in)\b"), 2),

        (CodeLanguages.PowerShell, Make(@"\$\w+\s*=\s*"), 1),
        (CodeLanguages.PowerShell, Make(@"\b(Get|Set|New|Remove|Write|Start|Stop|Invoke|Import)-[A-Z]\w+"), 5),
        (CodeLanguages.PowerShell, MakeIgnoreCase(@"\s-(eq|ne|lt|gt|le|ge|like|match)\s"), 3),
        (CodeLanguages.PowerShell, Make(@"\|\s*(Where|ForEach|Select)-Object\b"), 5),
        (CodeLanguages.PowerShell, MakeIgnoreCase(@"^\s*param\s*\("), 3),

        (CodeLanguages.Shell, Make(@"^#!\s*/(usr/)?bin/(env\s+)?(ba|z)?sh"), 6),
        (CodeLanguages.Shell, Make(@"^\s*(sudo\s+)?(apt|apt-get|brew|yum|dnf|pacman)\s+(install|update)\b"), 4),
        (CodeLanguages.Shell, Make(@"^\s*(fi|done|esac)\s*$"), 4),
        (CodeLanguages.Shell, Make(@"^\s*export\s+\w+="), 3),
        (CodeLanguages.Shell, Make(@"^\s*(echo|cd|ls|grep|chmod|mkdir|curl)\s"), 2),
        (CodeLanguages.Shell, Make(@"\$\{\w+\}"), 2),
    };

    public static string Detect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return CodeLanguages.PlainText;
        }

        var sample = text.Length > SampleChars ? text[..SampleChars] : text;
        var trimmed = sample.TrimStart();

        if (LooksLikeJson(trimmed, text.Length <= SampleChars))
        {
            return CodeLanguages.Json;
        }

        if (LooksLikeMarkup(trimmed))
        {
            return CodeLanguages.Xml;
        }

        var scores = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (language, pattern, weight) in Signals)
        {
            int matches;
            try
            {
                matches = Math.Min(3, pattern.Count(sample));
            }
            catch (RegexMatchTimeoutException)
            {
                continue;
            }

            if (matches > 0)
            {
                scores[language] = scores.GetValueOrDefault(language) + (weight * matches);
            }
        }

        var best = CodeLanguages.PlainText;
        var bestScore = 0;
        foreach (var (language, score) in scores)
        {
            if (score > bestScore)
            {
                best = language;
                bestScore = score;
            }
        }

        // weak hints are not enough to call something code
        return bestScore >= 4 ? best : CodeLanguages.PlainText;
    }

    private static bool LooksLikeJson(string trimmed, bool isComplete)
    {
        if (trimmed.Length < 2 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return false;
        }

        if (!isComplete)
        {
            // too long to parse fully, so check for a typical key
            return Regex.IsMatch(trimmed, "^[\\[{]\\s*\"[^\"]+\"\\s*:", RegexOptions.None, RegexTimeout);
        }

        try
        {
            using var _ = JsonDocument.Parse(trimmed);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool LooksLikeMarkup(string trimmed)
    {
        if (trimmed.Length < 3 || trimmed[0] != '<')
        {
            return false;
        }

        return trimmed.Contains("</", StringComparison.Ordinal)
            || trimmed.Contains("/>", StringComparison.Ordinal)
            || trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase);
    }

    private static Regex Make(string pattern) =>
        new(pattern, RegexOptions.Multiline | RegexOptions.CultureInvariant, RegexTimeout);

    private static Regex MakeIgnoreCase(string pattern) =>
        new(pattern, RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, RegexTimeout);
}
