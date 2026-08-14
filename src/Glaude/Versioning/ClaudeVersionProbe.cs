namespace Glaude.Versioning;

using System.Diagnostics;

/// <summary>
/// Probes the installed Claude Code version by running `claude --version`.
/// Never throws; returns null if claude isn't available or the version is unparseable.
/// </summary>
public static class ClaudeVersionProbe
{
    /// <summary>
    /// Gets the currently installed Claude Code version.
    /// </summary>
    /// <returns>The parsed version, or null if claude is unavailable or the version is unparseable</returns>
    public static ClaudeVersion? GetInstalledVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "claude",
                Arguments = "--version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using (var process = Process.Start(psi))
            {
                if (process == null)
                    return null;

                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                    return null;

                var versionString = output.Trim();
                if (ClaudeVersion.TryParse(versionString, out var version))
                    return version;

                return null;
            }
        }
        catch
        {
            return null;
        }
    }
}
