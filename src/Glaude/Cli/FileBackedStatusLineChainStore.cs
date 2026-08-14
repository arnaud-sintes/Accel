namespace Glaude.Cli;

using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glaude.Settings;

/// <summary>
/// Persists captured <see cref="StatusLineCapture"/> objects to a small JSON file on disk.
///
/// <see cref="StatusLineChain"/>'s in-memory store (Phase 2) is only good for a single
/// process's lifetime. But `install` (which does the capturing) and `statusline` /
/// `subagent-statusline` (which need the capture to chain to) are each separate, short-lived
/// process invocations spawned by Claude Code — the capture must survive across process
/// boundaries, which requires disk persistence. This is that persistence layer.
///
/// <para><b>Location decision:</b> <c>%USERPROFILE%\.claude\glaude-state.json</c>, i.e.
/// alongside <c>settings.json</c> rather than next to the executable. Rationale: once Phase 8
/// publishes a self-contained single-file exe, its own directory may be read-only (e.g. under
/// Program Files) or may not even be a stable location the user chose; <c>~/.claude</c> is
/// already a directory Claude Code itself reads and writes, so it is guaranteed writable and
/// is a natural, discoverable home for Glaude's own small amount of state.</para>
///
/// Never throws: a missing, unreadable, or malformed state file is always treated as
/// "no capture" rather than an error — losing a captured original statusLine is a degraded
/// (but safe) outcome, never a crash.
/// </summary>
public sealed class FileBackedStatusLineChainStore : IStatusLineChainStore
{
    public const string DefaultFileName = "glaude-state.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string path;

    public FileBackedStatusLineChainStore(string path)
    {
        this.path = path ?? throw new ArgumentNullException(nameof(path));
    }

    /// <summary>The default on-disk location, next to the real settings.json.</summary>
    public static string DefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            DefaultFileName);

    public bool TryGet(StatusLineField field, out StatusLineCapture capture)
    {
        capture = StatusLineCapture.None;

        var root = TryLoad();
        if (root is null || root[FieldKey(field)] is not JsonObject obj)
        {
            return false;
        }

        try
        {
            capture = StatusLineCapture.FromJson(obj);
            return true;
        }
        catch
        {
            capture = StatusLineCapture.None;
            return false;
        }
    }

    public void Save(StatusLineField field, StatusLineCapture capture)
    {
        try
        {
            var root = TryLoad() ?? new JsonObject();
            root[FieldKey(field)] = capture.ToJson();
            WriteAtomic(root);
        }
        catch
        {
            // Best effort: a failure to persist the capture must never abort an install.
        }
    }

    public void Remove(StatusLineField field)
    {
        try
        {
            var root = TryLoad();
            if (root is null)
            {
                return;
            }

            root.Remove(FieldKey(field));
            WriteAtomic(root);
        }
        catch
        {
            // Best effort.
        }
    }

    private static string FieldKey(StatusLineField field) => field switch
    {
        StatusLineField.SubagentStatusLine => GlaudeHookSpec.SubagentStatusLineField,
        _ => GlaudeHookSpec.StatusLineField,
    };

    private JsonObject? TryLoad()
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var text = File.ReadAllText(path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return JsonNode.Parse(text) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private void WriteAtomic(JsonObject root)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = Path.Combine(directory ?? ".", $".glaude-state-{Guid.NewGuid():N}.tmp");
        var payload = root.ToJsonString(SerializerOptions) + Environment.NewLine;

        try
        {
            File.WriteAllText(temp, payload, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(fullPath))
            {
                File.Replace(temp, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, fullPath, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                    // Best effort cleanup only.
                }
            }
        }
    }
}
