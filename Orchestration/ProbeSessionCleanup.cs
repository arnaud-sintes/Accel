namespace Accel.Orchestration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
/// Removes the throwaway Claude Code session <see cref="ModelCatalogProbe"/> leaves behind: the probe
/// drives a real interactive <c>claude</c> and types <c>/model</c>, which Claude Code records as a
/// session transcript in the working directory's project folder, where it shows up in the user's
/// session list. Best-effort and deliberately conservative - only a file that appeared during the
/// probe, is tiny, and holds nothing but the <c>/model</c> command is deleted, so a real session the
/// user started in the same folder at the same moment is never touched.
/// </summary>
internal static class ProbeSessionCleanup
{
    private const long MaxProbeTranscriptBytes = 64 * 1024;

    internal static IReadOnlySet<string> Snapshot(string workingDirectory, string? projectsRoot = null)
    {
        var directory = ProjectDirectory(workingDirectory, projectsRoot);
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*.jsonl").ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>();
        }
        catch (IOException)
        {
            return new HashSet<string>();
        }
    }

    internal static void DeleteProbeSessions(
        string workingDirectory, IReadOnlySet<string> before, string? projectsRoot = null)
    {
        var directory = ProjectDirectory(workingDirectory, projectsRoot);
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl"))
            {
                if (before.Contains(file) || !IsProbeTranscript(file))
                {
                    continue;
                }

                File.Delete(file);
                var sessionFolder = Path.Combine(directory, Path.GetFileNameWithoutExtension(file));
                if (Directory.Exists(sessionFolder))
                {
                    Directory.Delete(sessionFolder, recursive: true);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover one-line session is cosmetic; never fail discovery over it.
        }
    }

    private static bool IsProbeTranscript(string file)
    {
        if (new FileInfo(file).Length > MaxProbeTranscriptBytes)
        {
            return false;
        }

        var text = File.ReadAllText(file);
        return text.Contains("<command-name>/model</command-name>", StringComparison.Ordinal) &&
               !text.Contains("\"type\":\"user\"", StringComparison.Ordinal) &&
               !text.Contains("\"type\":\"assistant\"", StringComparison.Ordinal);
    }

    // Claude Code names a project folder after its cwd with every non-alphanumeric, non-hyphen
    // character replaced by '-' ("C:\projects\markdogwn-editor" -> "C--projects-markdogwn-editor").
    private static string ProjectDirectory(string workingDirectory, string? projectsRoot)
    {
        var root = projectsRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        var name = new string(workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '-').ToArray());
        return Path.Combine(root, name);
    }
}
