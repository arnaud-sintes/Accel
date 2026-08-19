namespace Accel.App.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

/// <summary>
/// Coarse per-file-type language bucket used only to pick a regex rule set for panel D's read-only
/// file viewer (see <see cref="SyntaxHighlighter"/>) - never a real parser/AST, just enough to make
/// comments/strings/numbers/keywords visually distinct.
/// </summary>
public enum SourceLanguage
{
    PlainText,
    CLike,
    Python,
    Json,
    Markup,
    Yaml,
    Markdown,
    Shell,
}

/// <summary>
/// Maps a file name's extension to a <see cref="SourceLanguage"/> bucket. Deliberately coarse: e.g.
/// C#, C++, Java, and JS/TS all share <see cref="SourceLanguage.CLike"/> rather than getting their
/// own grammar, since a read-only viewer's colouring only needs to distinguish
/// comments/strings/numbers/keywords, not be a real per-language parser - the same restraint
/// <see cref="FileTypeIconResolver"/> already applies to its own extension table.
/// </summary>
public static class SourceLanguageResolver
{
    private static readonly Dictionary<string, SourceLanguage> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = SourceLanguage.CLike,
        [".csx"] = SourceLanguage.CLike,
        [".c"] = SourceLanguage.CLike,
        [".cpp"] = SourceLanguage.CLike,
        [".cc"] = SourceLanguage.CLike,
        [".cxx"] = SourceLanguage.CLike,
        [".h"] = SourceLanguage.CLike,
        [".hpp"] = SourceLanguage.CLike,
        [".java"] = SourceLanguage.CLike,
        [".js"] = SourceLanguage.CLike,
        [".jsx"] = SourceLanguage.CLike,
        [".ts"] = SourceLanguage.CLike,
        [".tsx"] = SourceLanguage.CLike,
        [".go"] = SourceLanguage.CLike,
        [".rs"] = SourceLanguage.CLike,
        [".css"] = SourceLanguage.CLike,
        [".py"] = SourceLanguage.Python,
        [".json"] = SourceLanguage.Json,
        [".jsonc"] = SourceLanguage.Json,
        [".xml"] = SourceLanguage.Markup,
        [".csproj"] = SourceLanguage.Markup,
        [".html"] = SourceLanguage.Markup,
        [".htm"] = SourceLanguage.Markup,
        [".xaml"] = SourceLanguage.Markup,
        [".yml"] = SourceLanguage.Yaml,
        [".yaml"] = SourceLanguage.Yaml,
        [".md"] = SourceLanguage.Markdown,
        [".markdown"] = SourceLanguage.Markdown,
        [".sh"] = SourceLanguage.Shell,
        [".bash"] = SourceLanguage.Shell,
        [".ps1"] = SourceLanguage.Shell,
    };

    /// <summary>Never called for a directory - only ever passed a file name.</summary>
    public static SourceLanguage Resolve(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(ext) && Table.TryGetValue(ext, out var language) ? language : SourceLanguage.PlainText;
    }
}

/// <summary>One coloured run of <see cref="SyntaxHighlighter.Tokenize"/>'s output. A
/// <see langword="null"/> <see cref="ColorHex"/> means "leave it at the viewer's default
/// foreground" - never a real colour choice of its own.</summary>
public readonly record struct SyntaxToken(string Text, string? ColorHex);

/// <summary>
/// Regex-based, single-pass "highlighting" for panel D's read-only file viewer
/// (<c>MainWindow.ShowFileTabAsync</c>) - deliberately not a real tokenizer/parser: each
/// <see cref="SourceLanguage"/> bucket gets one combined regex (comments, strings, numbers,
/// keywords, ...), and anything the regex doesn't match comes back as a default-coloured token.
/// WPF-free (hex strings only, same convention as <see cref="FileTypeIconResolver"/>) so it stays
/// unit-testable without a UI thread. Good enough to make a file's type visually obvious at a
/// glance; not a substitute for a real editor's syntax highlighting.
/// </summary>
public static class SyntaxHighlighter
{
    private const string CommentColor = "#FF6A9955";
    private const string StringColor = "#FFCE9178";
    private const string NumberColor = "#FFB5CEA8";
    private const string KeywordColor = "#FF569CD6";
    private const string TagColor = "#FF569CD6";
    private const string AttributeColor = "#FF9CDCFE";
    private const string HeadingColor = "#FFDCDCAA";

    private const string CLikeKeywords =
        "abstract|and|as|assert|async|await|base|bool|boolean|break|byte|case|catch|char|class|const|continue|" +
        "def|default|delete|do|double|elif|else|enum|export|extends|false|final|finally|float|for|foreach|from|" +
        "func|function|global|goto|if|implements|import|in|instanceof|int|interface|is|lambda|let|long|namespace|" +
        "new|nonlocal|not|null|nullptr|of|or|override|package|private|protected|public|readonly|ref|return|sealed|" +
        "short|sizeof|static|string|struct|super|switch|template|this|throw|true|try|type|typedef|typename|typeof|" +
        "union|unsafe|using|var|virtual|void|volatile|while|yield";

    private static readonly Regex CLike = Build(
        @"(?<comment>//[^\n]*|/\*[\s\S]*?\*/)|" +
        "(?<string>@?\"(?:[^\"\\\\]|\\\\.)*\"|'(?:[^'\\\\]|\\\\.)*')|" +
        @"(?<number>\b0[xX][0-9a-fA-F]+\b|\b\d+\.?\d*(?:[eE][+-]?\d+)?[fFdDLuU]*\b)|" +
        $@"(?<keyword>\b(?:{CLikeKeywords})\b)");

    private const string PythonKeywords =
        "and|as|assert|async|await|break|class|continue|def|del|elif|else|except|False|finally|for|from|global|" +
        "if|import|in|is|lambda|None|nonlocal|not|or|pass|raise|return|self|True|try|while|with|yield";

    private static readonly Regex Python = Build(
        @"(?<comment>\#[^\n]*)|" +
        "(?<string>\"\"\"[\\s\\S]*?\"\"\"|'''[\\s\\S]*?'''|\"(?:[^\"\\\\]|\\\\.)*\"|'(?:[^'\\\\]|\\\\.)*')|" +
        @"(?<number>\b\d+\.?\d*(?:[eE][+-]?\d+)?\b)|" +
        $@"(?<keyword>\b(?:{PythonKeywords})\b)");

    private static readonly Regex Json = Build(
        "(?<string>\"(?:[^\"\\\\]|\\\\.)*\")|" +
        @"(?<number>-?\b\d+\.?\d*(?:[eE][+-]?\d+)?\b)|" +
        @"(?<keyword>\btrue\b|\bfalse\b|\bnull\b)");

    private static readonly Regex Markup = Build(
        @"(?<comment><!--[\s\S]*?-->)|" +
        "(?<string>\"[^\"]*\"|'[^']*')|" +
        @"(?<tag></?[A-Za-z][\w:.-]*|/?>)|" +
        @"(?<attribute>\b[A-Za-z_:][\w:.-]*(?=\s*=))");

    private static readonly Regex Yaml = Build(
        @"(?<comment>\#[^\n]*)|" +
        "(?<string>\"(?:[^\"\\\\]|\\\\.)*\"|'(?:[^']|'')*')|" +
        @"(?<keyword>(?m:^[\t ]*(?:-[\t ]*)?[\w.-]+(?=\s*:)))");

    private static readonly Regex Markdown = Build(
        @"(?<heading>(?m:^\#{1,6}[\t ].*$))|" +
        @"(?<string>`[^`\n]*`)");

    private static readonly Regex Shell = Build(
        @"(?<comment>\#[^\n]*)|" +
        "(?<string>\"(?:[^\"\\\\]|\\\\.)*\"|'[^']*')|" +
        @"(?<number>\b\d+\b)|" +
        @"(?<keyword>\$\{?\w+\}?|\b(?:if|then|elif|else|fi|for|foreach|while|do|done|case|esac|switch|function|param|return|exit|local|export|break|continue|begin|process|end)\b)");

    private static Regex Build(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Files above this size are never highlighted (<see cref="Tokenize"/> returns the whole
    /// content as one default-coloured token) - a single combined regex re-scanning a huge file on
    /// the UI thread would visibly stall it, and a file this large is rarely hand-read line by line
    /// anyway.</summary>
    public const int MaxHighlightedLength = 512 * 1024;

    /// <summary>Splits <paramref name="content"/> into alternating default/coloured
    /// <see cref="SyntaxToken"/>s for <paramref name="language"/>. Always returns at least one token
    /// (possibly empty-text) so callers never need to special-case an empty result.</summary>
    public static IReadOnlyList<SyntaxToken> Tokenize(string content, SourceLanguage language)
    {
        Regex? pattern = language switch
        {
            SourceLanguage.CLike => CLike,
            SourceLanguage.Python => Python,
            SourceLanguage.Json => Json,
            SourceLanguage.Markup => Markup,
            SourceLanguage.Yaml => Yaml,
            SourceLanguage.Markdown => Markdown,
            SourceLanguage.Shell => Shell,
            _ => null,
        };

        if (pattern is null || content.Length > MaxHighlightedLength)
        {
            return new[] { new SyntaxToken(content, null) };
        }

        var tokens = new List<SyntaxToken>();
        int last = 0;

        foreach (Match match in pattern.Matches(content))
        {
            if (match.Index > last)
            {
                tokens.Add(new SyntaxToken(content[last..match.Index], null));
            }

            tokens.Add(new SyntaxToken(match.Value, ColorFor(match)));
            last = match.Index + match.Length;
        }

        if (last < content.Length || tokens.Count == 0)
        {
            tokens.Add(new SyntaxToken(content[last..], null));
        }

        return tokens;
    }

    private static string? ColorFor(Match match)
    {
        if (match.Groups["comment"].Success)
        {
            return CommentColor;
        }

        if (match.Groups["string"].Success)
        {
            return StringColor;
        }

        if (match.Groups["number"].Success)
        {
            return NumberColor;
        }

        if (match.Groups["keyword"].Success)
        {
            return KeywordColor;
        }

        if (match.Groups["tag"].Success)
        {
            return TagColor;
        }

        if (match.Groups["attribute"].Success)
        {
            return AttributeColor;
        }

        if (match.Groups["heading"].Success)
        {
            return HeadingColor;
        }

        return null;
    }
}
