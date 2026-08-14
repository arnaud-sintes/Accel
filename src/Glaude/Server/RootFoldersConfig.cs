using System.Text;
using System.Text.Json;

namespace Glaude.Server;

/// <summary>
/// Phase UI-C: loads the v1 "root folders" config - a simple JSON array of folder path
/// strings, e.g. <c>["C:/projects"]</c> - that <c>GET /roots</c> exposes read-only.
///
/// <para><b>Probe order (per project-ui.md's "Root folders (`folder.json`)" section,
/// decision 0):</b></para>
/// <list type="number">
/// <item><c>%USERPROFILE%\.claude\glaude-folders.json</c> - the durable home, colocated with
/// the <c>glaude-state.json</c> that <see cref="Glaude.Cli.FileBackedStatusLineChainStore.DefaultPath"/>
/// already writes to <c>%USERPROFILE%\.claude\</c>.</item>
/// <item><c>&lt;directory of the running executable&gt;\folder.json</c>.</item>
/// <item><c>&lt;current working directory&gt;\folder.json</c>.</item>
/// </list>
///
/// <para><b>"First that exists AND parses" - exact semantics used here:</b> candidates are
/// tried strictly in order. The first candidate whose file *exists on disk* is the one that
/// decides the outcome - if it exists but is malformed JSON, or valid JSON that isn't an array
/// of strings, the result is an empty array immediately; later candidates are never
/// consulted (a user who put a broken config at the durable-home slot almost certainly meant
/// that slot, not the exe-dir or cwd fallback - silently trying the next slot would mask the
/// mistake). Only when a candidate's file does not exist at all do we move on to the next
/// candidate. If none of the three files exist, or the loop falls through, the result is an
/// empty array. This method never throws.</para>
///
/// <para>Paths *inside* a parsed array are returned exactly as written in the config file -
/// no <see cref="Path.GetFullPath"/>, no separator normalization - per project-ui.md: any
/// normalization is for internal comparison purposes only (a later phase's job), never for
/// what gets rendered back to a client.</para>
/// </summary>
public static class RootFoldersConfig
{
    /// <summary>File name for the durable-home candidate (candidate 1).</summary>
    public const string DurableFileName = "glaude-folders.json";

    /// <summary>File name for the exe-directory and cwd candidates (candidates 2 and 3).</summary>
    public const string LocalFileName = "folder.json";

    /// <summary>Loads using the real default candidate paths (see <see cref="DefaultCandidatePaths"/>).</summary>
    public static string[] Load() => Load(DefaultCandidatePaths());

    /// <summary>
    /// The three real candidate paths, in probe order. Exposed separately from
    /// <see cref="Load()"/> so tests can substitute a different candidate list via
    /// <see cref="Load(IReadOnlyList{string})"/> without touching the real filesystem
    /// locations (<c>%USERPROFILE%</c>, the exe directory, the process cwd).
    /// </summary>
    public static string[] DefaultCandidatePaths() => new[]
    {
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            DurableFileName),
        Path.Combine(AppContext.BaseDirectory, LocalFileName),
        Path.Combine(Directory.GetCurrentDirectory(), LocalFileName),
    };

    /// <summary>
    /// Core probe/parse logic, taking an explicit candidate list (in probe order) so tests can
    /// exercise every branch (missing/malformed/valid, at each of the three slots) without
    /// depending on real machine state.
    /// </summary>
    public static string[] Load(IReadOnlyList<string> candidatePaths)
    {
        foreach (string path in candidatePaths)
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                string text = File.ReadAllText(path, Encoding.UTF8);
                return ParseStringArray(text);
            }
            catch
            {
                // The candidate exists but couldn't even be read (locked, permissions, etc.) -
                // same "found it, but it's broken" outcome as a parse failure: degrade to
                // empty rather than falling through to the next candidate.
                return Array.Empty<string>();
            }
        }

        return Array.Empty<string>();
    }

    private static string[] ParseStringArray(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            var result = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    // Not a homogeneous array of strings - the whole file is treated as
                    // malformed for our purposes.
                    return Array.Empty<string>();
                }

                result.Add(item.GetString() ?? string.Empty);
            }

            return result.ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
