namespace Accel.Orchestration;

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

/// <summary>One row of the rendered picker: its ordinal, the short label, the trailing
/// description, and whether the picker's cursor was sitting on it.</summary>
public sealed record ModelPickerRow(int Number, string Label, string Description, bool Selected)
{
    public override string ToString() =>
        $"{(Selected ? ">" : " ")} {Number}. {Label}" + (Description.Length > 0 ? $"  [{Description}]" : string.Empty);
}

/// <summary>What one scrape attempt recovered from a rendered screen.</summary>
public sealed record ModelPickerScreen(
    IReadOnlyList<ModelPickerRow> Rows,
    string? CurrentEffort,
    bool SawPickerChrome,
    string? ClaudeCliVersion = null)
{
    /// <summary>Whether this looks like a picker screen worth reading at all - chrome present and at
    /// least one row. Callers must gate on this rather than on <see cref="Rows"/> alone, since a
    /// permission prompt or a <c>/resume</c> list also paints numbered rows.</summary>
    public bool IsUsable => SawPickerChrome && Rows.Count > 0;
}

/// <summary>
/// Parses the rendered picker. Anchored on the row ordinals the picker paints ("1. Default",
/// "2. Opus (1M context)", ...) rather than on model-family keywords: the ordinals are structural,
/// so a row survives being renamed, and rows Accel's own vocabulary cannot name at all - the
/// "Default (recommended)" row, or a "(1M context)" variant - are still recovered instead of
/// silently dropped. Only two shapes are load-bearing, and both are a fair measure of how
/// brittle this is: <c>N. label  description</c>, and a single "<c>&lt;level&gt; effort</c>"
/// line for the currently selected row.
/// </summary>
public static class ModelPickerScreenParser
{
    /// <summary>A row line: an optional cursor glyph, the ordinal, the label, then (after a run of
    /// two or more spaces, which is how the picker columnizes) the description.</summary>
    private static readonly Regex RowPattern = new(
        @"^(?<cursor>\S)?\s*(?<number>\d+)\.\s+(?<label>\S.*?)(?:\s{2,}(?<description>\S.*))?$",
        RegexOptions.Compiled);

    /// <summary>The picker's single effort line, e.g. "❯ Medium effort / to adjust". It reports only
    /// the tier the selected row is currently on - the picker never lists that row's whole range,
    /// which is why reading the range at all requires walking the slider key by key.</summary>
    private static readonly Regex EffortPattern = new(
        @"(?<level>[A-Za-z]+)\s+effort\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Chrome the real picker paints around its rows - without it, a line that happens to
    /// start with an ordinal is some other list (a permission prompt, a /resume picker), not this
    /// one, and the parse must not be trusted.</summary>
    private static readonly string[] ChromeKeywords = { "Select model", "Switch between Claude models" };

    private static readonly Regex WhitespaceRun = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>The version banner above the picker, e.g. "Claude Code v2.1.263".</summary>
    private static readonly Regex VersionPattern = new(
        @"Claude Code\s+v(?<version>[0-9][0-9A-Za-z.\-]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static ModelPickerScreen Parse(IReadOnlyList<string> screen)
    {
        var rows = new List<ModelPickerRow>();
        var chrome = false;

        foreach (var rawLine in screen)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            foreach (var keyword in ChromeKeywords)
            {
                if (line.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    chrome = true;
                    break;
                }
            }

            var match = RowPattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var number = int.Parse(match.Groups["number"].Value);
            var label = WhitespaceRun.Replace(match.Groups["label"].Value.Trim(), " ");
            var description = WhitespaceRun.Replace(match.Groups["description"].Value.Trim(), " ");

            // The cursor glyph is whatever the TUI happens to draw; all that matters is that the
            // ordinal was NOT the first non-space character on the line.
            var selected = match.Groups["cursor"].Success && !char.IsAsciiDigit(match.Groups["cursor"].Value[0]);

            if (!rows.Exists(existing => existing.Number == number))
            {
                rows.Add(new ModelPickerRow(number, label, description, selected));
            }
        }

        var effort = FindSelectedRowEffort(screen);
        rows.Sort((left, right) => left.Number.CompareTo(right.Number));
        return new ModelPickerScreen(rows, effort, chrome, FindCliVersion(screen));
    }

    /// <summary>
    /// Picks the CLI version out of the banner Claude Code paints above the picker ("Claude Code
    /// v2.1.263"). Recorded on the parse purely as provenance: the catalog is cached against the
    /// executable's identity, not this string, so a failure to find it costs nothing but a slightly
    /// less informative tooltip.
    /// </summary>
    private static string? FindCliVersion(IReadOnlyList<string> screen)
    {
        foreach (var line in screen)
        {
            var match = VersionPattern.Match(line);
            if (match.Success)
            {
                return match.Groups["version"].Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the picker's effort slider, scanning <i>upward</i> from the bottom of the screen. The
    /// direction is load-bearing and was a real bug caught while building this: Claude Code's status header
    /// says "Opus with medium effort · Claude Enterprise", so a top-down scan finds the header - which
    /// never changes while the slider moves - and the walk silently reports the same tier for every
    /// model. The slider is painted below the rows, the header above them, so the first match from the
    /// bottom is the slider. Two "&lt;level&gt; effort" strings on one screen, distinguishable only by
    /// their vertical position, is a fair illustration of what parsing a UI buys you.
    /// </summary>
    private static string? FindSelectedRowEffort(IReadOnlyList<string> screen)
    {
        for (var i = screen.Count - 1; i >= 0; i--)
        {
            var line = screen[i].Trim();
            if (line.Length == 0 || !line.Contains("effort", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The header names an account tier alongside the effort; the slider never does.
            if (line.Contains("Billing", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Claude Code v", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = EffortPattern.Match(line);
            if (match.Success)
            {
                return match.Groups["level"].Value.ToLowerInvariant();
            }
        }

        return null;
    }
}
