namespace Accel.Cli;

using System;
using Accel.Settings;

/// <summary>The verb selected on the command line. <see cref="Unknown"/> is a distinct state
/// so the caller can print a usage error without crashing.
///
/// Post-combined-app refactor: the only "verbs" left are the internal ones Claude Code itself
/// invokes as short-lived child processes (<see cref="StatusLine"/>/<see cref="SubagentStatusLine"/>/
/// <see cref="Notify"/>), plus the user-facing <see cref="Doctor"/> diagnostic — everything else
/// (install + server + UI) now happens under the single default <see cref="Start"/> verb, selected
/// whenever no recognised verb token is the first argument (including no arguments at all).</summary>
public enum Verb
{
    Start,
    StatusLine,
    SubagentStatusLine,
    Notify,
    Doctor,
    Unknown,
}

/// <summary>
/// Result of parsing argv: which verb was selected plus its options. Deliberately separate
/// from actually *executing* the verb, so dispatch resolution is unit-testable without starting
/// a real server or touching a real settings.json.
/// </summary>
public sealed class ParsedCommand
{
    public required Verb Verb { get; init; }

    public required int Port { get; init; }

    /// <summary>The `--uninstall` flag: run <c>UninstallCommand</c> and exit immediately instead
    /// of starting the server/UI. Only meaningful when <see cref="Verb"/> is <see cref="Verb.Start"/>.</summary>
    public bool Uninstall { get; init; }

    /// <summary>Hidden/internal debug flag preserved from the old `run --dump-raw &lt;dir&gt;`
    /// surface: raw payload capture on the combined-start path. Not part of the documented
    /// CLI surface (see project plan) but kept since it costs nothing to retain.</summary>
    public string? DumpRawDir { get; init; }

    /// <summary>`--verbose`: opts a regular <see cref="Verb.Start"/> run into the diagnostic
    /// console output normal runs no longer print (per-event lifecycle lines, the full install
    /// summary) - see <c>Program.cs</c>'s <c>RunCombinedAsync</c>. Off by default: a regular
    /// launch stays silent except for errors and startup-relevant facts (listening port, a
    /// refused install, a repaired port drift).</summary>
    public bool Verbose { get; init; }

    /// <summary>The raw, unrecognised token — only set when <see cref="Verb"/> is <see cref="Verb.Unknown"/>.</summary>
    public string? UnknownVerbText { get; init; }

    /// <summary>`--route &lt;path&gt;`: the event route to POST to. Only meaningful when
    /// <see cref="Verb"/> is <see cref="Verb.Notify"/>.</summary>
    public string? Route { get; init; }
}

/// <summary>
/// Hand-rolled argv parser — no framework. The user-facing surface is deliberately minimal:
/// no verb at all (the default combined install+server+UI start), plus `--port &lt;n&gt;` and/or
/// `--uninstall` in any order. `statusline`/`subagent-statusline` remain as the first positional
/// token exactly as before, since Claude Code itself shells out to those two sub-commands.
/// </summary>
public static class ArgParser
{
    private const string PortFlag = "--port";
    private const string UninstallFlag = "--uninstall";
    private const string DumpRawFlag = "--dump-raw";
    private const string VerboseFlag = "--verbose";
    private const string RouteFlag = "--route";

    /// <summary>Parses argv. Never throws — an unparseable `--port` value is ignored (default kept).</summary>
    public static ParsedCommand Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var verb = Verb.Start;
        string? unknownText = null;
        var i = 0;

        if (args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            (verb, unknownText) = ParseVerb(args[0]);
            i = 1;
        }

        var port = AccelHookSpec.DefaultPort;
        var uninstall = false;
        var verbose = false;
        string? dumpRawDir = null;
        string? route = null;

        for (; i < args.Length; i++)
        {
            if (string.Equals(args[i], PortFlag, StringComparison.Ordinal) && i + 1 < args.Length)
            {
                if (int.TryParse(args[i + 1], out var parsedPort))
                {
                    port = parsedPort;
                }

                i++;
            }
            else if (string.Equals(args[i], UninstallFlag, StringComparison.Ordinal))
            {
                uninstall = true;
            }
            else if (string.Equals(args[i], VerboseFlag, StringComparison.Ordinal))
            {
                verbose = true;
            }
            else if (string.Equals(args[i], DumpRawFlag, StringComparison.Ordinal) && i + 1 < args.Length)
            {
                dumpRawDir = args[i + 1];
                i++;
            }
            else if (string.Equals(args[i], RouteFlag, StringComparison.Ordinal) && i + 1 < args.Length)
            {
                route = args[i + 1];
                i++;
            }
        }

        return new ParsedCommand
        {
            Verb = verb,
            Port = port,
            Uninstall = uninstall,
            DumpRawDir = dumpRawDir,
            Verbose = verbose,
            UnknownVerbText = unknownText,
            Route = route,
        };
    }

    private static (Verb Verb, string? UnknownText) ParseVerb(string token) => token switch
    {
        "statusline" => (Verb.StatusLine, null),
        "subagent-statusline" => (Verb.SubagentStatusLine, null),
        "notify" => (Verb.Notify, null),
        "doctor" => (Verb.Doctor, null),
        _ => (Verb.Unknown, token),
    };
}
