using Accel.Server;
using Xunit;

namespace Accel.Tests;

/// <summary>
/// Unit tests for Phase UI-C's <see cref="RootFoldersConfig"/> probe/parse logic, exercised
/// via the explicit-candidate-list overload so no real filesystem locations
/// (%USERPROFILE%, exe directory, process cwd) are touched.
/// </summary>
public class RootFoldersConfigTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch { /* best effort cleanup */ }
        }
    }

    private string NewTempPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"accel-roots-test-{Guid.NewGuid():N}.json");
        _tempFiles.Add(path);
        return path;
    }

    private string MissingPath() =>
        Path.Combine(Path.GetTempPath(), $"accel-roots-missing-{Guid.NewGuid():N}.json");

    [Fact]
    public void Candidate1ExistsAndParses_ReturnsItsContents()
    {
        string candidate1 = NewTempPath();
        File.WriteAllText(candidate1, "[\"C:/projects\", \"C:/other\"]");
        string candidate2 = MissingPath();
        string candidate3 = MissingPath();

        var result = RootFoldersConfig.Load(new[] { candidate1, candidate2, candidate3 });

        Assert.Equal(new[] { "C:/projects", "C:/other" }, result);
    }

    [Fact]
    public void Candidate1ExistsButMalformed_ReturnsEmpty_DoesNotFallThroughToCandidate2()
    {
        string candidate1 = NewTempPath();
        File.WriteAllText(candidate1, "{ not valid json array");

        string candidate2 = NewTempPath();
        File.WriteAllText(candidate2, "[\"C:/should-not-be-used\"]");

        var result = RootFoldersConfig.Load(new[] { candidate1, candidate2 });

        Assert.Empty(result);
    }

    [Fact]
    public void Candidate1ExistsButNotAnArray_ReturnsEmpty_DoesNotFallThroughToCandidate2()
    {
        string candidate1 = NewTempPath();
        File.WriteAllText(candidate1, "{ \"foo\": \"bar\" }");

        string candidate2 = NewTempPath();
        File.WriteAllText(candidate2, "[\"C:/should-not-be-used\"]");

        var result = RootFoldersConfig.Load(new[] { candidate1, candidate2 });

        Assert.Empty(result);
    }

    [Fact]
    public void Candidate1ExistsButNotArrayOfStrings_ReturnsEmpty()
    {
        string candidate1 = NewTempPath();
        File.WriteAllText(candidate1, "[1, 2, 3]");

        var result = RootFoldersConfig.Load(new[] { candidate1 });

        Assert.Empty(result);
    }

    [Fact]
    public void Candidate1Missing_Candidate2ExistsAndParses_ReturnsCandidate2Contents()
    {
        string candidate1 = MissingPath();
        string candidate2 = NewTempPath();
        File.WriteAllText(candidate2, "[\"C:/from-candidate-2\"]");
        string candidate3 = MissingPath();

        var result = RootFoldersConfig.Load(new[] { candidate1, candidate2, candidate3 });

        Assert.Equal(new[] { "C:/from-candidate-2" }, result);
    }

    [Fact]
    public void AllThreeMissing_ReturnsEmpty()
    {
        var result = RootFoldersConfig.Load(new[] { MissingPath(), MissingPath(), MissingPath() });

        Assert.Empty(result);
    }

    [Fact]
    public void NonExistentPathInValidArray_IsStillIncludedVerbatim()
    {
        string candidate1 = NewTempPath();
        File.WriteAllText(candidate1, "[\"Z:/does/not/exist\"]");

        var result = RootFoldersConfig.Load(new[] { candidate1 });

        Assert.Equal(new[] { "Z:/does/not/exist" }, result);
    }

    [Fact]
    public void ForwardSlashPath_RoundTripsVerbatim_NotNormalizedToBackslashes()
    {
        string candidate1 = NewTempPath();
        File.WriteAllText(candidate1, "[\"C:/projects\"]");

        var result = RootFoldersConfig.Load(new[] { candidate1 });

        Assert.Equal("C:/projects", Assert.Single(result));
    }

    [Fact]
    public void DefaultCandidatePaths_ReturnsThreeCandidatesInOrder()
    {
        var candidates = RootFoldersConfig.DefaultCandidatePaths();

        Assert.Equal(3, candidates.Length);
        Assert.EndsWith(Path.Combine(".claude", RootFoldersConfig.DurableFileName), candidates[0]);
        Assert.EndsWith(RootFoldersConfig.LocalFileName, candidates[1]);
        Assert.EndsWith(RootFoldersConfig.LocalFileName, candidates[2]);
    }

    [Fact]
    public void DurableConfigPath_IsCandidate1()
    {
        Assert.Equal(RootFoldersConfig.DefaultCandidatePaths()[0], RootFoldersConfig.DurableConfigPath());
        Assert.EndsWith(
            Path.Combine(".claude", RootFoldersConfig.DurableFileName),
            RootFoldersConfig.DurableConfigPath());
    }

    // --- Write-path resolution: writes must never target a possibly-unwritable legacy slot ---

    [Fact]
    public void ResolveWritePath_DurableMissing_LegacyExeDirFileExists_StillTargetsDurableHome()
    {
        // The exact fresh-default-install shape: the installer's {app}\folder.json exists (and
        // {app} = C:\Program Files\Accel is unwritable for a standard user), the durable per-user
        // file does not exist yet. The write target must be the durable home regardless.
        string durable = MissingPath();
        _tempFiles.Add(durable);
        string exeDirFile = NewTempPath();
        File.WriteAllText(exeDirFile, "[]");

        string writePath = RootFoldersConfig.ResolveWritePath(new[] { durable, exeDirFile, MissingPath() });

        Assert.Equal(durable, writePath);
    }

    [Fact]
    public void ResolveWritePath_DurableExists_ReturnsIt_AndDoesNotRewriteIt()
    {
        string durable = NewTempPath();
        File.WriteAllText(durable, "[\"C:/already-here\"]");
        string exeDirFile = NewTempPath();
        File.WriteAllText(exeDirFile, "[\"C:/legacy\"]");

        string writePath = RootFoldersConfig.ResolveWritePath(new[] { durable, exeDirFile });

        Assert.Equal(durable, writePath);

        // Untouched: an existing durable file is authoritative, no migration runs over it.
        Assert.Equal(new[] { "C:/already-here" }, RootFoldersConfig.Load(new[] { durable }));
    }

    [Fact]
    public void ResolveWritePath_MigratesLegacyFolderJsonRootsIntoDurableHome_OnFirstRun()
    {
        string durable = MissingPath();
        _tempFiles.Add(durable);
        _tempFiles.Add(durable + Accel.Settings.SettingsFile.BackupSuffix);
        string exeDirFile = NewTempPath();
        File.WriteAllText(exeDirFile, "[\"C:/legacy-root\", \"C:/legacy-other\"]");

        string writePath = RootFoldersConfig.ResolveWritePath(new[] { durable, exeDirFile, MissingPath() });

        Assert.Equal(durable, writePath);
        Assert.True(File.Exists(durable));

        // Migrated verbatim, and upgraded to v2 on the way in.
        var migrated = RootFoldersConfig.LoadFull(new[] { durable });
        Assert.Equal(new[] { "C:/legacy-root", "C:/legacy-other" }, migrated.Roots);
        Assert.Contains("\"version\": 2", File.ReadAllText(durable));

        // The legacy file is left alone - migration copies, it never moves or deletes.
        Assert.True(File.Exists(exeDirFile));
    }

    [Fact]
    public void ResolveWritePath_MigratesLegacySessionOverridesToo()
    {
        string durable = MissingPath();
        _tempFiles.Add(durable);
        _tempFiles.Add(durable + Accel.Settings.SettingsFile.BackupSuffix);
        string cwdFile = NewTempPath();
        File.WriteAllText(
            cwdFile,
            "{\"version\":2,\"roots\":[\"C:/x\"],\"sessions\":{\"s1\":{\"displayName\":\"Kept\",\"pinned\":true,\"hidden\":false}}}");

        RootFoldersConfig.ResolveWritePath(new[] { durable, MissingPath(), cwdFile });

        var migrated = RootFoldersConfig.LoadFull(new[] { durable });
        Assert.Equal(new[] { "C:/x" }, migrated.Roots);
        var kept = Assert.Single(migrated.Sessions);
        Assert.Equal("s1", kept.Key);
        Assert.Equal("Kept", kept.Value.DisplayName);
        Assert.True(kept.Value.Pinned);
    }

    [Fact]
    public void ResolveWritePath_NothingExistsAnywhere_ReturnsDurableHome_AndCreatesNoFile()
    {
        string durable = MissingPath();

        string writePath = RootFoldersConfig.ResolveWritePath(new[] { durable, MissingPath(), MissingPath() });

        Assert.Equal(durable, writePath);

        // Nothing worth migrating means nothing is written: the first real Save creates the file.
        Assert.False(File.Exists(durable));
    }

    [Fact]
    public void ResolveWritePath_EmptyLegacyFile_DoesNotMigrate()
    {
        string durable = MissingPath();
        string exeDirFile = NewTempPath();
        File.WriteAllText(exeDirFile, "[]");

        RootFoldersConfig.ResolveWritePath(new[] { durable, exeDirFile });

        Assert.False(File.Exists(durable));
    }

    [Fact]
    public void ResolveWritePath_NoArgs_ReturnsDurableHome_NeverAnExeDirOrCwdPath()
    {
        string writePath = RootFoldersConfig.ResolveWritePath();

        Assert.Equal(RootFoldersConfig.DurableConfigPath(), writePath);
        Assert.EndsWith(RootFoldersConfig.DurableFileName, writePath);
    }

    [Fact]
    public void Load_NoArgs_NeverThrows()
    {
        // Exercises the real default candidate paths end-to-end (whatever the state of this
        // dev machine happens to be) - the contract is simply "never throws, always an array".
        var result = RootFoldersConfig.Load();

        Assert.NotNull(result);
    }

    // --- P1-T3: v2 schema (JsonValueKind-polymorphic load, atomic v2 save, sparse-map prune) ---

    [Fact]
    public void V1FlatArray_StillLoadsCorrectly_ViaLoadFull()
    {
        string candidate1 = NewTempPath();
        File.WriteAllText(candidate1, "[\"C:/projects\", \"C:/other\"]");

        var full = RootFoldersConfig.LoadFull(new[] { candidate1 });

        Assert.Equal(new[] { "C:/projects", "C:/other" }, full.Roots);
        Assert.Empty(full.Sessions);

        // The public string[]-returning surface is unchanged for v1 files too.
        var roots = RootFoldersConfig.Load(new[] { candidate1 });
        Assert.Equal(new[] { "C:/projects", "C:/other" }, roots);
    }

    [Fact]
    public void V2File_RoundTrips_RootsAndSessions()
    {
        string path = NewTempPath();
        File.Delete(path); // Save() must handle a not-yet-existing file.

        var roots = new[] { "C:/projects", "C:/other" };
        var lastOpened = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var sessions = new Dictionary<string, SessionOverride>
        {
            ["session-a"] = new SessionOverride("My Session", Pinned: true, Hidden: false, LastOpenedUtc: lastOpened),
        };
        var keepSet = new HashSet<string> { "session-a" };

        RootFoldersConfig.Save(path, roots, sessions, keepSet);

        var loaded = RootFoldersConfig.LoadFull(new[] { path });

        Assert.Equal(roots, loaded.Roots);
        var sessionOverride = Assert.Single(loaded.Sessions);
        Assert.Equal("session-a", sessionOverride.Key);
        Assert.Equal("My Session", sessionOverride.Value.DisplayName);
        Assert.True(sessionOverride.Value.Pinned);
        Assert.False(sessionOverride.Value.Hidden);
        Assert.Equal(lastOpened, sessionOverride.Value.LastOpenedUtc);

        // The public string[] surface still works unchanged against a v2 file.
        Assert.Equal(roots, RootFoldersConfig.Load(new[] { path }));
    }

    [Fact]
    public void MalformedJson_LoadsAsEmptyConfig_DoesNotThrow()
    {
        string path = NewTempPath();
        File.WriteAllText(path, "{ not valid json at all");

        var full = RootFoldersConfig.LoadFull(new[] { path });

        Assert.Empty(full.Roots);
        Assert.Empty(full.Sessions);
    }

    [Fact]
    public void Save_PrunesSessionOverrides_NotInKeepSet()
    {
        string path = NewTempPath();
        File.Delete(path);

        var sessions = new Dictionary<string, SessionOverride>
        {
            ["still-alive"] = new SessionOverride("Alive", Pinned: false, Hidden: false, LastOpenedUtc: null),
            ["long-gone"] = new SessionOverride("Gone", Pinned: true, Hidden: true, LastOpenedUtc: null),
        };
        var keepSet = new HashSet<string> { "still-alive" };

        RootFoldersConfig.Save(path, Array.Empty<string>(), sessions, keepSet);

        var loaded = RootFoldersConfig.LoadFull(new[] { path });

        var kept = Assert.Single(loaded.Sessions);
        Assert.Equal("still-alive", kept.Key);
    }
}
