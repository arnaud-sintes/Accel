namespace Glaude.Orchestration;

using System;
using System.IO;

/// <summary>Whether `claude` resolved to a native executable, an npm-style shim, or nothing.</summary>
public enum ClaudeCliResolutionKind
{
    Missing,
    NativeExe,
    Shim,
}

public sealed record ClaudeCliResolution(ClaudeCliResolutionKind Kind, string? Path);

/// <summary>
/// Resolves the path to `claude` for the ConPTY launch path (Phase 2). This is a distinct
/// consumer from <see cref="Glaude.Cli.DoctorCommand"/>'s `ResolveClaude`, which is a
/// diagnostic-only probe run once by `glaude doctor`; this class backs an actual process launch.
///
/// Claude Code self-updates by replacing its own binary in place, so callers MUST re-resolve on
/// every launch attempt rather than caching the result across separate launches — a resolution
/// taken before a self-update can point at a file that no longer exists, or that has been
/// replaced with a different kind (e.g. exe swapped for a shim). Every method here is a pure,
/// stateless function of its inputs for exactly that reason: there is nothing to invalidate
/// because nothing is ever stored between calls.
///
/// Mirrors <see cref="Glaude.Cli.DoctorCommand.ResolveClaude"/>'s PATH-walking semantics: walks
/// each PATH entry in order and, within a directory, prefers a native `claude.exe` over a
/// `.cmd`/`.bat`/`.ps1` shim, returning the first directory with any match at all. A shim match
/// is reported distinctly (not coerced into success) because the ConPTY launch path assumes it
/// can `CreateProcess` a native exe directly.
/// </summary>
public static class ClaudeCliLocator
{
    private static readonly string[] ShimExtensions = { ".cmd", ".bat", ".ps1" };

    /// <summary>Convenience overload for production use: resolves against the real PATH and filesystem.</summary>
    public static ClaudeCliResolution Resolve() =>
        Resolve(Environment.GetEnvironmentVariable("PATH") ?? string.Empty, File.Exists);

    /// <summary>Test seam: explicit PATH string and file-existence probe. Never caches — call fresh on every launch.</summary>
    public static ClaudeCliResolution Resolve(string pathEnv, Func<string, bool> fileExists)
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
                return new ClaudeCliResolution(ClaudeCliResolutionKind.NativeExe, exePath);
            }

            foreach (var ext in ShimExtensions)
            {
                var shimPath = Path.Combine(dir, "claude" + ext);
                if (fileExists(shimPath))
                {
                    return new ClaudeCliResolution(ClaudeCliResolutionKind.Shim, shimPath);
                }
            }
        }

        return new ClaudeCliResolution(ClaudeCliResolutionKind.Missing, null);
    }
}
