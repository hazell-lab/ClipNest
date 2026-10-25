using System;
using System.Linq;
using System.Text;
using ClipNest.Core.Code;
using ClipNest.Core.Text;
using Xunit;

namespace ClipNest.Core.Tests;

public class CodeAndTextTests
{
    [Theory]
    [InlineData("{\"name\": \"clip\", \"count\": 3}", CodeLanguages.Json)]
    [InlineData("<div class=\"a\">hi</div>", CodeLanguages.Xml)]
    [InlineData("using System;\nnamespace Demo;\npublic class A { public int X { get; set; } }", CodeLanguages.CSharp)]
    [InlineData("def add(a, b):\n    return a + b\n\nif __name__ == '__main__':\n    print(add(1, 2))", CodeLanguages.Python)]
    [InlineData("import React from 'react';\nconst x = () => {\n  console.log('hi');\n};", CodeLanguages.JavaScript)]
    [InlineData("public class Main {\n  public static void main(String[] args) {\n    System.out.println(\"hi\");\n  }\n}", CodeLanguages.Java)]
    [InlineData("#include <stdio.h>\nint main() {\n  printf(\"hi\");\n}", CodeLanguages.Cpp)]
    [InlineData("SELECT id, name FROM users WHERE id = 3;", CodeLanguages.Sql)]
    [InlineData("Get-ChildItem -Path C:\\ | Where-Object { $_.Length -gt 100 }", CodeLanguages.PowerShell)]
    [InlineData("#!/bin/bash\nfor f in *.txt; do\n  echo $f\ndone", CodeLanguages.Shell)]
    [InlineData(".card {\n  color: red;\n  margin: 4px;\n}", CodeLanguages.Css)]
    [InlineData("Remember to buy milk and call mom tomorrow.", CodeLanguages.PlainText)]
    [InlineData("", CodeLanguages.PlainText)]
    public void Detector_makes_reasonable_guesses(string text, string expected)
    {
        Assert.Equal(expected, LanguageDetector.Detect(text));
    }

    [Fact]
    public void Csharp_tokens_cover_keywords_strings_comments_and_numbers()
    {
        const string code = "// note\nvar s = \"hi\\\"there\"; int n = 42; /* block */\n#if DEBUG";
        var tokens = SyntaxTokenizer.Tokenize(code, CodeLanguages.CSharp);

        string TextOf(TokenKind kind) => string.Join("|", tokens.Where(t => t.Kind == kind).Select(t => code.Substring(t.Start, t.Length)));

        Assert.Equal("// note|/* block */", TextOf(TokenKind.Comment));
        Assert.Equal("\"hi\\\"there\"", TextOf(TokenKind.String));
        Assert.Equal("var", TextOf(TokenKind.Keyword));
        Assert.Equal("int", TextOf(TokenKind.Type));
        Assert.Equal("42", TextOf(TokenKind.Number));
        Assert.Equal("#if DEBUG", TextOf(TokenKind.Preprocessor));
    }

    [Fact]
    public void Json_keys_and_values_get_different_colors()
    {
        const string json = "{\"a\": \"b\", \"n\": -1.5, \"ok\": true}";
        var tokens = SyntaxTokenizer.Tokenize(json, CodeLanguages.Json);

        Assert.Equal(new[] { "\"a\"", "\"n\"", "\"ok\"" }, tokens.Where(t => t.Kind == TokenKind.Property).Select(t => json.Substring(t.Start, t.Length)));
        Assert.Contains(tokens, t => t.Kind == TokenKind.String && json.Substring(t.Start, t.Length) == "\"b\"");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Number && json.Substring(t.Start, t.Length) == "-1.5");
        Assert.Contains(tokens, t => t.Kind == TokenKind.Keyword);
    }

    [Fact]
    public void Markup_tokens_find_tags_attributes_and_values()
    {
        const string html = "<!-- c --><a href=\"x\">link</a>";
        var tokens = SyntaxTokenizer.Tokenize(html, CodeLanguages.Xml);
        var pieces = tokens.Select(t => (t.Kind, html.Substring(t.Start, t.Length))).ToList();

        Assert.Contains((TokenKind.Comment, "<!-- c -->"), pieces);
        Assert.Contains((TokenKind.Tag, "<a"), pieces);
        Assert.Contains((TokenKind.AttributeName, "href"), pieces);
        Assert.Contains((TokenKind.String, "\"x\""), pieces);
        Assert.Contains((TokenKind.Tag, "</a"), pieces);
    }

    [Fact]
    public void Tokenizer_never_throws_and_tokens_stay_in_order_and_in_bounds()
    {
        var random = new Random(1234);
        const string alphabet = "abc{}[]()<>\"'`/\\*#@$-:;=!.\n\t 0123456789xyzé";
        foreach (var language in CodeLanguages.All)
        {
            for (var round = 0; round < 200; round++)
            {
                var builder = new StringBuilder();
                var length = random.Next(0, 120);
                for (var i = 0; i < length; i++)
                {
                    builder.Append(alphabet[random.Next(alphabet.Length)]);
                }

                var text = builder.ToString();
                var tokens = SyntaxTokenizer.Tokenize(text, language.Id);
                var lastEnd = 0;
                foreach (var token in tokens)
                {
                    Assert.True(token.Start >= lastEnd, $"{language.Id}: overlapping token");
                    Assert.True(token.Length > 0, $"{language.Id}: empty token");
                    Assert.True(token.Start + token.Length <= text.Length, $"{language.Id}: token past end");
                    lastEnd = token.Start + token.Length;
                }
            }
        }
    }

    [Fact]
    public void Plain_text_has_no_colors()
    {
        Assert.Empty(SyntaxTokenizer.Tokenize("if (x) { return 1; }", CodeLanguages.PlainText));
    }

    [Fact]
    public void Relative_times_read_naturally()
    {
        var now = new DateTimeOffset(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("just now", RelativeTime.Format(now.AddSeconds(-10), now));
        Assert.Equal("5 min ago", RelativeTime.Format(now.AddMinutes(-5), now));
        Assert.Equal("3 h ago", RelativeTime.Format(now.AddHours(-3), now));
        Assert.Equal("4 days ago", RelativeTime.Format(now.AddDays(-4), now));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    [InlineData(300L * 1024 * 1024, "300 MB")]
    public void Byte_sizes_are_short(long bytes, string expected)
    {
        Assert.Equal(expected, ByteSize.Format(bytes));
    }

    [Fact]
    public void Previews_trim_leading_blank_lines_and_tabs()
    {
        Assert.Equal("a    b\nc", PreviewFormatter.ForList("\n\n  a\tb\r\nc\n\n"));
        Assert.Equal("first", PreviewFormatter.FirstLine("\n  first\nsecond"));
        Assert.EndsWith("…", PreviewFormatter.ForList(new string('x', 1000), 10));
    }
}
