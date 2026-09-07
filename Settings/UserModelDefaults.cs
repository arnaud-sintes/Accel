namespace Accel.Settings;

using System;
using System.Text.Json.Nodes;

/// <summary>
/// The user's own model/effort defaults, read out of their <c>settings.json</c> (<c>"model"</c> and
/// <c>"effortLevel"</c>) - what a new `claude` session would pick if Accel passed no flags at all.
///
/// <para><b>Why read them.</b> The "Create session" dialog used to open on a hardcoded
/// Sonnet/medium regardless of what the user had configured, so someone whose settings said
/// Opus/high got Sonnet/medium every time they created a session from Accel - Accel silently
/// overriding a preference it never asked about. These values are the correct preselection.</para>
///
/// <para><b>What is deliberately not read here.</b> The <c>modelPicker</c> settings field (custom
/// or replacement rows, including <c>replaceBuiltInOptions</c>) is not parsed: Claude Code already
/// applies it when rendering the picker, so <see cref="Accel.Orchestration.ModelCatalogProbe"/>
/// observes the merged result. Re-implementing that merge here would be a second, divergent
/// interpretation of a setting Accel does not own.</para>
/// </summary>
public sealed record UserModelDefaults(string? Model, string? EffortLevel)
{
    /// <summary>Nothing configured - the caller falls back to the catalog's own defaults.</summary>
    public static readonly UserModelDefaults None = new(null, null);

    /// <summary>
    /// Reads the defaults from the settings file at <paramref name="path"/> (defaulting to
    /// <see cref="Accel.Cli.AccelPaths.DefaultSettingsPath"/>). Never throws: a missing, unreadable or
    /// malformed settings file yields <see cref="None"/>, since a preselection is a convenience and
    /// must never be able to break startup.
    /// </summary>
    public static UserModelDefaults Read(string? path = null)
    {
        try
        {
            var file = SettingsFile.Load(path ?? Accel.Cli.AccelPaths.DefaultSettingsPath());
            return FromRoot(file.Root);
        }
        catch (Exception)
        {
            return None;
        }
    }

    /// <summary>Test seam: the same extraction against an already-parsed settings root.</summary>
    public static UserModelDefaults FromRoot(JsonObject? root)
    {
        if (root is null)
        {
            return None;
        }

        return new UserModelDefaults(ReadString(root, "model"), ReadString(root, "effortLevel"));
    }

    private static string? ReadString(JsonObject root, string property)
    {
        if (!root.TryGetPropertyValue(property, out var node) || node is null)
        {
            return null;
        }

        try
        {
            var value = node.GetValue<string>();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception)
        {
            // Present but not a string (a number, an object) - no more meaningful than absent.
            return null;
        }
    }
}
