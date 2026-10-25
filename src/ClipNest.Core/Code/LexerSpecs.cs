using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace ClipNest.Core.Code;

/// <summary>
/// describes how one language looks so the generic tokenizer can color it.
/// </summary>
internal sealed class LexerSpec
{
    public required string Id { get; init; }
    public string[] LineComments { get; init; } = Array.Empty<string>();
    public (string Open, string Close)[] BlockComments { get; init; } = Array.Empty<(string, string)>();
    public char[] Quotes { get; init; } = { '"', '\'' };
    public bool TripleQuotes { get; init; }
    public bool VerbatimAtStrings { get; init; }
    public bool DoubledQuoteEscapes { get; init; }
    public bool HashLineIsPreprocessor { get; init; }
    public bool Decorators { get; init; }
    public bool CaseInsensitive { get; init; }
    public bool DashInWords { get; init; }
    public bool DashOperators { get; init; }
    public char VariablePrefix { get; init; }
    public HashSet<string> Keywords { get; init; } = new(StringComparer.Ordinal);
    public HashSet<string> Types { get; init; } = new(StringComparer.Ordinal);
}

internal static class LexerSpecs
{
    private static readonly Dictionary<string, LexerSpec> Specs = Build();

    public static bool TryGet(string? id, [NotNullWhen(true)] out LexerSpec? spec)
    {
        spec = null;
        return id is not null && Specs.TryGetValue(id, out spec);
    }

    private static HashSet<string> Words(string list) =>
        new(list.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    private static Dictionary<string, LexerSpec> Build()
    {
        var cStyleComments = new[] { ("/*", "*/") };
        var specs = new[]
        {
            new LexerSpec
            {
                Id = CodeLanguages.CSharp,
                LineComments = new[] { "//" },
                BlockComments = cStyleComments,
                VerbatimAtStrings = true,
                HashLineIsPreprocessor = true,
                Keywords = Words("abstract as async await base break case catch checked class const continue default delegate do else enum event explicit extern false finally fixed for foreach get goto if implicit in init interface internal is lock namespace new null operator out override params partial private protected public readonly record ref required return sealed set sizeof stackalloc static struct switch this throw true try typeof unchecked unsafe using value var virtual void volatile when where while with yield"),
                Types = Words("bool byte char decimal double dynamic float int long nint nuint object sbyte short string uint ulong ushort Task List Dictionary String Console Math DateTime TimeSpan Guid Exception IEnumerable"),
            },
            new LexerSpec
            {
                Id = CodeLanguages.JavaScript,
                LineComments = new[] { "//" },
                BlockComments = cStyleComments,
                Quotes = new[] { '"', '\'', '`' },
                Decorators = true,
                Keywords = Words("abstract as async await break case catch class const constructor continue debugger declare default delete do else enum export extends false finally for from function get if implements import in instanceof interface let new null of private protected public readonly return set static super switch this throw true try type typeof undefined var void while with yield"),
                Types = Words("any boolean number string symbol unknown never object Array Promise Map Set Date JSON Math Object String Number Boolean Error RegExp console document window"),
            },
            new LexerSpec
            {
                Id = CodeLanguages.Python,
                LineComments = new[] { "#" },
                TripleQuotes = true,
                Decorators = true,
                Keywords = Words("False None True and as assert async await break class continue def del elif else except finally for from global if import in is lambda match nonlocal not or pass raise return try while with yield self"),
                Types = Words("bool bytes dict float int list object set str tuple print len range open super isinstance Exception ValueError TypeError"),
            },
            new LexerSpec
            {
                Id = CodeLanguages.Java,
                LineComments = new[] { "//" },
                BlockComments = cStyleComments,
                Decorators = true,
                Keywords = Words("abstract assert break case catch class const continue default do else enum extends false final finally for goto if implements import instanceof interface native new null package private protected public record return static strictfp super switch synchronized this throw throws transient true try var void volatile while yield"),
                Types = Words("boolean byte char double float int long short String Object Integer Long Double Boolean List ArrayList Map HashMap System Exception"),
            },
            new LexerSpec
            {
                Id = CodeLanguages.Cpp,
                LineComments = new[] { "//" },
                BlockComments = cStyleComments,
                HashLineIsPreprocessor = true,
                Keywords = Words("alignas alignof auto break case catch class const constexpr const_cast continue decltype default delete do dynamic_cast else enum explicit export extern false for friend goto if inline mutable namespace new noexcept nullptr operator private protected public register reinterpret_cast return sizeof static static_assert static_cast struct switch template this throw true try typedef typename union using virtual volatile while NULL"),
                Types = Words("bool char double float int long short signed unsigned void size_t wchar_t int8_t int16_t int32_t int64_t uint8_t uint16_t uint32_t uint64_t string vector map std"),
            },
            new LexerSpec
            {
                Id = CodeLanguages.Sql,
                LineComments = new[] { "--" },
                BlockComments = cStyleComments,
                DoubledQuoteEscapes = true,
                CaseInsensitive = true,
                Keywords = Words("add all alter and as asc begin between by case check column commit constraint create cross database default delete desc distinct drop else end exists foreign from full group having if in index inner insert into is join key left like limit not null offset on or order outer primary references replace right rollback select set table then top transaction trigger truncate union unique update values view when where with"),
                Types = Words("bigint bit blob boolean char date datetime decimal double float int integer money nchar numeric nvarchar real smallint text time timestamp tinyint uniqueidentifier varchar count sum avg min max coalesce"),
            },
            new LexerSpec
            {
                Id = CodeLanguages.PowerShell,
                LineComments = new[] { "#" },
                BlockComments = new[] { ("<#", "#>") },
                DoubledQuoteEscapes = true,
                CaseInsensitive = true,
                DashInWords = true,
                DashOperators = true,
                VariablePrefix = '$',
                Keywords = Words("begin break catch class continue data do dynamicparam else elseif end enum exit filter finally for foreach function if in param process return switch throw trap try until using while"),
                Types = Words("string int bool array hashtable pscustomobject datetime"),
            },
            new LexerSpec
            {
                Id = CodeLanguages.Shell,
                LineComments = new[] { "#" },
                VariablePrefix = '$',
                Keywords = Words("if then else elif fi case esac for while until do done in function select time return exit export local readonly declare unset shift source alias"),
                Types = Words("echo printf cd ls cat grep sed awk find chmod chown mkdir rm cp mv curl wget sudo apt apt-get git npm pip python tar"),
            },
        };

        var map = new Dictionary<string, LexerSpec>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            map[spec.Id] = spec;
        }

        return map;
    }
}
