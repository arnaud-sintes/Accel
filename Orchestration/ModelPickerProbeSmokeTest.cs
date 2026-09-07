namespace Accel.Orchestration;

using System;
using System.IO;
using System.Text;
using System.Threading;

/// <summary>
/// Hidden dev-only diagnostic for <see cref="ModelCatalogProbe"/>, reachable via the undocumented
/// <c>model-picker-probe</c> verb - same pattern and same rationale as <c>pty-session-smoke-test</c>
/// (<see cref="PtySessionSmokeTest"/>): what is being exercised here is a real `claude` process, a
/// real pseudoconsole, and a real TUI painting real escape sequences, none of which a unit test with
/// fakes can establish. The pure halves - <see cref="TerminalScreenBuffer"/>'s emulation and
/// <see cref="ModelPickerScreenParser"/>'s parsing - are unit-tested against captured screens
/// instead; this verb covers "does driving the live picker actually work on this machine".
///
/// <para>Unlike the other smoke tests in this folder it <i>does</i> launch <c>claude.exe</c>, because
/// Claude Code's own picker is the thing being read. It never submits a prompt, so it bills no API
/// call, and it writes only arrow keys and Escape, so it cannot commit a model change - see
/// <see cref="ModelCatalogProbe"/>'s remarks.</para>
///
/// <para>Diagnostic only: it prints what discovery found and does <b>not</b> write the catalog cache,
/// so running it can never change what the app subsequently offers. Pass a directory as the first
/// argument to probe somewhere other than the current one; it must be a directory Claude Code already
/// trusts.</para>
/// </summary>
public static class ModelPickerProbeSmokeTest
{
    /// <summary>Runs one discovery attempt and reports it. Returns 0 when a catalog was recovered, 1
    /// otherwise - "1" means "discovery did not work here", which is a legitimate answer (the app
    /// falls back to its built-in list), not a broken test.</summary>
    public static int Run(TextWriter output, string? workingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(output);

        // The picker is drawn with box-drawing and arrow glyphs the console's default codepage turns
        // into question marks, and an unreadable report defeats the point of a diagnostic.
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (IOException)
        {
            // No console attached (redirected launch) - nothing to configure.
        }

        var directory = string.IsNullOrWhiteSpace(workingDirectory)
            ? Environment.CurrentDirectory
            : workingDirectory;

        output.WriteLine($"model-picker-probe: driving Claude Code's /model picker in {directory}");
        output.WriteLine("model-picker-probe: no prompt is submitted (no API cost); only arrow keys and Escape are sent.");
        output.WriteLine();

        var probe = new ModelCatalogProbe(trace: message => output.WriteLine($"  {message}"));

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var result = probe.DiscoverAsync(directory, cancellation.Token).GetAwaiter().GetResult();

        output.WriteLine();
        output.WriteLine($"  outcome : {result.Outcome}");
        output.WriteLine($"  elapsed : {result.Elapsed.TotalSeconds:F1}s");
        if (result.Detail is not null)
        {
            output.WriteLine($"  detail  : {result.Detail}");
        }

        if (result.Catalog is { } catalog)
        {
            output.WriteLine($"  CLI     : {catalog.ClaudeCliVersion ?? "(not reported)"}");
            output.WriteLine();
            foreach (var entry in catalog.Entries)
            {
                var tiers = entry.EffortTiers.Count == 0
                    ? "no effort control"
                    : $"{entry.EffortTiers.Count} tiers: {string.Join(" < ", entry.EffortTiers)}";
                output.WriteLine($"    --model {entry.CliValue,-10} | {entry.DisplayName,-24} | {tiers}");
            }
        }

        output.WriteLine();
        output.WriteLine(result.Succeeded
            ? "model-picker-probe: DISCOVERED (not cached - this verb never writes the catalog cache)"
            : "model-picker-probe: NOT DISCOVERED - the app would fall back to its built-in catalog");
        return result.Succeeded ? 0 : 1;
    }
}
