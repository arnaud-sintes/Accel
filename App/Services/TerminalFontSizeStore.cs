namespace Accel.App.Services;

using System;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>
/// The bounds and default for panel D's terminal font size, in CSS pixels. Pure and WPF-free so the
/// clamp rule is unit-testable and is the <b>single</b> source of truth shared by both sides of the
/// WebView2 bridge: <see cref="TerminalFontSizeStore"/> clamps what it reads back from disk with it,
/// and <c>terminal.js</c> mirrors the same three constants (see its <c>MIN_FONT_SIZE</c> /
/// <c>MAX_FONT_SIZE</c> / <c>DEFAULT_FONT_SIZE</c>) - keep them in sync when changing either.
/// </summary>
public static class TerminalFontSize
{
    /// <summary>xterm's own default and the value terminal.js shipped with before zoom existed, so a
    /// user who never touches the shortcuts sees exactly what they saw before.</summary>
    public const int Default = 14;

    /// <summary>Below this the Cascadia Mono / Consolas glyphs stop being legible at 100% scaling and
    /// the integer cell-width snap in terminal.js has almost nothing left to work with.</summary>
    public const int Minimum = 8;

    /// <summary>A generous ceiling for presenting/screen-sharing; beyond it FitAddon yields so few
    /// columns that a full-screen TUI like Claude Code's cannot lay itself out any more.</summary>
    public const int Maximum = 32;

    /// <summary>Clamps <paramref name="fontSize"/> into [<see cref="Minimum"/>, <see cref="Maximum"/>].
    /// Also what makes a hand-edited or corrupted on-disk value safe to hand to the page.</summary>
    public static int Clamp(int fontSize) => Math.Clamp(fontSize, Minimum, Maximum);
}

/// <summary>
/// Persists the terminal font size the user last chose (via Ctrl+= / Ctrl+- / Ctrl+0 / Ctrl+wheel in
/// panel D - see <c>terminal.js</c>'s <c>setFontSize</c>) so it survives an app restart, in
/// <c>%USERPROFILE%\.claude\accel-ui.json</c>.
///
/// <para><b>Why host-side and not <c>localStorage</c> in the page:</b> the page could remember the
/// value itself with less code (its WebView2 profile is persistent - see
/// <see cref="Accel.App.Controls.TerminalView.WebView2UserDataFolder"/>), but that hides a user
/// preference inside an opaque Chromium profile folder that a user (or a future "reset the terminal"
/// action) may wipe wholesale, and it would be unreachable from C# - no unit test, no future
/// settings UI, no way for the host to seed a fresh page with it after a WebView2 process recovery.
/// Keeping it in a plain <c>accel-*.json</c> next to the app's other per-user state follows the same
/// location decision as <c>accel-state.json</c>/<c>accel-folders.json</c>/<c>accel-sessions.json</c>
/// (see <see cref="Accel.Cli.FileBackedStatusLineChainStore"/>'s doc for the rationale). The page is
/// then just told the value at document creation (<c>window.accelTerminalFontSize</c>) and reports
/// each change back over the WebMessage bridge.</para>
///
/// <para><b>Its own file, not <c>accel-state.json</c>:</b> that file is owned by the hook-side CLI
/// verbs (statusline chaining) and is read on every status-line render; a UI preference has nothing
/// to do with it and should not be able to corrupt it. <c>accel-ui.json</c> is meant to grow into the
/// home for any further window-level UI preferences, one flat key each.</para>
///
/// <para>Never throws: a missing, unreadable or malformed file always reads as
/// <see cref="TerminalFontSize.Default"/>, and a failed save is swallowed - losing a zoom level is a
/// cosmetic degradation, never worth a crash on the UI thread. Other keys already present in the
/// file are preserved on save (read-modify-write of the whole DOM, not a typed overwrite).</para>
/// </summary>
public sealed class TerminalFontSizeStore
{
    public const string DefaultFileName = "accel-ui.json";

    /// <summary>The top-level JSON key. Camel-case to match Claude Code's own settings.json style.</summary>
    public const string FontSizeKey = "terminalFontSize";

    /// <summary>Hand-edited boolean, default true: when on, terminal.js swallows the app's "enable mouse
    /// reporting" sequences so text selection never needs Shift. Read-only here - never written.</summary>
    public const string NativeSelectionKey = "terminalNativeSelection";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;

    public TerminalFontSizeStore(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
    }

    /// <summary><c>%USERPROFILE%\.claude\accel-ui.json</c>, next to the app's other per-user state.</summary>
    public static string DefaultPath() =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            DefaultFileName);

    /// <summary>
    /// The saved font size, clamped via <see cref="TerminalFontSize.Clamp"/>, or
    /// <see cref="TerminalFontSize.Default"/> when the file is missing, unreadable, malformed, or has no
    /// (or a non-integer) <see cref="FontSizeKey"/> entry.
    /// </summary>
    public bool LoadNativeSelection() =>
        TryLoad()?[NativeSelectionKey] is JsonValue value && value.TryGetValue<bool>(out var enabled)
            ? enabled
            : true;

    public int Load()
    {
        var root = TryLoad();
        if (root?[FontSizeKey] is not JsonValue value)
        {
            return TerminalFontSize.Default;
        }

        // TryGetValue<int> is false for a fractional number or a string - either reads as "unset"
        // rather than throwing or truncating a value the user did not actually choose.
        return value.TryGetValue<int>(out var fontSize)
            ? TerminalFontSize.Clamp(fontSize)
            : TerminalFontSize.Default;
    }

    /// <summary>
    /// Records <paramref name="fontSize"/> (clamped) as the value to restore next launch. Best-effort:
    /// a write failure (read-only profile, AV lock) is swallowed so the live terminal keeps its new
    /// size regardless. Returns whether the write actually landed, for callers that want to log it.
    /// </summary>
    public bool Save(int fontSize)
    {
        try
        {
            var root = TryLoad() ?? new JsonObject();
            root[FontSizeKey] = TerminalFontSize.Clamp(fontSize);
            WriteAtomic(root);
            return true;
        }
        catch
        {
            // Best effort: a preference that fails to persist must never surface as an error in the
            // terminal the user is zooming.
            return false;
        }
    }

    private JsonObject? TryLoad()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var text = File.ReadAllText(_path, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            return JsonNode.Parse(text) as JsonObject;
        }
        catch
        {
            // A malformed file is treated as absent on read; the next Save rewrites it wholesale,
            // which is the right recovery for a file whose only content is UI preferences.
            return null;
        }
    }

    // Same temp-file-then-replace shape as FileBackedStatusLineChainStore.WriteAtomic, so a crash
    // mid-write can never leave a half-written file behind.
    private void WriteAtomic(JsonObject root)
    {
        var fullPath = System.IO.Path.GetFullPath(_path);
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = System.IO.Path.Combine(directory ?? ".", $".accel-ui-{Guid.NewGuid():N}.tmp");
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
