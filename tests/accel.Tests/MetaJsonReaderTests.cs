using Accel.Metrics;
using Xunit;

namespace Accel.Tests;

public class MetaJsonReaderTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string NewTranscriptPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"agent-{Guid.NewGuid():N}.jsonl");
        _tempFiles.Add(path);
        return path;
    }

    private static string SiblingMetaPath(string transcriptPath)
    {
        string dir = Path.GetDirectoryName(transcriptPath)!;
        string baseName = Path.GetFileNameWithoutExtension(transcriptPath);
        return Path.Combine(dir, baseName + ".meta.json");
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch { /* best effort cleanup */ }
            try { File.Delete(SiblingMetaPath(path)); } catch { /* best effort cleanup */ }
        }
    }

    [Fact]
    public void SiblingPresent_AllFields_ExtractsAll()
    {
        string transcriptPath = NewTranscriptPath();
        string metaPath = SiblingMetaPath(transcriptPath);
        File.WriteAllText(metaPath, """
            {
                "agentType": "code-reviewer",
                "spawnDepth": 2,
                "toolUseId": "tool-123",
                "description": "Reviews the diff",
                "model": "sonnet",
                "parentAgentId": "agent-parent-1"
            }
            """);

        var meta = MetaJsonReader.TryRead(transcriptPath);

        Assert.NotNull(meta);
        Assert.Equal("code-reviewer", meta!.AgentType);
        Assert.Equal(2, meta.SpawnDepth);
        Assert.Equal("tool-123", meta.ToolUseId);
        Assert.Equal("Reviews the diff", meta.Description);
        Assert.Equal("sonnet", meta.Model);
        Assert.Equal("agent-parent-1", meta.ParentAgentId);
    }

    [Fact]
    public void SiblingPresent_PartialFields_MissingOnesAreNull()
    {
        string transcriptPath = NewTranscriptPath();
        string metaPath = SiblingMetaPath(transcriptPath);
        File.WriteAllText(metaPath, """{"agentType":"general-purpose","spawnDepth":1}""");

        var meta = MetaJsonReader.TryRead(transcriptPath);

        Assert.NotNull(meta);
        Assert.Equal("general-purpose", meta!.AgentType);
        Assert.Equal(1, meta.SpawnDepth);
        Assert.Null(meta.ToolUseId);
        Assert.Null(meta.Description);
        Assert.Null(meta.Model);
        Assert.Null(meta.ParentAgentId);
    }

    [Fact]
    public void SiblingAbsent_ReturnsNull_NoThrow()
    {
        string transcriptPath = NewTranscriptPath();
        // Never write the sibling .meta.json file.

        var meta = MetaJsonReader.TryRead(transcriptPath);

        Assert.Null(meta);
    }

    [Fact]
    public void SiblingMalformed_ReturnsNull_NoThrow()
    {
        string transcriptPath = NewTranscriptPath();
        string metaPath = SiblingMetaPath(transcriptPath);
        File.WriteAllText(metaPath, "{ not valid json ][");

        var meta = MetaJsonReader.TryRead(transcriptPath);

        Assert.Null(meta);
    }

    [Fact]
    public void NullOrEmptyTranscriptPath_ReturnsNull_NoThrow()
    {
        Assert.Null(MetaJsonReader.TryRead(null));
        Assert.Null(MetaJsonReader.TryRead(string.Empty));
    }
}
