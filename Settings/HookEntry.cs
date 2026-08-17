namespace Accel.Settings;

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

/// <summary>
/// One hook command entry as it appears inside a matcher group's <c>hooks</c> array.
///
/// Accel always uses <b>exec form</b> (<c>command</c> = the executable, <c>args</c> = an
/// argv array) rather than shell form, per project.md "Command execution form": this removes
/// shell-quoting ambiguity, sh-vs-PowerShell divergence and JSON-injection hazards.
/// </summary>
public sealed class HookEntry
{
    /// <summary>Header arg prefix that marks an entry as Accel-owned (project.md "Marker scheme").</summary>
    public const string MarkerHeaderPrefix = "X-Accel-Hook:";

    public string Type { get; init; } = "command";

    /// <summary>The executable to spawn (exec form), e.g. <c>curl.exe</c>.</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>argv for the executable. Never a shell string.</summary>
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    public int? Timeout { get; init; }

    public bool? Async { get; init; }

    public string? StatusMessage { get; init; }

    /// <summary>Builds the JSON object for this entry. Key order is stable.</summary>
    public JsonObject ToJson()
    {
        var obj = new JsonObject
        {
            ["type"] = Type,
            ["command"] = Command,
        };

        var args = new JsonArray();
        foreach (var a in Args)
        {
            args.Add(JsonValue.Create(a));
        }

        obj["args"] = args;

        if (Timeout.HasValue)
        {
            obj["timeout"] = Timeout.Value;
        }

        if (Async.HasValue)
        {
            obj["async"] = Async.Value;
        }

        if (StatusMessage is not null)
        {
            obj["statusMessage"] = StatusMessage;
        }

        return obj;
    }

    /// <summary>
    /// Returns the event name carried by this entry's Accel marker header arg
    /// (<c>-H "X-Accel-Hook: SessionStart"</c>), or null if the entry is not Accel-owned.
    /// This is the only ownership test — never assume Accel owns a whole event or the
    /// whole <c>hooks</c> object.
    /// </summary>
    public static string? GetAccelMarkerEvent(JsonNode? entryNode)
    {
        if (entryNode is not JsonObject entry)
        {
            return null;
        }

        if (entry["args"] is not JsonArray args)
        {
            return null;
        }

        foreach (var arg in args)
        {
            if (arg is not JsonValue v || !v.TryGetValue<string>(out var s) || s is null)
            {
                continue;
            }

            var trimmed = s.Trim();
            if (!trimmed.StartsWith(MarkerHeaderPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var eventName = trimmed[MarkerHeaderPrefix.Length..].Trim();
            return eventName.Length == 0 ? null : eventName;
        }

        return null;
    }

    /// <summary>True if this entry carries the Accel marker header arg.</summary>
    public static bool IsAccelOwned(JsonNode? entryNode) => GetAccelMarkerEvent(entryNode) is not null;

    /// <summary>
    /// Extracts the port from the loopback URL registered in this entry's args
    /// (used for port-drift detection). Null if no parseable URL is present.
    /// </summary>
    public static int? GetRegisteredPort(JsonNode? entryNode)
    {
        if (entryNode is not JsonObject entry || entry["args"] is not JsonArray args)
        {
            return null;
        }

        foreach (var arg in args)
        {
            if (arg is not JsonValue v || !v.TryGetValue<string>(out var s) || s is null)
            {
                continue;
            }

            if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.Port > 0)
            {
                return uri.Port;
            }
        }

        return null;
    }
}

/// <summary>
/// One <c>{ "matcher": ..., "hooks": [ ... ] }</c> group under an event key.
/// Accel always installs its own <i>additional</i> group; existing groups belonging to other
/// tools are never replaced or removed.
/// </summary>
public sealed class HookMatcherGroup
{
    public string Matcher { get; init; } = "*";

    public IReadOnlyList<HookEntry> Hooks { get; init; } = Array.Empty<HookEntry>();

    public JsonObject ToJson()
    {
        var entries = new JsonArray();
        foreach (var h in Hooks)
        {
            entries.Add(h.ToJson());
        }

        return new JsonObject
        {
            ["matcher"] = Matcher,
            ["hooks"] = entries,
        };
    }
}
