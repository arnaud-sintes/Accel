namespace Glaude.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Glaude.Cli;
using Glaude.Settings;
using Glaude.Versioning;
using Xunit;

/// <summary>
/// Phase 4 (minimal slice) tests: argv dispatch parsing, and the install/uninstall/status verbs
/// against fixture settings.json files in temp directories — the real
/// %USERPROFILE%\.claude\settings.json is never read or written.
/// </summary>
public class CliTests
{
    // ---- ArgParser: dispatch resolution -----------------------------------------------

    [Fact]
    public void Parse_NoArgs_DefaultsToStart()
    {
        var parsed = ArgParser.Parse(Array.Empty<string>());

        Assert.Equal(Verb.Start, parsed.Verb);
        Assert.Equal(GlaudeHookSpec.DefaultPort, parsed.Port);
        Assert.False(parsed.Uninstall);
        Assert.Null(parsed.DumpRawDir);
    }

    [Theory]
    [InlineData("statusline", Verb.StatusLine)]
    [InlineData("subagent-statusline", Verb.SubagentStatusLine)]
    [InlineData("doctor", Verb.Doctor)]
    public void Parse_InternalVerb_ResolvesToExpectedVerb(string token, Verb expected)
    {
        var parsed = ArgParser.Parse(new[] { token });
        Assert.Equal(expected, parsed.Verb);
    }

    [Fact]
    public void Parse_UnknownVerb_ReturnsUnknownWithoutThrowing()
    {
        var parsed = ArgParser.Parse(new[] { "frobnicate" });

        Assert.Equal(Verb.Unknown, parsed.Verb);
        Assert.Equal("frobnicate", parsed.UnknownVerbText);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("install")]
    [InlineData("ui")]
    [InlineData("sessions")]
    [InlineData("status")]
    public void Parse_RemovedLegacyVerbTokens_ResolveToUnknown(string token)
    {
        var parsed = ArgParser.Parse(new[] { token });

        Assert.Equal(Verb.Unknown, parsed.Verb);
        Assert.Equal(token, parsed.UnknownVerbText);
    }

    [Fact]
    public void Parse_PortFlag_OverridesDefault()
    {
        var parsed = ArgParser.Parse(new[] { "--port", "5050" });
        Assert.Equal(Verb.Start, parsed.Verb);
        Assert.Equal(5050, parsed.Port);
    }

    [Fact]
    public void Parse_UninstallFlag_SetsUninstallAndStaysStartVerb()
    {
        var parsed = ArgParser.Parse(new[] { "--uninstall" });

        Assert.Equal(Verb.Start, parsed.Verb);
        Assert.True(parsed.Uninstall);
    }

    [Fact]
    public void Parse_UninstallFlag_CombinedWithPort()
    {
        var parsed = ArgParser.Parse(new[] { "--port", "6060", "--uninstall" });

        Assert.Equal(Verb.Start, parsed.Verb);
        Assert.Equal(6060, parsed.Port);
        Assert.True(parsed.Uninstall);
    }

    [Fact]
    public void Parse_StatusLineVerb_WithPort()
    {
        var parsed = ArgParser.Parse(new[] { "statusline", "--port", "7070" });
        Assert.Equal(Verb.StatusLine, parsed.Verb);
        Assert.Equal(7070, parsed.Port);
    }

    [Fact]
    public void Parse_SubagentStatusLineVerb_WithPort()
    {
        var parsed = ArgParser.Parse(new[] { "subagent-statusline", "--port", "7070" });
        Assert.Equal(Verb.SubagentStatusLine, parsed.Verb);
        Assert.Equal(7070, parsed.Port);
    }

    [Fact]
    public void Parse_DumpRawFlag_OnDefaultStart()
    {
        var parsed = ArgParser.Parse(new[] { "--dump-raw", @"C:\temp\raw" });
        Assert.Equal(Verb.Start, parsed.Verb);
        Assert.Equal(@"C:\temp\raw", parsed.DumpRawDir);
    }

    [Fact]
    public void Parse_MalformedPortValue_KeepsDefaultRatherThanThrowing()
    {
        var parsed = ArgParser.Parse(new[] { "--port", "not-a-number" });
        Assert.Equal(GlaudeHookSpec.DefaultPort, parsed.Port);
    }

    // ---- install / uninstall against fixtures ------------------------------------------

    private const int Port = 40010;
    private const string ExePath = @"C:\tools\glaude\glaude.exe";

    private static ClaudeVersion? NoVersion() => null;

    [Fact]
    public void Install_EmptyFile_InstallsCleanly()
    {
        using var dir = new TempDir();
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var statePath = Path.Combine(dir.Path, "glaude-state.json");
        File.WriteAllText(settingsPath, "{}");

        var writer = new StringWriter();
        var exitCode = InstallCommand.Run(Port, settingsPath, ExePath, statePath, writer, NoVersion);

        Assert.Equal(0, exitCode);
        Assert.Contains("Installed Glaude", writer.ToString());

        var reloaded = SettingsFile.Load(settingsPath);
        var spec = new GlaudeHookSpec(Port, ExePath, includeSubagentStart: false, includeSubagentStatusLine: false);
        Assert.Equal(InstallState.Installed, SettingsMerger.Detect(reloaded.Root, spec).State);
    }

    [Fact]
    public void Install_AlreadyInstalled_IsNoOp()
    {
        using var dir = new TempDir();
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var statePath = Path.Combine(dir.Path, "glaude-state.json");
        File.WriteAllText(settingsPath, "{}");

        InstallCommand.Run(Port, settingsPath, ExePath, statePath, new StringWriter(), NoVersion);

        var secondWriter = new StringWriter();
        var exitCode = InstallCommand.Run(Port, settingsPath, ExePath, statePath, secondWriter, NoVersion);

        Assert.Equal(0, exitCode);
        Assert.Contains("Already installed", secondWriter.ToString());
    }

    [Fact]
    public void Install_MalformedFile_RefusesAndDoesNotWrite()
    {
        using var dir = new TempDir();
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var statePath = Path.Combine(dir.Path, "glaude-state.json");
        const string malformed = "{ \"hooks\": [ ";
        File.WriteAllText(settingsPath, malformed);

        var writer = new StringWriter();
        var exitCode = InstallCommand.Run(Port, settingsPath, ExePath, statePath, writer, NoVersion);

        Assert.Equal(1, exitCode);
        Assert.Contains("Refused", writer.ToString());
        Assert.Equal(malformed, File.ReadAllText(settingsPath));
        Assert.False(File.Exists(settingsPath + SettingsFile.BackupSuffix));
    }

    [Fact]
    public void Uninstall_RestoresCapturedThirdPartyStatusLine()
    {
        using var dir = new TempDir();
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var statePath = Path.Combine(dir.Path, "glaude-state.json");
        const string fixture = """
        {
          "statusLine": {
            "type": "command",
            "command": "node C:\\tools\\ccstatus\\statusline.js --fancy",
            "refreshInterval": 2
          }
        }
        """;
        File.WriteAllText(settingsPath, fixture);

        var installWriter = new StringWriter();
        InstallCommand.Run(Port, settingsPath, ExePath, statePath, installWriter, NoVersion);
        Assert.Contains("captured", installWriter.ToString());

        var uninstallWriter = new StringWriter();
        var exitCode = UninstallCommand.Run(settingsPath, statePath, uninstallWriter);

        Assert.Equal(0, exitCode);
        Assert.Contains("restored the pre-existing third-party command", uninstallWriter.ToString());

        var reloaded = SettingsFile.Load(settingsPath);
        var statusLine = reloaded.Root!["statusLine"]!;
        Assert.Contains("ccstatus", statusLine["command"]!.GetValue<string>());
    }

    [Fact]
    public void Uninstall_NoPriorInstall_ReportsNothingToDo()
    {
        using var dir = new TempDir();
        var settingsPath = Path.Combine(dir.Path, "settings.json");
        var statePath = Path.Combine(dir.Path, "glaude-state.json");
        File.WriteAllText(settingsPath, "{}");

        var writer = new StringWriter();
        var exitCode = UninstallCommand.Run(settingsPath, statePath, writer);

        Assert.Equal(0, exitCode);
        Assert.Contains("Nothing to uninstall", writer.ToString());
    }

    [Fact]
    public void Uninstall_MissingSettingsFile_ReportsNothingToDoWithoutCreatingIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"glaude-cli-test-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(dir, "settings.json");
        var statePath = Path.Combine(dir, "glaude-state.json");

        try
        {
            var writer = new StringWriter();
            var exitCode = UninstallCommand.Run(settingsPath, statePath, writer);

            Assert.Equal(0, exitCode);
            Assert.False(File.Exists(settingsPath));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ---- FileBackedStatusLineChainStore -------------------------------------------------

    [Fact]
    public void FileBackedStore_SaveThenLoad_RoundTripsCapture()
    {
        using var dir = new TempDir();
        var statePath = Path.Combine(dir.Path, "glaude-state.json");

        var original = new JsonObject { ["type"] = "command", ["command"] = "node statusline.js" };
        var capture = StatusLineCapture.Capture(original);

        var writerStore = new FileBackedStatusLineChainStore(statePath);
        writerStore.Save(StatusLineField.StatusLine, capture);

        var readerStore = new FileBackedStatusLineChainStore(statePath);
        var found = readerStore.TryGet(StatusLineField.StatusLine, out var loaded);

        Assert.True(found);
        Assert.True(loaded.HadOriginal);
        Assert.Equal("node statusline.js", loaded.Original!["command"]!.GetValue<string>());
    }

    [Fact]
    public void FileBackedStore_TwoFieldsAreIndependent()
    {
        using var dir = new TempDir();
        var statePath = Path.Combine(dir.Path, "glaude-state.json");
        var store = new FileBackedStatusLineChainStore(statePath);

        store.Save(StatusLineField.StatusLine, StatusLineCapture.Capture(new JsonObject { ["command"] = "main" }));
        store.Save(StatusLineField.SubagentStatusLine, StatusLineCapture.Capture(new JsonObject { ["command"] = "sub" }));

        Assert.True(store.TryGet(StatusLineField.StatusLine, out var main));
        Assert.True(store.TryGet(StatusLineField.SubagentStatusLine, out var sub));
        Assert.Equal("main", main.Original!["command"]!.GetValue<string>());
        Assert.Equal("sub", sub.Original!["command"]!.GetValue<string>());

        store.Remove(StatusLineField.StatusLine);
        Assert.False(store.TryGet(StatusLineField.StatusLine, out _));
        Assert.True(store.TryGet(StatusLineField.SubagentStatusLine, out _));
    }

    [Fact]
    public void FileBackedStore_MissingFile_ReturnsNoCaptureWithoutThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"glaude-state-missing-{Guid.NewGuid():N}.json");
        var store = new FileBackedStatusLineChainStore(path);

        var found = store.TryGet(StatusLineField.StatusLine, out var capture);

        Assert.False(found);
        Assert.False(capture.HadOriginal);
    }

    [Fact]
    public void FileBackedStore_MalformedFile_ReturnsNoCaptureWithoutThrowing()
    {
        using var dir = new TempDir();
        var statePath = Path.Combine(dir.Path, "glaude-state.json");
        File.WriteAllText(statePath, "{ this is not json ");

        var store = new FileBackedStatusLineChainStore(statePath);
        var found = store.TryGet(StatusLineField.StatusLine, out var capture);

        Assert.False(found);
        Assert.False(capture.HadOriginal);
    }

    [Fact]
    public void FileBackedStore_EmptyFile_ReturnsNoCaptureWithoutThrowing()
    {
        using var dir = new TempDir();
        var statePath = Path.Combine(dir.Path, "glaude-state.json");
        File.WriteAllText(statePath, "   ");

        var store = new FileBackedStatusLineChainStore(statePath);
        var found = store.TryGet(StatusLineField.StatusLine, out var capture);

        Assert.False(found);
        Assert.False(capture.HadOriginal);
    }

    // ---- DoctorCommand: claude resolution ------------------------------------------------

    [Fact]
    public void ResolveClaude_NativeExeOnPath_ReturnsNativeExe()
    {
        var exists = MakeExists(@"C:\tools\claude\claude.exe");
        var resolution = DoctorCommand.ResolveClaude(@"C:\other;C:\tools\claude", exists);

        Assert.Equal(ClaudeResolutionKind.NativeExe, resolution.Kind);
        Assert.Equal(@"C:\tools\claude\claude.exe", resolution.Path);
    }

    [Theory]
    [InlineData(".cmd")]
    [InlineData(".bat")]
    [InlineData(".ps1")]
    public void ResolveClaude_ShimOnly_ReturnsShim(string ext)
    {
        var exists = MakeExists(@"C:\tools\claude\claude" + ext);
        var resolution = DoctorCommand.ResolveClaude(@"C:\tools\claude", exists);

        Assert.Equal(ClaudeResolutionKind.Shim, resolution.Kind);
        Assert.Equal(@"C:\tools\claude\claude" + ext, resolution.Path);
    }

    [Fact]
    public void ResolveClaude_NativeExeAndShimInSameDir_PrefersNativeExe()
    {
        var exists = MakeExists(@"C:\tools\claude\claude.exe", @"C:\tools\claude\claude.cmd");
        var resolution = DoctorCommand.ResolveClaude(@"C:\tools\claude", exists);

        Assert.Equal(ClaudeResolutionKind.NativeExe, resolution.Kind);
    }

    [Fact]
    public void ResolveClaude_EarlierDirWins_EvenIfLaterHasNativeExe()
    {
        var exists = MakeExists(@"C:\shim\claude.cmd", @"C:\native\claude.exe");
        var resolution = DoctorCommand.ResolveClaude(@"C:\shim;C:\native", exists);

        Assert.Equal(ClaudeResolutionKind.Shim, resolution.Kind);
        Assert.Equal(@"C:\shim\claude.cmd", resolution.Path);
    }

    [Fact]
    public void ResolveClaude_NotOnPath_ReturnsMissing()
    {
        var resolution = DoctorCommand.ResolveClaude(@"C:\empty1;C:\empty2", _ => false);

        Assert.Equal(ClaudeResolutionKind.Missing, resolution.Kind);
        Assert.Null(resolution.Path);
    }

    [Fact]
    public void ResolveClaude_EmptyPath_ReturnsMissingWithoutThrowing()
    {
        var resolution = DoctorCommand.ResolveClaude(string.Empty, _ => false);

        Assert.Equal(ClaudeResolutionKind.Missing, resolution.Kind);
    }

    // ---- DoctorCommand: Run() exit codes and output --------------------------------------

    [Fact]
    public void Doctor_NativeExeAndWebView2Present_ExitsZero()
    {
        var writer = new StringWriter();
        var exitCode = DoctorCommand.Run(
            writer,
            @"C:\tools\claude",
            MakeExists(@"C:\tools\claude\claude.exe"),
            () => new WebView2Probe(WebView2RuntimeStatus.Found, "120.0.0.0"));

        Assert.Equal(0, exitCode);
        Assert.Contains("[OK]   claude resolves to a native executable", writer.ToString());
        Assert.Contains("[OK]   WebView2 Evergreen runtime present: 120.0.0.0", writer.ToString());
        Assert.Contains("All checks passed.", writer.ToString());
    }

    [Fact]
    public void Doctor_ShimResolution_ExitsOneAndExplains()
    {
        var writer = new StringWriter();
        var exitCode = DoctorCommand.Run(
            writer,
            @"C:\tools\claude",
            MakeExists(@"C:\tools\claude\claude.cmd"),
            () => new WebView2Probe(WebView2RuntimeStatus.Found, "120.0.0.0"));

        Assert.Equal(1, exitCode);
        Assert.Contains("[FAIL] claude resolves to a shim", writer.ToString());
        Assert.Contains("One or more checks failed", writer.ToString());
    }

    [Fact]
    public void Doctor_WebView2Missing_ExitsOneAndExplains()
    {
        var writer = new StringWriter();
        var exitCode = DoctorCommand.Run(
            writer,
            @"C:\tools\claude",
            MakeExists(@"C:\tools\claude\claude.exe"),
            () => new WebView2Probe(WebView2RuntimeStatus.NotFound, null));

        Assert.Equal(1, exitCode);
        Assert.Contains("[FAIL] WebView2 Evergreen runtime not found", writer.ToString());
    }

    [Fact]
    public void Doctor_ClaudeMissing_ExitsOneAndExplains()
    {
        var writer = new StringWriter();
        var exitCode = DoctorCommand.Run(
            writer,
            string.Empty,
            _ => false,
            () => new WebView2Probe(WebView2RuntimeStatus.Found, "120.0.0.0"));

        Assert.Equal(1, exitCode);
        Assert.Contains("[FAIL] claude was not found on PATH.", writer.ToString());
    }

    [Fact]
    public void Doctor_AlwaysNotesChecksAreMachineLocal()
    {
        var writer = new StringWriter();
        DoctorCommand.Run(
            writer,
            @"C:\tools\claude",
            MakeExists(@"C:\tools\claude\claude.exe"),
            () => new WebView2Probe(WebView2RuntimeStatus.Found, "120.0.0.0"));

        Assert.Contains("re-run on every deployment target before shipping", writer.ToString());
    }

    private static Func<string, bool> MakeExists(params string[] existingPaths)
    {
        var set = new HashSet<string>(existingPaths, StringComparer.OrdinalIgnoreCase);
        return path => set.Contains(path);
    }

    // ---- helpers ------------------------------------------------------------------------

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"glaude-cli-test-{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Test cleanup only.
            }
        }
    }
}
