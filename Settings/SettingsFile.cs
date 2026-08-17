namespace Accel.Settings;

using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>Outcome of loading a settings.json. Distinct states so callers can refuse rather than overwrite.</summary>
public enum SettingsLoadStatus
{
    /// <summary>File loaded and parsed into a JSON object.</summary>
    Ok,

    /// <summary>File does not exist. Install may create it.</summary>
    Missing,

    /// <summary>File exists but contains only whitespace. Install must refuse.</summary>
    Empty,

    /// <summary>File exists but is not parseable JSON, or its root is not an object. Install must refuse.</summary>
    Malformed,
}

/// <summary>
/// Loads / atomically saves <c>settings.json</c> as a <see cref="JsonNode"/> DOM.
///
/// Never a typed POCO: a POCO round-trip silently drops unknown top-level keys
/// (<c>env</c>, <c>permissions</c>, <c>theme</c>, <c>effortLevel</c>,
/// <c>preferredNotifChannel</c>, ...) that exist in real files.
/// </summary>
public sealed class SettingsFile
{
    public const string BackupSuffix = ".accel.bak";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,

        // Non-escaping encoder so existing non-ASCII content is not mangled into \uXXXX.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonNodeOptions NodeOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private SettingsFile(string path, SettingsLoadStatus status, JsonObject? root, string? error)
    {
        Path = path;
        Status = status;
        Root = root;
        ErrorMessage = error;
    }

    public string Path { get; }

    public SettingsLoadStatus Status { get; }

    /// <summary>The DOM root, non-null only when <see cref="Status"/> is <see cref="SettingsLoadStatus.Ok"/>.</summary>
    public JsonObject? Root { get; }

    public string? ErrorMessage { get; }

    /// <summary>True once the <c>.accel.bak</c> copy has been taken in this session.</summary>
    public bool BackupTaken { get; private set; }

    public string BackupPath => Path + BackupSuffix;

    /// <summary>
    /// Whether an install may proceed. Missing is writable (the file is created);
    /// Empty/Malformed must be refused rather than overwritten.
    /// </summary>
    public bool IsWritableForInstall => Status is SettingsLoadStatus.Ok or SettingsLoadStatus.Missing;

    /// <summary>Loads the file. Never throws on missing/empty/malformed content.</summary>
    public static SettingsFile Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        string text;
        try
        {
            if (!File.Exists(path))
            {
                return new SettingsFile(path, SettingsLoadStatus.Missing, null, null);
            }

            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            return new SettingsFile(path, SettingsLoadStatus.Malformed, null, ex.Message);
        }

        return FromText(path, text);
    }

    /// <summary>Parses settings JSON text (used by tests and by re-read-before-write).</summary>
    public static SettingsFile FromText(string path, string? text)
    {
        if (text is null)
        {
            return new SettingsFile(path, SettingsLoadStatus.Missing, null, null);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new SettingsFile(path, SettingsLoadStatus.Empty, null, "settings file is empty");
        }

        try
        {
            var node = JsonNode.Parse(text, NodeOptions, DocumentOptions);
            if (node is not JsonObject obj)
            {
                return new SettingsFile(path, SettingsLoadStatus.Malformed, null, "root JSON value is not an object");
            }

            return new SettingsFile(path, SettingsLoadStatus.Ok, obj, null);
        }
        catch (JsonException ex)
        {
            return new SettingsFile(path, SettingsLoadStatus.Malformed, null, ex.Message);
        }
    }

    /// <summary>An empty settings DOM, for the Missing case (install creates the file).</summary>
    public static SettingsFile CreateNew(string path) =>
        new(path, SettingsLoadStatus.Ok, new JsonObject(), null);

    public static string Serialize(JsonNode? root) =>
        (root?.ToJsonString(SerializerOptions) ?? "{}") + Environment.NewLine;

    /// <summary>
    /// Writes the DOM atomically: temp file in the same directory, then replace. Takes a
    /// <c>.accel.bak</c> copy of the original before the first write of this session.
    /// </summary>
    public void Save(JsonNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!BackupTaken)
        {
            if (File.Exists(Path))
            {
                File.Copy(Path, BackupPath, overwrite: true);
            }

            BackupTaken = true;
        }

        var temp = System.IO.Path.Combine(
            directory ?? ".",
            $".accel-{Guid.NewGuid():N}.tmp");

        var payload = Serialize(root);

        try
        {
            File.WriteAllText(temp, payload, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(Path))
            {
                try
                {
                    File.Replace(temp, Path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (IOException)
                {
                    // File.Replace's atomic swap can fail with "Unable to remove the file to be
                    // replaced" when the destination sits under a cloud-synced profile (OneDrive)
                    // or is briefly locked by AV/indexing - neither of which is a real conflict.
                    // Delete+move tolerates that transient lock; File.Move(overwrite: true) still
                    // fails on a genuinely locked file, so the exception still surfaces if this
                    // isn't transient.
                    File.Delete(Path);
                    File.Move(temp, Path, overwrite: true);
                }
            }
            else
            {
                File.Move(temp, Path, overwrite: true);
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
