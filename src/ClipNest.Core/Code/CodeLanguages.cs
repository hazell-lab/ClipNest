using System;
using System.Collections.Generic;

namespace ClipNest.Core.Code;

public sealed record CodeLanguage(string Id, string DisplayName);

/// <summary>
/// the languages the code preview can color.
/// </summary>
public static class CodeLanguages
{
    public const string PlainText = "plaintext";
    public const string CSharp = "csharp";
    public const string JavaScript = "javascript";
    public const string Python = "python";
    public const string Java = "java";
    public const string Cpp = "cpp";
    public const string Json = "json";
    public const string Xml = "xml";
    public const string Css = "css";
    public const string Sql = "sql";
    public const string PowerShell = "powershell";
    public const string Shell = "shell";

    public static IReadOnlyList<CodeLanguage> All { get; } = new[]
    {
        new CodeLanguage(PlainText, "Plain text"),
        new CodeLanguage(CSharp, "C#"),
        new CodeLanguage(JavaScript, "JavaScript / TypeScript"),
        new CodeLanguage(Python, "Python"),
        new CodeLanguage(Java, "Java"),
        new CodeLanguage(Cpp, "C / C++"),
        new CodeLanguage(Json, "JSON"),
        new CodeLanguage(Xml, "HTML / XML"),
        new CodeLanguage(Css, "CSS"),
        new CodeLanguage(Sql, "SQL"),
        new CodeLanguage(PowerShell, "PowerShell"),
        new CodeLanguage(Shell, "Shell (bash)"),
    };

    public static bool IsKnown(string? id)
    {
        foreach (var language in All)
        {
            if (string.Equals(language.Id, id, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static string DisplayNameFor(string? id)
    {
        foreach (var language in All)
        {
            if (string.Equals(language.Id, id, StringComparison.Ordinal))
            {
                return language.DisplayName;
            }
        }

        return "Plain text";
    }
}
