namespace Glaude.Settings;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

/// <summary>One expected Glaude-owned event hook: which settings.json event key it lives under,
/// and the matcher group to install there.</summary>
public sealed record GlaudeEventHook(string EventName, string Route, HookMatcherGroup Group);

/// <summary>
/// The complete set of Glaude-owned settings.json entries expected for a given
/// (port, exePath) pair, plus the ownership/port predicates used to recognise them again.
///
/// Two independent mechanisms are modelled here and must never be conflated
/// (project.md "Hook invocation contract"):
///  1. <c>hooks</c> event entries (exec-form curl POSTs, marker-tagged);
///  2. the <c>statusLine</c> / <c>subagentStatusLine</c> <b>top-level</b> fields.
/// </summary>
public sealed class GlaudeHookSpec
{
    public const int DefaultPort = 40010;

    /// <summary>CLI verb Glaude registers as the <c>statusLine</c> command.</summary>
    public const string StatusLineVerb = "statusline";

    /// <summary>CLI verb Glaude registers as the <c>subagentStatusLine</c> command.</summary>
    public const string SubagentStatusLineVerb = "subagent-statusline";

    public const string StatusLineField = "statusLine";
    public const string SubagentStatusLineField = "subagentStatusLine";

    public const string HooksField = "hooks";

    private const string Matcher = "*";
    private const string CurlExe = "curl.exe";
    private const string MaxTimeSeconds = "2";

    public GlaudeHookSpec(
        int port,
        string exePath,
        bool includeSubagentStart = true,
        bool includeSubagentStatusLine = true,
        int statusLineRefreshInterval = 5)
    {
        if (port <= 0 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        Port = port;
        ExePath = exePath ?? throw new ArgumentNullException(nameof(exePath));
        IncludeSubagentStart = includeSubagentStart;
        IncludeSubagentStatusLine = includeSubagentStatusLine;
        StatusLineRefreshInterval = Math.Max(1, statusLineRefreshInterval);
    }

    public int Port { get; }

    public string ExePath { get; }

    /// <summary>Version-gated (project.md: confirm the running Claude Code emits SubagentStart).</summary>
    public bool IncludeSubagentStart { get; }

    /// <summary>Version-gated (v2.1.205 / v2.1.214).</summary>
    public bool IncludeSubagentStatusLine { get; }

    /// <summary>Minimum 1 — without it, no status-line event arrives while the session waits on subagents.</summary>
    public int StatusLineRefreshInterval { get; }

    /// <summary>Expected event hooks, in install order.</summary>
    public IReadOnlyList<GlaudeEventHook> EventHooks => BuildEventHooks();

    public IReadOnlyList<string> EventNames => EventHooks.Select(e => e.EventName).ToArray();

    private IReadOnlyList<GlaudeEventHook> BuildEventHooks()
    {
        var list = new List<GlaudeEventHook>
        {
            // SessionStart / SubagentStart / SubagentStop: plain synchronous exec-form curl.
            MakeEventHook("SessionStart", "/events/session-start", timeout: 5, async: null),
        };

        // SessionEnd hooks share a ~1.5 s budget -> must be async with a short timeout.
        list.Add(MakeEventHook("SessionEnd", "/events/session-end", timeout: 2, async: true));

        if (IncludeSubagentStart)
        {
            list.Add(MakeEventHook("SubagentStart", "/events/subagent-start", timeout: 5, async: null));
        }

        list.Add(MakeEventHook("SubagentStop", "/events/subagent-stop", timeout: 5, async: null));

        return list;
    }

    private GlaudeEventHook MakeEventHook(string eventName, string route, int timeout, bool? async)
    {
        var url = BuildUrl(Port, route);

        var entry = new HookEntry
        {
            Type = "command",
            Command = CurlExe,
            Args = new[]
            {
                // -s -o NUL: never emit stdout; a stray response body could be mis-parsed
                // as a hook control object.
                "-s", "-o", "NUL", "--max-time", MaxTimeSeconds,
                "-X", "POST",
                url,
                "-H", "Content-Type: application/json",
                "-H", $"{HookEntry.MarkerHeaderPrefix} {eventName}",
                "-d", "@-",
            },
            Timeout = timeout,
            Async = async,
            StatusMessage = "glaude",
        };

        var group = new HookMatcherGroup { Matcher = Matcher, Hooks = new[] { entry } };
        return new GlaudeEventHook(eventName, route, group);
    }

    public static string BuildUrl(int port, string route) =>
        string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}{route}");

    /// <summary>
    /// The shell command string for the <c>statusLine</c> field. <c>statusLine</c> has no exec
    /// form, so Glaude installs *itself* (quoted exe path + verb + --port).
    /// </summary>
    public string StatusLineCommand => BuildSelfCommand(StatusLineVerb);

    public string SubagentStatusLineCommand => BuildSelfCommand(SubagentStatusLineVerb);

    private string BuildSelfCommand(string verb) =>
        string.Create(CultureInfo.InvariantCulture, $"\"{ExePath}\" {verb} --port {Port}");

    /// <summary>Expected value of the top-level <c>statusLine</c> field.</summary>
    public JsonObject BuildStatusLine() => new()
    {
        ["type"] = "command",
        ["command"] = StatusLineCommand,
        ["refreshInterval"] = StatusLineRefreshInterval,
    };

    /// <summary>Expected value of the top-level <c>subagentStatusLine</c> field.</summary>
    public JsonObject BuildSubagentStatusLine() => new()
    {
        ["type"] = "command",
        ["command"] = SubagentStatusLineCommand,
    };

    // ---- ownership / port recognition for the two top-level fields -------------------

    /// <summary>
    /// Ownership test for the top-level <c>statusLine</c> field, per project.md's marker
    /// scheme: the command string contains the token <c>glaude</c> AND <c>statusline --port</c>.
    /// </summary>
    public static bool IsGlaudeStatusLineCommand(string? command) =>
        ContainsGlaude(command) && command!.Contains($"{StatusLineVerb} --port", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ownership test for the top-level <c>subagentStatusLine</c> field. Note the more specific
    /// verb: "subagent-statusline --port" also contains "statusline --port" as a substring, so
    /// each predicate is only ever applied to its own field.
    /// </summary>
    public static bool IsGlaudeSubagentStatusLineCommand(string? command) =>
        ContainsGlaude(command) &&
        command!.Contains($"{SubagentStatusLineVerb} --port", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsGlaude(string? command) =>
        !string.IsNullOrEmpty(command) && command.Contains("glaude", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the <c>--port N</c> value out of a status-line command string.</summary>
    public static int? ExtractPortFromCommand(string? command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return null;
        }

        var idx = command.IndexOf("--port", StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var rest = command[(idx + "--port".Length)..].TrimStart(' ', '\t', '=');
        var end = 0;
        while (end < rest.Length && char.IsDigit(rest[end]))
        {
            end++;
        }

        if (end == 0)
        {
            return null;
        }

        return int.TryParse(rest[..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            ? port
            : null;
    }

    /// <summary>Gets the <c>command</c> string of a top-level status-line-shaped node, if any.</summary>
    public static string? GetCommand(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            return null;
        }

        return obj["command"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }
}
