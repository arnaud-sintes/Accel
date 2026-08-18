namespace Accel.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using Accel.Settings;
using Xunit;

/// <summary>
/// Phase 2 tests. Everything here runs against fixture strings and temp files only —
/// the real %USERPROFILE%\.claude\settings.json is never read or written.
/// </summary>
public class SettingsMergerTests
{
    private const string ExePath = @"C:\tools\accel\accel.exe";
    private const int Port = 40010;
    private const int OtherPort = 41111;

    private static AccelHookSpec Spec(int port = Port) => new(port, ExePath);

    // ---- fixtures --------------------------------------------------------------------

    private const string EmptyObjectFixture = "{}";

    /// <summary>
    /// Approximates the real machine's settings.json: an existing PreToolUse -> `rtk hook claude`
    /// matcher group, an existing exec-form Notification toast hook, plus unrelated top-level
    /// keys (env / theme / permissions / effortLevel / preferredNotifChannel) and non-ASCII text.
    /// </summary>
    private const string RealWorldFixture = """
    {
      "env": {
        "SOME_TOOL_HOME": "C:\\tools\\some tool",
        "GREETING": "caf\u00e9 \u2014 na\u00efve"
      },
      "theme": "dark",
      "effortLevel": "medium",
      "preferredNotifChannel": "terminal_bell",
      "permissions": {
        "allow": ["Bash(git status:*)", "Read(//c/projects/**)"],
        "deny": []
      },
      "hooks": {
        "PreToolUse": [
          {
            "matcher": "Bash",
            "hooks": [
              { "type": "command", "command": "rtk hook claude" }
            ]
          }
        ],
        "Notification": [
          {
            "matcher": "*",
            "hooks": [
              {
                "type": "command",
                "command": "powershell.exe",
                "args": ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "C:\\tools\\toast\\toast.ps1"],
                "async": true
              }
            ]
          }
        ]
      }
    }
    """;

    private const string ForeignStatusLineFixture = """
    {
      "theme": "dark",
      "statusLine": {
        "type": "command",
        "command": "node C:\\tools\\ccstatus\\statusline.js --fancy",
        "padding": 0,
        "refreshInterval": 2
      },
      "subagentStatusLine": {
        "type": "command",
        "command": "node C:\\tools\\ccstatus\\subagents.js"
      },
      "hooks": {
        "PreToolUse": [
          { "matcher": "Bash", "hooks": [ { "type": "command", "command": "rtk hook claude" } ] }
        ]
      }
    }
    """;

    private const string MalformedFixture = """
    {
      "theme": "dark",
      "hooks": { "PreToolUse": [
    """;

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    // ---- load states -----------------------------------------------------------------

    [Fact]
    public void Load_MissingFile_ReturnsMissingAndIsWritable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"accel-test-{Guid.NewGuid():N}", "settings.json");
        var file = SettingsFile.Load(path);

        Assert.Equal(SettingsLoadStatus.Missing, file.Status);
        Assert.Null(file.Root);
        Assert.True(file.IsWritableForInstall);
    }

    [Fact]
    public void Load_EmptyFile_ReturnsEmptyStateAndInstallRefuses()
    {
        using var temp = new TempSettings("   \r\n");
        var file = SettingsFile.Load(temp.Path);

        Assert.Equal(SettingsLoadStatus.Empty, file.Status);
        Assert.Null(file.Root);
        Assert.False(file.IsWritableForInstall);

        var outcome = SettingsMerger.InstallInto(file, Spec(), new InMemoryStatusLineChainStore());

        Assert.Equal(InstallOutcome.Refused, outcome);
        Assert.Equal("   \r\n", File.ReadAllText(temp.Path));
        Assert.False(File.Exists(file.BackupPath));
    }

    [Fact]
    public void Load_EmptyJsonObject_InstallsCleanly()
    {
        var root = Parse(EmptyObjectFixture);
        var store = new InMemoryStatusLineChainStore();

        Assert.Equal(InstallState.NotInstalled, SettingsMerger.Detect(root, Spec()).State);
        Assert.True(SettingsMerger.Install(root, Spec(), store));

        var detected = SettingsMerger.Detect(root, Spec());
        Assert.Equal(InstallState.Installed, detected.State);
        Assert.Equal(
            new[] { "SessionStart", "SessionEnd", "SubagentStart", "SubagentStop", "PostToolUse" }.OrderBy(x => x),
            detected.FoundEvents.Keys.OrderBy(x => x));
        Assert.Equal(StatusLineOwnership.Accel, detected.StatusLine);
        Assert.Equal(StatusLineOwnership.Accel, detected.SubagentStatusLine);
    }

    [Fact]
    public void Load_MalformedJson_ReturnsMalformedAndInstallRefusesWithoutOverwriting()
    {
        using var temp = new TempSettings(MalformedFixture);
        var file = SettingsFile.Load(temp.Path);

        Assert.Equal(SettingsLoadStatus.Malformed, file.Status);
        Assert.Null(file.Root);
        Assert.NotNull(file.ErrorMessage);
        Assert.False(file.IsWritableForInstall);

        var outcome = SettingsMerger.InstallInto(file, Spec(), new InMemoryStatusLineChainStore());

        Assert.Equal(InstallOutcome.Refused, outcome);
        Assert.Equal(MalformedFixture, File.ReadAllText(temp.Path));
        Assert.False(File.Exists(file.BackupPath));
    }

    [Fact]
    public void Load_NonObjectRoot_IsMalformed()
    {
        var file = SettingsFile.FromText("x.json", "[1, 2, 3]");
        Assert.Equal(SettingsLoadStatus.Malformed, file.Status);
    }

    // ---- real-world file -------------------------------------------------------------

    [Fact]
    public void Install_RealWorldFile_LeavesThirdPartyEntriesAndTopLevelKeysUntouched()
    {
        var root = Parse(RealWorldFixture);
        var original = Parse(RealWorldFixture);

        Assert.True(SettingsMerger.Install(root, Spec(), new InMemoryStatusLineChainStore()));

        // Third-party hook groups byte-identical.
        Assert.Equal(
            SettingsFile.Serialize(original["hooks"]!["PreToolUse"]),
            SettingsFile.Serialize(root["hooks"]!["PreToolUse"]));
        Assert.Equal(
            SettingsFile.Serialize(original["hooks"]!["Notification"]),
            SettingsFile.Serialize(root["hooks"]!["Notification"]));

        // Unrelated top-level keys survive verbatim (the POCO round-trip hazard).
        foreach (var key in new[] { "env", "theme", "effortLevel", "preferredNotifChannel", "permissions" })
        {
            Assert.True(JsonEqual(original[key], root[key]), $"top-level key '{key}' was altered");
        }

        Assert.Equal("café — naïve", root["env"]!["GREETING"]!.GetValue<string>());

        // Accel's own entries are present and exec-form (self-invoked via `notify`, not curl).
        var sessionStart = SingleAccelEntry(root, "SessionStart");
        Assert.Equal(ExePath, sessionStart["command"]!.GetValue<string>());
        Assert.IsType<JsonArray>(sessionStart["args"]);
        var args = sessionStart["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray();
        Assert.Contains("X-Accel-Hook: SessionStart", args);
        Assert.Contains("notify", args);
        Assert.Contains("/events/session-start", args);
        Assert.Contains(Port.ToString(), args);

        // SessionEnd must be async with a short timeout (1.5 s shared budget).
        var sessionEnd = SingleAccelEntry(root, "SessionEnd");
        Assert.True(sessionEnd["async"]!.GetValue<bool>());
        Assert.Equal(2, sessionEnd["timeout"]!.GetValue<int>());

        // statusLine / subagentStatusLine are TOP-LEVEL, not inside hooks.
        Assert.Null(root["hooks"]!["statusLine"]);
        Assert.Contains("statusline --port 40010", root["statusLine"]!["command"]!.GetValue<string>());
        Assert.Equal(5, root["statusLine"]!["refreshInterval"]!.GetValue<int>());
        Assert.Contains("subagent-statusline --port 40010", root["subagentStatusLine"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void Install_AddsAdditionalMatcherGroup_WhenEventAlreadyHasThirdPartyGroups()
    {
        // A pre-existing SessionStart group from another tool must survive and stay first.
        var root = Parse("""
        {
          "hooks": {
            "SessionStart": [
              { "matcher": "*", "hooks": [ { "type": "command", "command": "other-tool.exe" } ] }
            ]
          }
        }
        """);

        SettingsMerger.Install(root, Spec(), new InMemoryStatusLineChainStore());

        var groups = root["hooks"]!["SessionStart"]!.AsArray();
        Assert.Equal(2, groups.Count);
        Assert.Equal("other-tool.exe", groups[0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal(ExePath, groups[1]!["hooks"]![0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void Detect_IgnoresLookalikeThirdPartyEntries()
    {
        // Another tool posting to the same port is NOT ours: only the marker header counts.
        var root = Parse($$"""
        {
          "hooks": {
            "SessionStart": [
              {
                "matcher": "*",
                "hooks": [
                  {
                    "type": "command",
                    "command": "curl.exe",
                    "args": ["-X", "POST", "http://127.0.0.1:{{Port}}/events/session-start"]
                  }
                ]
              }
            ]
          }
        }
        """);

        Assert.Equal(InstallState.NotInstalled, SettingsMerger.Detect(root, Spec()).State);
    }

    // ---- status line chaining --------------------------------------------------------

    [Fact]
    public void Install_ForeignStatusLine_IsCapturedThenRestoredOnUninstall()
    {
        var original = Parse(ForeignStatusLineFixture);
        var root = Parse(ForeignStatusLineFixture);
        var store = new InMemoryStatusLineChainStore();

        SettingsMerger.Install(root, Spec(), store);

        Assert.True(store.TryGet(StatusLineField.StatusLine, out var captured));
        Assert.True(captured.HadOriginal);
        Assert.True(JsonEqual(original["statusLine"], captured.Original));

        Assert.True(store.TryGet(StatusLineField.SubagentStatusLine, out var capturedSub));
        Assert.True(capturedSub.HadOriginal);
        Assert.True(JsonEqual(original["subagentStatusLine"], capturedSub.Original));

        // Overwritten with Accel's own command.
        Assert.True(AccelHookSpec.IsAccelStatusLineCommand(root["statusLine"]!["command"]!.GetValue<string>()));

        SettingsMerger.Uninstall(root, store);

        Assert.True(JsonEqual(original["statusLine"], root["statusLine"]));
        Assert.True(JsonEqual(original["subagentStatusLine"], root["subagentStatusLine"]));
    }

    [Fact]
    public void Uninstall_RemovesStatusLineFields_WhenThereWasNoOriginal()
    {
        var root = Parse(EmptyObjectFixture);
        var store = new InMemoryStatusLineChainStore();

        SettingsMerger.Install(root, Spec(), store);
        Assert.NotNull(root["statusLine"]);

        SettingsMerger.Uninstall(root, store);

        Assert.Null(root["statusLine"]);
        Assert.Null(root["subagentStatusLine"]);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Install_Twice_DoesNotOverwriteTheCapturedOriginal()
    {
        var root = Parse(ForeignStatusLineFixture);
        var store = new InMemoryStatusLineChainStore();

        SettingsMerger.Install(root, Spec(), store);
        SettingsMerger.Install(root, Spec(OtherPort), store);

        Assert.True(store.TryGet(StatusLineField.StatusLine, out var captured));
        Assert.Contains("ccstatus", AccelHookSpec.GetCommand(captured.Original));
    }

    // ---- already installed / idempotency ---------------------------------------------

    [Fact]
    public void Detect_AlreadyInstalled_ReturnsInstalled_AndInstallIsNoOp()
    {
        var root = Parse(RealWorldFixture);
        var store = new InMemoryStatusLineChainStore();

        Assert.True(SettingsMerger.Install(root, Spec(), store));
        var afterFirst = SettingsFile.Serialize(root);

        Assert.Equal(InstallState.Installed, SettingsMerger.Detect(root, Spec()).State);

        var changed = SettingsMerger.Install(root, Spec(), store);

        Assert.False(changed);
        Assert.Equal(afterFirst, SettingsFile.Serialize(root));
    }

    [Fact]
    public void Install_Twice_ProducesNoDuplicateEntries()
    {
        var root = Parse(RealWorldFixture);
        var store = new InMemoryStatusLineChainStore();

        SettingsMerger.Install(root, Spec(), store);
        SettingsMerger.Install(root, Spec(), store);
        SettingsMerger.Install(root, Spec(), store);

        var accel = SettingsMerger.EnumerateAccelEntries(root).ToList();
        Assert.Equal(5, accel.Count);
        Assert.Equal(5, accel.Select(g => g.EventName).Distinct().Count());
    }

    // ---- port drift ------------------------------------------------------------------

    [Fact]
    public void Detect_PortDrift_AndReinstallRewritesOnlyAccelEntries()
    {
        var root = Parse(RealWorldFixture);
        var store = new InMemoryStatusLineChainStore();

        SettingsMerger.Install(root, Spec(), store);

        var preToolUseBefore = SettingsFile.Serialize(root["hooks"]!["PreToolUse"]);
        var notificationBefore = SettingsFile.Serialize(root["hooks"]!["Notification"]);

        var drifted = SettingsMerger.Detect(root, Spec(OtherPort));
        Assert.Equal(InstallState.PortDrift, drifted.State);
        Assert.Empty(drifted.MissingEvents);
        Assert.All(drifted.DriftingPorts, p => Assert.Equal(Port, p));

        Assert.True(SettingsMerger.Install(root, Spec(OtherPort), store));

        Assert.Equal(InstallState.Installed, SettingsMerger.Detect(root, Spec(OtherPort)).State);

        // Third-party entries byte-identical after the rewrite.
        Assert.Equal(preToolUseBefore, SettingsFile.Serialize(root["hooks"]!["PreToolUse"]));
        Assert.Equal(notificationBefore, SettingsFile.Serialize(root["hooks"]!["Notification"]));

        // Every Accel entry now carries the new port, and there are no leftovers on the old one.
        Assert.All(
            SettingsMerger.EnumerateAccelEntries(root),
            e => Assert.Equal(OtherPort, e.Port));
        Assert.Contains($"--port {OtherPort}", root["statusLine"]!["command"]!.GetValue<string>());
    }

    // ---- partial install -------------------------------------------------------------

    [Fact]
    public void Detect_HalfInstalled_ReturnsPartiallyInstalled_AndInstallRepairsIt()
    {
        var root = Parse(RealWorldFixture);
        var store = new InMemoryStatusLineChainStore();

        SettingsMerger.Install(root, Spec(), store);
        var complete = SettingsFile.Serialize(root);

        // Simulate a half-written install: drop the SubagentStop event entirely and the
        // subagentStatusLine field.
        root["hooks"]!.AsObject().Remove("SubagentStop");
        root.Remove("subagentStatusLine");

        var detected = SettingsMerger.Detect(root, Spec());
        Assert.Equal(InstallState.PartiallyInstalled, detected.State);
        Assert.Equal(new[] { "SubagentStop" }, detected.MissingEvents);
        Assert.Equal(StatusLineOwnership.None, detected.SubagentStatusLine);

        Assert.True(SettingsMerger.Install(root, Spec(), store));
        Assert.Equal(InstallState.Installed, SettingsMerger.Detect(root, Spec()).State);
        Assert.True(JsonEqual(JsonNode.Parse(complete), root));
    }

    [Fact]
    public void Detect_StrayAccelEventNotInSpec_IsPartiallyInstalled_AndInstallRemovesIt()
    {
        var root = Parse(RealWorldFixture);
        var store = new InMemoryStatusLineChainStore();

        // Installed on a version that emits SubagentStart...
        SettingsMerger.Install(root, Spec(), store);

        // ...then re-run against a version-gated spec that excludes it.
        var gated = new AccelHookSpec(Port, ExePath, includeSubagentStart: false);

        var detected = SettingsMerger.Detect(root, gated);
        Assert.Equal(InstallState.PartiallyInstalled, detected.State);
        Assert.Equal(new[] { "SubagentStart" }, detected.StrayEvents);

        Assert.True(SettingsMerger.Install(root, gated, store));

        Assert.Equal(InstallState.Installed, SettingsMerger.Detect(root, gated).State);
        Assert.Null(root["hooks"]!["SubagentStart"]);
    }

    // ---- round trips -----------------------------------------------------------------

    [Theory]
    [InlineData(EmptyObjectFixture)]
    [InlineData(RealWorldFixture)]
    [InlineData(ForeignStatusLineFixture)]
    public void InstallThenUninstall_RestoresTheOriginalDocument(string fixture)
    {
        var original = Parse(fixture);
        var root = Parse(fixture);
        var store = new InMemoryStatusLineChainStore();

        SettingsMerger.Install(root, Spec(), store);
        Assert.True(SettingsMerger.Uninstall(root, store));

        Assert.True(
            JsonEqual(original, root),
            $"round-trip lost or altered data.\nexpected:\n{SettingsFile.Serialize(original)}\nactual:\n{SettingsFile.Serialize(root)}");
    }

    [Fact]
    public void Uninstall_PrunesEmptyContainersButKeepsOtherEvents()
    {
        var root = Parse(RealWorldFixture);
        var store = new InMemoryStatusLineChainStore();

        SettingsMerger.Install(root, Spec(), store);
        SettingsMerger.Uninstall(root, store);

        var hooks = root["hooks"]!.AsObject();

        // Accel's own event keys are gone...
        Assert.Null(hooks["SessionStart"]);
        Assert.Null(hooks["SessionEnd"]);
        Assert.Null(hooks["SubagentStart"]);
        Assert.Null(hooks["SubagentStop"]);

        // ...but the top-level hooks object survives because other events remain.
        Assert.NotNull(hooks["PreToolUse"]);
        Assert.NotNull(hooks["Notification"]);
    }

    [Fact]
    public void Uninstall_RemovesOnlyAccelEntryFromASharedMatcherGroup()
    {
        var root = Parse(RealWorldFixture);
        SettingsMerger.Install(root, Spec(), new InMemoryStatusLineChainStore());

        // Another tool appends its own entry into Accel's matcher group.
        var accelGroup = root["hooks"]!["SessionStart"]!.AsArray()[0]!.AsObject();
        accelGroup["hooks"]!.AsArray().Add(new JsonObject
        {
            ["type"] = "command",
            ["command"] = "someone-else.exe",
        });

        SettingsMerger.Uninstall(root, new InMemoryStatusLineChainStore());

        var remaining = root["hooks"]!["SessionStart"]!.AsArray();
        Assert.Single(remaining);
        Assert.Single(remaining[0]!["hooks"]!.AsArray());
        Assert.Equal("someone-else.exe", remaining[0]!["hooks"]![0]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void Uninstall_DropsTheHooksObject_WhenNothingElseRemains()
    {
        var root = Parse(EmptyObjectFixture);
        var store = new InMemoryStatusLineChainStore();

        SettingsMerger.Install(root, Spec(), store);
        SettingsMerger.Uninstall(root, store);

        Assert.Null(root["hooks"]);
        Assert.Equal("{}", root.ToJsonString());
    }

    // ---- atomic save / backup --------------------------------------------------------

    [Fact]
    public void InstallInto_WritesAtomicallyAndTakesASingleBackup()
    {
        using var temp = new TempSettings(RealWorldFixture);
        var store = new InMemoryStatusLineChainStore();

        var file = SettingsFile.Load(temp.Path);
        Assert.Equal(SettingsLoadStatus.Ok, file.Status);

        Assert.Equal(InstallOutcome.Applied, SettingsMerger.InstallInto(file, Spec(), store));

        Assert.True(File.Exists(file.BackupPath));
        Assert.True(JsonEqual(Parse(RealWorldFixture), Parse(File.ReadAllText(file.BackupPath))));

        var written = SettingsFile.Load(temp.Path);
        Assert.Equal(SettingsLoadStatus.Ok, written.Status);
        Assert.Equal(InstallState.Installed, SettingsMerger.Detect(written.Root, Spec()).State);

        // Non-ASCII survived the write (non-escaping encoder).
        Assert.Contains("café — naïve", File.ReadAllText(temp.Path));

        // No temp files left behind.
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(temp.Path)!, "*.tmp"));

        // Second pass is a no-op.
        Assert.Equal(InstallOutcome.NoChange, SettingsMerger.InstallInto(written, Spec(), store));
    }

    [Fact]
    public void InstallInto_CreatesTheFile_WhenItIsAbsentEntirely()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"accel-test-{Guid.NewGuid():N}");
        var path = Path.Combine(dir, "settings.json");

        try
        {
            var file = SettingsFile.Load(path);
            Assert.Equal(SettingsLoadStatus.Missing, file.Status);

            Assert.Equal(InstallOutcome.Applied, SettingsMerger.InstallInto(file, Spec(), new InMemoryStatusLineChainStore()));

            Assert.True(File.Exists(path));
            Assert.False(File.Exists(file.BackupPath));
            Assert.Equal(InstallState.Installed, SettingsMerger.Detect(SettingsFile.Load(path).Root, Spec()).State);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ---- helpers ---------------------------------------------------------------------

    private static JsonObject SingleAccelEntry(JsonObject root, string eventName)
    {
        var located = SettingsMerger.EnumerateAccelEntries(root)
            .Where(e => e.EventName == eventName)
            .ToList();

        Assert.Single(located);

        var found = located[0];
        return root["hooks"]![eventName]![found.GroupIndex]!["hooks"]![found.EntryIndex]!.AsObject();
    }

    /// <summary>
    /// Structural equality: object key order is irrelevant (re-serialisation may reorder),
    /// array order is significant, and no key or value may be added, lost or altered.
    /// </summary>
    private static bool JsonEqual(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        switch (a)
        {
            case JsonObject ao when b is JsonObject bo:
                if (ao.Count != bo.Count)
                {
                    return false;
                }

                foreach (var (key, value) in ao)
                {
                    if (!bo.TryGetPropertyValue(key, out var other) || !JsonEqual(value, other))
                    {
                        return false;
                    }
                }

                return true;

            case JsonArray aa when b is JsonArray ba:
                if (aa.Count != ba.Count)
                {
                    return false;
                }

                return !aa.Where((t, i) => !JsonEqual(t, ba[i])).Any();

            case JsonValue av when b is JsonValue bv:
                return string.Equals(av.ToJsonString(), bv.ToJsonString(), StringComparison.Ordinal);

            default:
                return false;
        }
    }

    /// <summary>A settings.json fixture in its own temp directory. Never the real one.</summary>
    private sealed class TempSettings : IDisposable
    {
        private readonly string directory;

        public TempSettings(string content)
        {
            directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"accel-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "settings.json");
            File.WriteAllText(Path, content);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
                // Test cleanup only.
            }
        }
    }
}
