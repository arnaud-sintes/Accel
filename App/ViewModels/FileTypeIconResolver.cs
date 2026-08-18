namespace Accel.App.ViewModels;

using System;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Maps a file name's extension to a short chip label and a pastel background colour for panel B's
/// file tree row (<see cref="FilesPanelNodeViewModel"/>). WPF-free and static so it stays trivially
/// unit-testable, same rationale as <see cref="Accel.Metrics.ModelBadgeTable"/>/
/// <see cref="SessionVisualStateResolver"/> for their own hex-string tables.
/// </summary>
public static class FileTypeIconResolver
{
    /// <summary>Fallback chip for any extension not in <see cref="Table"/> - a neutral gray dot
    /// rather than no chip at all, so every file row reads consistently.</summary>
    public static readonly (string Label, string ColorHex) Unknown = (string.Empty, "#FF3A3A3A");

    private static readonly Dictionary<string, (string Label, string ColorHex)> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        [".md"] = ("MD", "#FFB7B3F0"),
        [".markdown"] = ("MD", "#FFB7B3F0"),
        [".json"] = ("{}", "#FFF4D58D"),
        [".jsonc"] = ("{}", "#FFF4D58D"),
        [".ts"] = ("TS", "#FF8FC6FF"),
        [".tsx"] = ("TSX", "#FF8FC6FF"),
        [".js"] = ("JS", "#FFFCE38F"),
        [".jsx"] = ("JSX", "#FFFCE38F"),
        [".cpp"] = ("C++", "#FFB0E8C6"),
        [".cc"] = ("C++", "#FFB0E8C6"),
        [".cxx"] = ("C++", "#FFB0E8C6"),
        [".h"] = ("H", "#FFB0E8C6"),
        [".hpp"] = ("H", "#FFB0E8C6"),
        [".cs"] = ("C#", "#FFCDB4F5"),
        [".csproj"] = ("CS", "#FFCDB4F5"),
        [".sln"] = ("SLN", "#FFCDB4F5"),
        [".py"] = ("PY", "#FFA8D8B9"),
        [".yml"] = ("YML", "#FFF6C99B"),
        [".yaml"] = ("YML", "#FFF6C99B"),
        [".xml"] = ("XML", "#FFF6B4C9"),
        [".css"] = ("CSS", "#FF9FD9F0"),
        [".html"] = ("HTM", "#FFF4A9A9"),
        [".sh"] = ("SH", "#FFD9D9D9"),
        [".ps1"] = ("PS1", "#FFD9D9D9"),
        [".txt"] = ("TXT", "#FFD9D9D9"),
    };

    /// <summary>Never called for a directory row (see <see cref="FilesPanelNodeViewModel.IconLabel"/>) -
    /// only ever passed a file name.</summary>
    public static (string Label, string ColorHex) Resolve(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        return !string.IsNullOrEmpty(ext) && Table.TryGetValue(ext, out var entry) ? entry : Unknown;
    }
}
