namespace Accel.Settings;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

/// <summary>One expected Accel-owned event hook: which settings.json event key it lives under,
/// and the matcher group to install there.</summary>
public sealed record AccelEventHook(string EventName, string Route, HookMatcherGroup Group);

/// <summary>
/// The complete set of Accel-owned settings.json entries expected for a given
/// (port, exePath) pair, plus the ownership/port predicates used to recognise them again.
///
/// Two independent mechanisms are modelled here and must never be conflated
/// (project.md "Hook invocation contract"):
///  1. <c>hooks</c> event entries (exec-form curl POSTs, marker-tagged);
///  2. the <c>statusLine</c> / <c>subagentStatusLine</c> <b>top-level</b> fields.
/// </summary>
public sealed class AccelHookSpec
{
    public const int DefaultPort = 40010;

    /// <summary>CLI verb Accel registers as the <c>statusLine</c> command.</summary>
    public const string StatusLineVerb = "statusline";

    /// <summary>CLI verb Accel registers as the <c>subagentStatusLine</c> command.</summary>
    public const string SubagentStatusLineVerb = "subagent-statusline";

    /// <summary>CLI verb Accel registers for the event hooks (SessionStart/SessionEnd/
    /// SubagentStart/SubagentStop) — see <c>Cli/NotifyCommand.cs</c>.</summary>
    public const string NotifyVerb = "notify";

    public const string StatusLineField = "statusLine";
    public const string SubagentStatusLineField = "subagentStatusLine";

    public const string HooksField = "hooks";

    private const string Matcher = "*";

    public AccelHookSpec(
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
    public IReadOnlyList<AccelEventHook> EventHooks => BuildEventHooks();

    public IReadOnlyList<string> EventNames => EventHooks.Select(e => e.EventName).ToArray();

    private IReadOnlyList<AccelEventHook> BuildEventHooks()
    {
        var list = new List<AccelEventHook>
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

        // PostToolUse fires on every tool call and must never block tool execution - async
        // with a short timeout, same rationale as SessionEnd above.
        list.Add(MakeEventHook("PostToolUse", "/events/post-tool-use", timeout: 5, async: true));

        // Stop fires once the main agent finishes a turn and is waiting on the user - drives
        // the "waiting for feedback" window-flash/row-highlight feature (SessionState.WaitingSinceUtc).
        // Async with a short timeout: Accel only observes this event, it must never hold up
        // Claude Code's own turn-completion.
        list.Add(MakeEventHook("Stop", "/events/stop", timeout: 5, async: true));

        return list;
    }

    private AccelEventHook MakeEventHook(string eventName, string route, int timeout, bool? async)
    {
        // Accel notifies itself (`accel.exe notify --route <route>`) rather than shelling out to
        // curl.exe: unlike curl, which exits non-zero (with no stderr, since `-s` silences it)
        // whenever Accel isn't listening yet, NotifyCommand swallows every failure itself, so a
        // Claude Code session started before the Accel app never surfaces a spurious hook error.
        var entry = new HookEntry
        {
            Type = "command",
            Command = ExePath,
            Args = new[]
            {
                NotifyVerb,
                "--port", Port.ToString(CultureInfo.InvariantCulture),
                "--route", route,
                "-H", $"{HookEntry.MarkerHeaderPrefix} {eventName}",
            },
            Timeout = timeout,
            Async = async,
            StatusMessage = "accel",
        };

        var group = new HookMatcherGroup { Matcher = Matcher, Hooks = new[] { entry } };
        return new AccelEventHook(eventName, route, group);
    }

    public static string BuildUrl(int port, string route) =>
        string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}{route}");

    /// <summary>
    /// The shell command string for the <c>statusLine</c> field. <c>statusLine</c> has no exec
    /// form, so Accel installs *itself* (quoted exe path + verb + --port).
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
    /// scheme: the command string contains the token <c>accel</c> AND <c>statusline --port</c>.
    /// </summary>
    public static bool IsAccelStatusLineCommand(string? command) =>
        ContainsAccel(command) && command!.Contains($"{StatusLineVerb} --port", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ownership test for the top-level <c>subagentStatusLine</c> field. Note the more specific
    /// verb: "subagent-statusline --port" also contains "statusline --port" as a substring, so
    /// each predicate is only ever applied to its own field.
    /// </summary>
    public static bool IsAccelSubagentStatusLineCommand(string? command) =>
        ContainsAccel(command) &&
        command!.Contains($"{SubagentStatusLineVerb} --port", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsAccel(string? command) =>
        !string.IsNullOrEmpty(command) && command.Contains("accel", StringComparison.OrdinalIgnoreCase);

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
