using Glaude.Server;
using Xunit;

namespace Glaude.Tests;

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
        string path = Path.Combine(Path.GetTempPath(), $"glaude-roots-test-{Guid.NewGuid():N}.json");
        _tempFiles.Add(path);
        return path;
    }

    private string MissingPath() =>
        Path.Combine(Path.GetTempPath(), $"glaude-roots-missing-{Guid.NewGuid():N}.json");

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
    public void Load_NoArgs_NeverThrows()
    {
        // Exercises the real default candidate paths end-to-end (whatever the state of this
        // dev machine happens to be) - the contract is simply "never throws, always an array".
        var result = RootFoldersConfig.Load();

        Assert.NotNull(result);
    }
}
