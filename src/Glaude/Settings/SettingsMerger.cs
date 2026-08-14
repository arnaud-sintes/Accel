namespace Glaude.Settings;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

/// <summary>Install state of Glaude's own entries relative to the expected set.</summary>
public enum InstallState
{
    /// <summary>No Glaude-owned entry of any kind is present.</summary>
    NotInstalled,

    /// <summary>Every expected entry is present, on the expected port, with no stray Glaude entries.</summary>
    Installed,

    /// <summary>All expected entries present, but at least one registers a different port.</summary>
    PortDrift,

    /// <summary>Some, but not all, expected entries are present (or stray Glaude entries exist).</summary>
    PartiallyInstalled,
}

/// <summary>Ownership state of one top-level status-line field.</summary>
public enum StatusLineOwnership
{
    /// <summary>Field absent.</summary>
    None,

    /// <summary>Field present but owned by another tool — must be captured before takeover.</summary>
    Foreign,

    /// <summary>Field present and Glaude-owned.</summary>
    Glaude,
}

/// <summary>A Glaude-owned hook entry found in the DOM.</summary>
public sealed record FoundHookEntry(string EventName, int GroupIndex, int EntryIndex, int? Port);

/// <summary>Full result of <see cref="SettingsMerger.Detect"/>.</summary>
public sealed class DetectionResult
{
    public required InstallState State { get; init; }

    /// <summary>Expected events that were found, with the port each currently registers.</summary>
    public required IReadOnlyDictionary<string, int?> FoundEvents { get; init; }

    /// <summary>Expected events with no Glaude entry.</summary>
    public required IReadOnlyList<string> MissingEvents { get; init; }

    /// <summary>Glaude-owned entries under events that are <i>not</i> in the expected set (stale installs).</summary>
    public required IReadOnlyList<string> StrayEvents { get; init; }

    public required StatusLineOwnership StatusLine { get; init; }

    public required int? StatusLinePort { get; init; }

    public required StatusLineOwnership SubagentStatusLine { get; init; }

    public required int? SubagentStatusLinePort { get; init; }

    /// <summary>Ports registered by Glaude entries that differ from the expected port.</summary>
    public required IReadOnlyList<int> DriftingPorts { get; init; }

    public bool AnyGlaudePresence =>
        FoundEvents.Count > 0 ||
        StrayEvents.Count > 0 ||
        StatusLine == StatusLineOwnership.Glaude ||
        SubagentStatusLine == StatusLineOwnership.Glaude;
}

/// <summary>Result of a full load-modify-save install pass.</summary>
public enum InstallOutcome
{
    /// <summary>Settings were not in a writable state (empty/malformed) — nothing was written.</summary>
    Refused,

    /// <summary>Already installed as expected — nothing was written.</summary>
    NoChange,

    /// <summary>Changes were applied and saved.</summary>
    Applied,
}

/// <summary>
/// Detect / Install / Uninstall of Glaude's settings.json entries.
///
/// Invariants (project.md "Install / detection / uninstall"):
///  - ownership is decided <b>per entry</b>, via the <c>X-Glaude-Hook</c> marker arg for hooks
///    and the command token for the status-line fields — never "Glaude owns the hooks object";
///  - a matcher group that is not ours is never modified, reordered or removed;
///  - removals prune empty containers: empty <c>hooks</c> array -> drop the group; empty event
///    array -> drop the event key; other events remain -> keep the top-level <c>hooks</c> object;
///  - Install is idempotent and rewrites only Glaude-owned entries.
/// </summary>
public static class SettingsMerger
{
    public static DetectionResult Detect(JsonNode? root, GlaudeHookSpec expected)
    {
        ArgumentNullException.ThrowIfNull(expected);

        var expectedNames = expected.EventNames.ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, int?>(StringComparer.Ordinal);
        var stray = new List<string>();
        var drifting = new List<int>();

        foreach (var entry in EnumerateGlaudeEntries(root as JsonObject))
        {
            if (expectedNames.Contains(entry.EventName))
            {
                // First occurrence wins for port reporting; duplicates are repaired by Install.
                if (!found.ContainsKey(entry.EventName))
                {
                    found[entry.EventName] = entry.Port;
                }
            }
            else if (!stray.Contains(entry.EventName))
            {
                stray.Add(entry.EventName);
            }

            if (entry.Port is int p && p != expected.Port)
            {
                drifting.Add(p);
            }
        }

        var missing = expected.EventNames.Where(n => !found.ContainsKey(n)).ToArray();

        var (statusLine, statusLinePort) =
            InspectStatusLine(root as JsonObject, StatusLineField.StatusLine);
        var (subagentStatusLine, subagentPort) =
            InspectStatusLine(root as JsonObject, StatusLineField.SubagentStatusLine);

        if (statusLine == StatusLineOwnership.Glaude && statusLinePort is int slp && slp != expected.Port)
        {
            drifting.Add(slp);
        }

        if (subagentStatusLine == StatusLineOwnership.Glaude && subagentPort is int sslp && sslp != expected.Port)
        {
            drifting.Add(sslp);
        }

        var missingList = new List<string>(missing);
        if (statusLine != StatusLineOwnership.Glaude)
        {
            missingList.Add(GlaudeHookSpec.StatusLineField);
        }

        if (expected.IncludeSubagentStatusLine && subagentStatusLine != StatusLineOwnership.Glaude)
        {
            missingList.Add(GlaudeHookSpec.SubagentStatusLineField);
        }

        var anyPresence =
            found.Count > 0 ||
            stray.Count > 0 ||
            statusLine == StatusLineOwnership.Glaude ||
            subagentStatusLine == StatusLineOwnership.Glaude;

        InstallState state;
        if (!anyPresence)
        {
            state = InstallState.NotInstalled;
        }
        else if (missingList.Count > 0 || stray.Count > 0)
        {
            state = InstallState.PartiallyInstalled;
        }
        else if (drifting.Count > 0)
        {
            state = InstallState.PortDrift;
        }
        else
        {
            state = InstallState.Installed;
        }

        return new DetectionResult
        {
            State = state,
            FoundEvents = found,
            MissingEvents = missing,
            StrayEvents = stray,
            StatusLine = statusLine,
            StatusLinePort = statusLinePort,
            SubagentStatusLine = subagentStatusLine,
            SubagentStatusLinePort = subagentPort,
            DriftingPorts = drifting,
        };
    }

    /// <summary>
    /// Adds/repairs Glaude's entries in place. Returns true if the DOM changed.
    /// Existing non-Glaude matcher groups are never touched; Glaude's own entries are rewritten
    /// in place (preserving their position) when already present.
    /// </summary>
    public static bool Install(JsonNode root, GlaudeHookSpec expected, IStatusLineChainStore store)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(store);

        if (root is not JsonObject obj)
        {
            throw new ArgumentException("settings root must be a JSON object", nameof(root));
        }

        var before = SettingsFile.Serialize(obj);

        var expectedNames = expected.EventNames.ToHashSet(StringComparer.Ordinal);

        // 1. Drop Glaude entries under events we no longer expect (e.g. a version-gated
        //    SubagentStart that is no longer supported). Only our own entries are removed.
        RemoveGlaudeEntries(obj, eventName => !expectedNames.Contains(eventName));

        // 2. Rewrite or append each expected event hook.
        foreach (var eventHook in expected.EventHooks)
        {
            InstallEventHook(obj, eventHook);
        }

        // 3. Top-level status-line fields (NOT part of `hooks`).
        StatusLineChain.Install(obj, StatusLineField.StatusLine, expected.BuildStatusLine(), store);

        if (expected.IncludeSubagentStatusLine)
        {
            StatusLineChain.Install(
                obj,
                StatusLineField.SubagentStatusLine,
                expected.BuildSubagentStatusLine(),
                store);
        }

        return !string.Equals(before, SettingsFile.Serialize(obj), StringComparison.Ordinal);
    }

    /// <summary>
    /// Removes every Glaude-tagged hook entry and restores the captured status-line fields.
    /// Returns true if the DOM changed.
    /// </summary>
    public static bool Uninstall(JsonNode root, IStatusLineChainStore store)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(store);

        if (root is not JsonObject obj)
        {
            throw new ArgumentException("settings root must be a JSON object", nameof(root));
        }

        var before = SettingsFile.Serialize(obj);

        RemoveGlaudeEntries(obj, _ => true);

        StatusLineChain.Uninstall(obj, StatusLineField.StatusLine, store);
        StatusLineChain.Uninstall(obj, StatusLineField.SubagentStatusLine, store);

        return !string.Equals(before, SettingsFile.Serialize(obj), StringComparison.Ordinal);
    }

    /// <summary>
    /// Full load-guard + install + atomic save. Refuses on empty/malformed settings rather than
    /// overwriting; creates the file only when it is absent entirely.
    /// </summary>
    public static InstallOutcome InstallInto(SettingsFile file, GlaudeHookSpec expected, IStatusLineChainStore store)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (!file.IsWritableForInstall)
        {
            return InstallOutcome.Refused;
        }

        var root = file.Root ?? new JsonObject();

        if (!Install(root, expected, store))
        {
            return InstallOutcome.NoChange;
        }

        file.Save(root);
        return InstallOutcome.Applied;
    }

    // ---- internals -------------------------------------------------------------------

    private static (StatusLineOwnership Ownership, int? Port) InspectStatusLine(JsonObject? root, StatusLineField field)
    {
        var node = root?[StatusLineChain.FieldName(field)];
        if (node is null)
        {
            return (StatusLineOwnership.None, null);
        }

        if (!StatusLineChain.IsGlaudeOwned(field, node))
        {
            return (StatusLineOwnership.Foreign, null);
        }

        return (StatusLineOwnership.Glaude, GlaudeHookSpec.ExtractPortFromCommand(GlaudeHookSpec.GetCommand(node)));
    }

    /// <summary>Walks every Glaude-marked hook entry in the DOM, in document order.</summary>
    public static IEnumerable<FoundHookEntry> EnumerateGlaudeEntries(JsonObject? root)
    {
        if (root?[GlaudeHookSpec.HooksField] is not JsonObject hooks)
        {
            yield break;
        }

        foreach (var (eventName, eventValue) in hooks)
        {
            if (eventValue is not JsonArray groups)
            {
                continue;
            }

            for (var gi = 0; gi < groups.Count; gi++)
            {
                if (groups[gi] is not JsonObject group || group["hooks"] is not JsonArray entries)
                {
                    continue;
                }

                for (var ei = 0; ei < entries.Count; ei++)
                {
                    if (!HookEntry.IsGlaudeOwned(entries[ei]))
                    {
                        continue;
                    }

                    yield return new FoundHookEntry(
                        eventName,
                        gi,
                        ei,
                        HookEntry.GetRegisteredPort(entries[ei]));
                }
            }
        }
    }

    private static void InstallEventHook(JsonObject root, GlaudeEventHook eventHook)
    {
        var hooks = root[GlaudeHookSpec.HooksField] as JsonObject;
        if (hooks is null)
        {
            hooks = new JsonObject();
            root[GlaudeHookSpec.HooksField] = hooks;
        }

        if (hooks[eventHook.EventName] is not JsonArray groups)
        {
            groups = new JsonArray();
            hooks[eventHook.EventName] = groups;
        }

        var desiredEntry = eventHook.Group.Hooks[0].ToJson();

        // Positions of our own entries, in document order.
        var positions = new List<(int GroupIndex, int EntryIndex)>();
        for (var gi = 0; gi < groups.Count; gi++)
        {
            if (groups[gi] is not JsonObject group || group["hooks"] is not JsonArray entries)
            {
                continue;
            }

            for (var ei = 0; ei < entries.Count; ei++)
            {
                if (HookEntry.IsGlaudeOwned(entries[ei]))
                {
                    positions.Add((gi, ei));
                }
            }
        }

        if (positions.Count == 0)
        {
            // Not present -> append as an ADDITIONAL matcher group, never replacing existing ones.
            groups.Add(eventHook.Group.ToJson());
            return;
        }

        // Duplicates of ours (beyond the first) are dropped, back to front so indices stay valid.
        for (var i = positions.Count - 1; i >= 1; i--)
        {
            var (gi, ei) = positions[i];
            var entries = (JsonArray)((JsonObject)groups[gi]!)["hooks"]!;
            entries.RemoveAt(ei);

            // Prune only containers WE emptied.
            if (entries.Count == 0)
            {
                groups.RemoveAt(gi);
            }
        }

        // Rewrite the surviving one in place: preserves its position and its group's matcher,
        // and leaves any third-party sibling entries in that group untouched.
        var (fgi, fei) = positions[0];
        var firstEntries = (JsonArray)((JsonObject)groups[fgi]!)["hooks"]!;
        firstEntries.RemoveAt(fei);
        firstEntries.Insert(fei, desiredEntry);
    }

    private static void RemoveGlaudeEntries(JsonObject root, Func<string, bool> eventFilter)
    {
        if (root[GlaudeHookSpec.HooksField] is not JsonObject hooks)
        {
            return;
        }

        foreach (var eventName in hooks.Select(kv => kv.Key).ToList())
        {
            if (!eventFilter(eventName))
            {
                continue;
            }

            if (hooks[eventName] is not JsonArray groups)
            {
                continue;
            }

            for (var gi = groups.Count - 1; gi >= 0; gi--)
            {
                if (groups[gi] is not JsonObject group || group["hooks"] is not JsonArray entries)
                {
                    continue;
                }

                for (var ei = entries.Count - 1; ei >= 0; ei--)
                {
                    if (HookEntry.IsGlaudeOwned(entries[ei]))
                    {
                        entries.RemoveAt(ei);
                    }
                }

                // Prune: drop the {matcher, hooks} group when its hooks array is now empty.
                if (entries.Count == 0)
                {
                    groups.RemoveAt(gi);
                }
            }

            // Prune: drop the event key when its array is now empty.
            if (groups.Count == 0)
            {
                hooks.Remove(eventName);
            }
        }

        // Only drop the top-level `hooks` object when NO events remain at all.
        if (hooks.Count == 0)
        {
            root.Remove(GlaudeHookSpec.HooksField);
        }
    }
}
