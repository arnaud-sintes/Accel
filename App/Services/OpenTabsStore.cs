namespace Accel.App.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>What a saved tab was. Only the kinds that can be brought back: a Claude Code session
/// (resumed by id), a plain terminal (relaunched in its folder) and a file. Git change/diff tabs are
/// views over transient repository state and are not restored.</summary>
public enum SavedTabKind
{
    Session,
    Shell,
    File,
}

/// <summary>One tab to bring back. <see cref="Id"/> is the session id for a session, the full path for a
/// file, and a throwaway GUID for a terminal (kept only so the record is self-describing).</summary>
public sealed record SavedTab(SavedTabKind Kind, string Id, string? Title, string? WorkingDirectory);

/// <summary>The tab strip as it was left: its tabs in strip order, and which one was selected.</summary>
public sealed record SavedTabSet(IReadOnlyList<SavedTab> Tabs, int? SelectedIndex)
{
    public static SavedTabSet Empty { get; } = new(Array.Empty<SavedTab>(), null);
}

/// <summary>
/// Remembers which tabs were open when Accel last ran, in <c>%USERPROFILE%\.claude\accel-open-tabs.json</c>,
/// so the next launch can offer to restore them.
///
/// <para>Its own file rather than a key in <c>accel-ui.json</c>: that file is read-modify-written by the
/// terminal zoom on every change, and a list that is rewritten on every tab open/close has no business
/// sharing a write path with it.</para>
///
/// <para>Never throws: a missing, unreadable or malformed file reads as <see cref="SavedTabSet.Empty"/>
/// (the user simply gets no restore offer), and a failed save is swallowed - failing to remember tabs
/// must never surface as an error while the user is opening one.</para>
/// </summary>
public sealed class OpenTabsStore
{
    public const string DefaultFileName = "accel-open-tabs.json";

    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly string _path;

    public OpenTabsStore(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
    }

    /// <summary><c>%USERPROFILE%\.claude\accel-open-tabs.json</c>.</summary>
    public static string DefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            DefaultFileName);

    /// <summary>The saved set, or <see cref="SavedTabSet.Empty"/> when there is none or it cannot be read.</summary>
    public SavedTabSet Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return SavedTabSet.Empty;
            }

            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path, Encoding.UTF8), SerializerOptions);
            if (document is null || document.Version != CurrentVersion || document.Tabs is null)
            {
                return SavedTabSet.Empty;
            }

            var tabs = document.Tabs
                .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Id))
                .ToList();

            int? selected = document.SelectedIndex is { } index && index >= 0 && index < tabs.Count ? index : null;
            return new SavedTabSet(tabs, selected);
        }
        catch
        {
            return SavedTabSet.Empty;
        }
    }

    /// <summary>Writes <paramref name="tabs"/> (temp file then replace, so a crash mid-write cannot leave
    /// half a file). Returns whether it landed.</summary>
    public bool Save(SavedTabSet tabs)
    {
        ArgumentNullException.ThrowIfNull(tabs);

        try
        {
            string fullPath = Path.GetFullPath(_path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string payload = JsonSerializer.Serialize(
                new Document { Version = CurrentVersion, SelectedIndex = tabs.SelectedIndex, Tabs = tabs.Tabs.ToList() },
                SerializerOptions) + Environment.NewLine;

            string temp = Path.Combine(directory ?? ".", $".accel-open-tabs-{Guid.NewGuid():N}.tmp");
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

            return true;
        }
        catch
        {
            return false;
        }
    }

    // The on-disk shape, kept separate from SavedTabSet so the file can carry a version and so a
    // hand-edited or older file fails closed (reads as empty) instead of half-deserialising.
    private sealed class Document
    {
        public int Version { get; set; }

        public int? SelectedIndex { get; set; }

        public List<SavedTab>? Tabs { get; set; }
    }
}
