using System.Text.Json;
using Glaude.Metrics;
using Xunit;

namespace Glaude.Tests;

public class TranscriptReaderTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string NewTempFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"glaude-transcript-{Guid.NewGuid():N}.jsonl");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch { /* best effort cleanup */ }
        }
    }

    // Builds one "type":"assistant" JSONL line via System.Text.Json (rather than hand-rolled
    // string interpolation) so the fixture JSON is guaranteed well-formed.
    private static string AssistantLine(string model, string? effortLevel, int input, int output, int cacheCreate = 0, int cacheRead = 0)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "assistant",
            ["message"] = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["usage"] = new Dictionary<string, object?>
                {
                    ["input_tokens"] = input,
                    ["output_tokens"] = output,
                    ["cache_creation_input_tokens"] = cacheCreate,
                    ["cache_read_input_tokens"] = cacheRead,
                },
            },
        };

        if (effortLevel is not null)
        {
            payload["effort"] = new Dictionary<string, object?> { ["level"] = effortLevel };
        }

        return JsonSerializer.Serialize(payload);
    }

    [Fact]
    public void OneCleanAssistantEntry_ExtractsFields()
    {
        string path = NewTempFile();
        File.WriteAllText(path, AssistantLine("claude-sonnet-5", "medium", 100, 20, 5, 3) + "\n");

        var entry = TranscriptReader.TryReadLastAssistantEntry(path);

        Assert.NotNull(entry);
        Assert.Equal("claude-sonnet-5", entry!.Model);
        Assert.Equal("medium", entry.EffortLevel);
        Assert.Equal(100, entry.InputTokens);
        Assert.Equal(20, entry.OutputTokens);
        Assert.Equal(5, entry.CacheCreationInputTokens);
        Assert.Equal(3, entry.CacheReadInputTokens);
    }

    [Fact]
    public void MultipleEntries_ReturnsLastOne()
    {
        string path = NewTempFile();
        string content =
            AssistantLine("claude-opus-5", "high", 1, 1) + "\n" +
            AssistantLine("claude-sonnet-5", "medium", 2, 2) + "\n" +
            AssistantLine("claude-haiku-4-5-20251001", "low", 3, 3) + "\n";
        File.WriteAllText(path, content);

        var entry = TranscriptReader.TryReadLastAssistantEntry(path);

        Assert.NotNull(entry);
        Assert.Equal("claude-haiku-4-5-20251001", entry!.Model);
        Assert.Equal("low", entry.EffortLevel);
        Assert.Equal(3, entry.InputTokens);
    }

    [Fact]
    public void TrailingIncompleteLine_IsSkipped_PriorEntryStillReturned()
    {
        string path = NewTempFile();
        string content =
            AssistantLine("claude-sonnet-5", "medium", 42, 7) + "\n" +
            """{"type":"assistant","message":{"model":"claude-opus-5","usage":{"input_tokens":9""";
        // Note: no trailing newline - simulates a partial write in progress.
        File.WriteAllText(path, content);

        var entry = TranscriptReader.TryReadLastAssistantEntry(path);

        Assert.NotNull(entry);
        Assert.Equal("claude-sonnet-5", entry!.Model);
        Assert.Equal(42, entry.InputTokens);
    }

    [Fact]
    public void MissingFile_ReturnsNoData_NoThrow()
    {
        string path = Path.Combine(Path.GetTempPath(), $"glaude-does-not-exist-{Guid.NewGuid():N}.jsonl");

        var entry = TranscriptReader.TryReadLastAssistantEntry(path);

        Assert.Null(entry);
    }

    [Fact]
    public void EmptyFile_ReturnsNoData_NoThrow()
    {
        string path = NewTempFile();
        File.WriteAllText(path, string.Empty);

        var entry = TranscriptReader.TryReadLastAssistantEntry(path);

        Assert.Null(entry);
    }

    [Fact]
    public void NullOrEmptyPath_ReturnsNoData_NoThrow()
    {
        Assert.Null(TranscriptReader.TryReadLastAssistantEntry(null));
        Assert.Null(TranscriptReader.TryReadLastAssistantEntry(string.Empty));
    }

    [Fact]
    public void LargeFile_BoundedTailRead_StillFindsTrailingEntry()
    {
        string path = NewTempFile();

        using (var writer = new StreamWriter(path))
        {
            // Write well over 64KB of junk lines (non-JSON) before the real entry, to prove
            // the bounded tail-read (last ~64KB) actually works rather than scanning the
            // whole file.
            string junkLine = new string('x', 200);
            for (int i = 0; i < 1000; i++)
            {
                writer.WriteLine($"junk-{i}-{junkLine}");
            }

            writer.Write(AssistantLine("claude-opus-5", "high", 555, 66));
            writer.Write('\n');
        }

        long fileSize = new FileInfo(path).Length;
        Assert.True(fileSize > 64 * 1024, "Fixture file must exceed the 64KB tail-read bound for this test to be meaningful.");

        var entry = TranscriptReader.TryReadLastAssistantEntry(path);

        Assert.NotNull(entry);
        Assert.Equal("claude-opus-5", entry!.Model);
        Assert.Equal(555, entry.InputTokens);
    }

    [Fact]
    public void MalformedNonJsonLinesInterspersed_AreSkipped_ValidEntriesParsed()
    {
        string path = NewTempFile();
        string content =
            "not json at all\n" +
            AssistantLine("claude-sonnet-5", "medium", 11, 2) + "\n" +
            "{ this is broken json\n" +
            """{"type":"user","message":{"model":"should-be-ignored"}}""" + "\n" +
            AssistantLine("claude-opus-5", "high", 33, 4) + "\n" +
            "another garbage line ][\n";
        File.WriteAllText(path, content);

        var entry = TranscriptReader.TryReadLastAssistantEntry(path);

        Assert.NotNull(entry);
        Assert.Equal("claude-opus-5", entry!.Model);
        Assert.Equal(33, entry.InputTokens);
    }
}
