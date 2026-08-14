namespace Glaude.Cli;

using System;
using System.IO;
using Microsoft.Win32;

/// <summary>Whether `claude` resolved to a native executable, an npm-style shim, or nothing.</summary>
public enum ClaudeResolutionKind
{
    Missing,
    NativeExe,
    Shim,
}

public sealed record ClaudeResolution(ClaudeResolutionKind Kind, string? Path);

public enum WebView2RuntimeStatus
{
    NotFound,
    Found,
}

public sealed record WebView2Probe(WebView2RuntimeStatus Status, string? Version);

/// <summary>
/// `glaude doctor` — Phase 0 pre-flight checks for the WPF/ConPTY refactor. Verifies the two
/// load-bearing assumptions the rest of the plan is built on: that `claude` resolves to a native
/// executable rather than a `.cmd`/`.ps1` npm shim (the ConPTY launch path in Phase 2 assumes it
/// can attach directly), and that the WebView2 Evergreen runtime is present (Phase 2's terminal
/// host needs it). Never throws — every probe degrades to a reported failure, not a crash.
///
/// Both checks only describe *this* machine — re-run on every deployment target before shipping,
/// per the locked-in architecture decisions.
/// </summary>
public static class DoctorCommand
{
    private static readonly string[] ShimExtensions = { ".cmd", ".bat", ".ps1" };

    /// <summary>Convenience entry point for the CLI: real PATH, real filesystem, real registry.</summary>
    public static int Run(TextWriter output) =>
        Run(output, Environment.GetEnvironmentVariable("PATH") ?? string.Empty, File.Exists, ProbeWebView2Runtime);

    /// <summary>Test seam: explicit PATH string, file-existence probe, and WebView2 probe.</summary>
    public static int Run(TextWriter output, string pathEnv, Func<string, bool> fileExists, Func<WebView2Probe> webView2Probe)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(webView2Probe);

        output.WriteLine("Glaude doctor — pre-flight checks");
        output.WriteLine();

        var claudeOk = PrintClaudeResolution(output, ResolveClaude(pathEnv, fileExists));
        var webView2Ok = PrintWebView2Probe(output, webView2Probe());

        output.WriteLine();
        output.WriteLine("These checks only describe this machine — re-run on every deployment target before shipping.");

        if (claudeOk && webView2Ok)
        {
            output.WriteLine("All checks passed.");
            return 0;
        }

        output.WriteLine("One or more checks failed — see above.");
        return 1;
    }

    /// <summary>
    /// Resolves `claude` the way Windows' own PATH search does: walks each PATH entry in order
    /// and, within a directory, prefers a native `claude.exe` over a `.cmd`/`.bat`/`.ps1` shim —
    /// then returns the first directory with any match at all, rather than the best match across
    /// the whole PATH (mirroring `where`/`Get-Command -All`'s first-hit semantics).
    /// </summary>
    public static ClaudeResolution ResolveClaude(string pathEnv, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);

        foreach (var rawDir in (pathEnv ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = rawDir.Trim();
            if (dir.Length == 0)
            {
                continue;
            }

            var exePath = Path.Combine(dir, "claude.exe");
            if (fileExists(exePath))
            {
                return new ClaudeResolution(ClaudeResolutionKind.NativeExe, exePath);
            }

            foreach (var ext in ShimExtensions)
            {
                var shimPath = Path.Combine(dir, "claude" + ext);
                if (fileExists(shimPath))
                {
                    return new ClaudeResolution(ClaudeResolutionKind.Shim, shimPath);
                }
            }
        }

        return new ClaudeResolution(ClaudeResolutionKind.Missing, null);
    }

    private static bool PrintClaudeResolution(TextWriter output, ClaudeResolution resolution)
    {
        switch (resolution.Kind)
        {
            case ClaudeResolutionKind.NativeExe:
                output.WriteLine($"[OK]   claude resolves to a native executable: {resolution.Path}");
                return true;

            case ClaudeResolutionKind.Shim:
                output.WriteLine($"[FAIL] claude resolves to a shim, not a native exe: {resolution.Path}");
                output.WriteLine("       The Phase 2 ConPTY launch path assumes CreateProcess attaches directly to");
                output.WriteLine("       claude.exe. A shim means it must spawn node.exe + the JS entry instead —");
                output.WriteLine("       re-check locked-in decision 2 before starting Phase 2 on this machine.");
                return false;

            default:
                output.WriteLine("[FAIL] claude was not found on PATH.");
                return false;
        }
    }

    /// <summary>
    /// Reads the WebView2 Evergreen runtime's registered version from the registry — the same
    /// detection surface the WebView2 installer itself publishes — rather than taking a
    /// dependency on the WebView2 SDK NuGet package just for this diagnostic (that package lands
    /// with the real terminal host in P2-T5). Checks the per-machine key first, then per-user.
    /// </summary>
    public static WebView2Probe ProbeWebView2Runtime()
    {
        const string ClientId = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

        var machineVersion =
            ReadRegistryPv(Registry.LocalMachine, $@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{ClientId}") ??
            ReadRegistryPv(Registry.LocalMachine, $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{ClientId}");
        if (machineVersion is not null)
        {
            return new WebView2Probe(WebView2RuntimeStatus.Found, machineVersion);
        }

        var userVersion = ReadRegistryPv(Registry.CurrentUser, $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{ClientId}");
        return userVersion is not null
            ? new WebView2Probe(WebView2RuntimeStatus.Found, userVersion)
            : new WebView2Probe(WebView2RuntimeStatus.NotFound, null);
    }

    private static string? ReadRegistryPv(RegistryKey root, string subKeyPath)
    {
        try
        {
            using var key = root.OpenSubKey(subKeyPath);
            return key?.GetValue("pv") as string;
        }
        catch
        {
            return null;
        }
    }

    private static bool PrintWebView2Probe(TextWriter output, WebView2Probe probe)
    {
        if (probe.Status == WebView2RuntimeStatus.Found)
        {
            output.WriteLine($"[OK]   WebView2 Evergreen runtime present: {probe.Version}");
            return true;
        }

        output.WriteLine("[FAIL] WebView2 Evergreen runtime not found on this machine.");
        output.WriteLine("       Phase 2's terminal host (P2-T5) needs it — either confirm it will be present on");
        output.WriteLine("       every deployment target, or plan to bundle the fixed-version runtime instead.");
        return false;
    }
}
