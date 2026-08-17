using System.Text.Json;
using Accel.Metrics;
using Xunit;

namespace Accel.Tests;

public class TranscriptHeadReaderTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string NewTempFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"accel-transcript-head-{Guid.NewGuid():N}.jsonl");
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

    private static string ModeLine(string sessionId = "x") =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["type"] = "mode",
            ["mode"] = "normal",
            ["sessionId"] = sessionId,
        });

    private static string UserStringLine(string content, string? cwd = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["message"] = new Dictionary<string, object?>
            {
                ["content"] = content,
            },
        };

        if (cwd is not null)
        {
            payload["cwd"] = cwd;
        }

        return JsonSerializer.Serialize(payload);
    }

    private static string UserArrayLine(string text, string? cwd = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["type"] = "user",
            ["message"] = new Dictionary<string, object?>
            {
                ["content"] = new object?[]
                {
                    new Dictionary<string, object?> { ["type"] = "text", ["text"] = text },
                },
            },
        };

        if (cwd is not null)
        {
            payload["cwd"] = cwd;
        }

        return JsonSerializer.Serialize(payload);
    }

    [Fact]
    public void RealMachineShape_SkipsModeLineAndCommandMessage_FindsCwdAndRealLabel()
    {
        string path = NewTempFile();
        string content =
            ModeLine() + "\n" +
            UserStringLine("<command-message>caveman</command-message><command-name>/caveman</command-name>") + "\n" +
            UserStringLine("Hello world", cwd: @"C:\projects") + "\n";
        File.WriteAllText(path, content);

        var info = TranscriptHeadReader.Read(path);

        Assert.Equal(@"C:\projects", info.Cwd);
        Assert.Equal("Hello world", info.FirstUserMessageText);
        Assert.Equal("Hello world", TranscriptHeadReader.DeriveLabel(info.FirstUserMessageText));
    }

    [Fact]
    public void ArrayShapedContent_FirstTextBlock_IsExtracted()
    {
        string path = NewTempFile();
        string content =
            ModeLine() + "\n" +
            UserArrayLine("Array-shaped message", cwd: @"C:\projects") + "\n";
        File.WriteAllText(path, content);

        var info = TranscriptHeadReader.Read(path);

        Assert.Equal("Array-shaped message", info.FirstUserMessageText);
        Assert.Equal(@"C:\projects", info.Cwd);
    }

    [Theory]
    [InlineData("<command-message>foo</command-message>")]
    [InlineData("<command-name>/foo</command-name>")]
    [InlineData("<local-command-stdout>output</local-command-stdout>")]
    [InlineData("<system-reminder>reminder text</system-reminder>")]
    [InlineData("[Request interrupted by user]")]
    public void SkipPrefix_SoleCandidate_FallsThroughToNull(string wrapperText)
    {
        string path = NewTempFile();
        string content =
            ModeLine() + "\n" +
            UserStringLine(wrapperText) + "\n";
        File.WriteAllText(path, content);

        var info = TranscriptHeadReader.Read(path);

        Assert.Null(info.FirstUserMessageText);
    }

    [Theory]
    [InlineData("<command-message>foo</command-message>")]
    [InlineData("<command-name>/foo</command-name>")]
    [InlineData("<local-command-stdout>output</local-command-stdout>")]
    [InlineData("<system-reminder>reminder text</system-reminder>")]
    [InlineData("[Request interrupted by user]")]
    public void SkipPrefix_FollowedByValidCandidate_UsesNextOne(string wrapperText)
    {
        string path = NewTempFile();
        string content =
            ModeLine() + "\n" +
            UserStringLine(wrapperText) + "\n" +
            UserStringLine("Real prompt text") + "\n";
        File.WriteAllText(path, content);

        var info = TranscriptHeadReader.Read(path);

        Assert.Equal("Real prompt text", info.FirstUserMessageText);
    }

    [Fact]
    public void SkillInvocation_SkipsInjectedSkillBody_FindsGenuineFirstUserRequest()
    {
        // Reproduces the real sequence observed on this machine when a user invokes a skill
        // (e.g. "/caveman"): Claude Code injects the skill's full body text as a SEPARATE
        // "type":"user" entry immediately after the <command-message>/<command-name> entry -
        // plain text starting with "Base directory for this skill:", not covered by the
        // original 5-prefix skip-list, so it used to win as the derived label instead of the
        // user's real first request. Modeled on the real shape in
        // C:\Users\a.sintes\.claude\projects\C--projects\3e7a5e3e-3210-41ef-be36-3604b2b101a7.jsonl
        // (line 4: <command-message>caveman</command-message>... -> line 5, its direct child,
        // isMeta:true, "Base directory for this skill: ..." -> real request only at line 17).
        string path = NewTempFile();
        string content =
            ModeLine() + "\n" +
            UserStringLine("<command-message>caveman</command-message><command-name>/caveman</command-name><command-args>lite</command-args>") + "\n" +
            UserArrayLine("Base directory for this skill: C:\\projects\\.claude\\skills\\caveman\n\nRespond terse like smart caveman. ...") + "\n" +
            UserStringLine("Focus on swgen2 repository, backend folder.") + "\n";
        File.WriteAllText(path, content);

        var info = TranscriptHeadReader.Read(path);

        Assert.Equal("Focus on swgen2 repository, backend folder.", info.FirstUserMessageText);
    }

    [Fact]
    public void MissingFile_ReturnsNullFields_NoThrow()
    {
        string path = Path.Combine(Path.GetTempPath(), $"accel-head-does-not-exist-{Guid.NewGuid():N}.jsonl");

        var info = TranscriptHeadReader.Read(path);

        Assert.Null(info.Cwd);
        Assert.Null(info.FirstUserMessageText);
    }

    [Fact]
    public void EmptyFile_ReturnsNullFields_NoThrow()
    {
        string path = NewTempFile();
        File.WriteAllText(path, string.Empty);

        var info = TranscriptHeadReader.Read(path);

        Assert.Null(info.Cwd);
        Assert.Null(info.FirstUserMessageText);
    }

    [Fact]
    public void GarbageOnlyFile_ReturnsNullFields_NoThrow()
    {
        string path = NewTempFile();
        File.WriteAllText(path, "not json at all\n{ broken\n][garbage\n");

        var info = TranscriptHeadReader.Read(path);

        Assert.Null(info.Cwd);
        Assert.Null(info.FirstUserMessageText);
    }

    [Fact]
    public void CwdWrongJsonType_DegradesToNull_NoThrow()
    {
        string path = NewTempFile();
        string content =
            ModeLine() + "\n" +
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "user",
                ["cwd"] = 123,
                ["message"] = new Dictionary<string, object?> { ["content"] = "hi" },
            }) + "\n";
        File.WriteAllText(path, content);

        var info = TranscriptHeadReader.Read(path);

        Assert.Null(info.Cwd);
        Assert.Equal("hi", info.FirstUserMessageText);
    }

    [Fact]
    public void MessageContentIsNumber_DegradesToNull_NoThrow()
    {
        string path = NewTempFile();
        string content =
            ModeLine() + "\n" +
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "user",
                ["message"] = new Dictionary<string, object?> { ["content"] = 42 },
            }) + "\n";
        File.WriteAllText(path, content);

        var info = TranscriptHeadReader.Read(path);

        Assert.Null(info.FirstUserMessageText);
    }

    [Fact]
    public void ContentArrayFirstElementLacksType_DegradesToNull_NoThrow()
    {
        string path = NewTempFile();
        string content =
            ModeLine() + "\n" +
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "user",
                ["message"] = new Dictionary<string, object?>
                {
                    ["content"] = new object?[]
                    {
                        new Dictionary<string, object?> { ["text"] = "no type field" },
                    },
                },
            }) + "\n";
        File.WriteAllText(path, content);

        var info = TranscriptHeadReader.Read(path);

        Assert.Null(info.FirstUserMessageText);
    }

    [Fact]
    public void NoCwdAnywhereInHeadWindow_ReturnsNullCwd_NoThrow()
    {
        string path = NewTempFile();
        string content =
            ModeLine() + "\n" +
            UserStringLine("Hello world") + "\n";
        File.WriteAllText(path, content);

        var info = TranscriptHeadReader.Read(path);

        Assert.Null(info.Cwd);
        Assert.Equal("Hello world", info.FirstUserMessageText);
    }

    [Fact]
    public void NullOrEmptyPath_ReturnsNullFields_NoThrow()
    {
        var infoNull = TranscriptHeadReader.Read(null);
        var infoEmpty = TranscriptHeadReader.Read(string.Empty);

        Assert.Null(infoNull.Cwd);
        Assert.Null(infoNull.FirstUserMessageText);
        Assert.Null(infoEmpty.Cwd);
        Assert.Null(infoEmpty.FirstUserMessageText);
    }

    [Fact]
    public void LargeFile_BoundedHeadRead_DataBeyondBoundIsNotFound()
    {
        string path = NewTempFile();

        using (var writer = new StreamWriter(path))
        {
            // Write well over 64KB of junk lines (non-JSON) before the real cwd/user entry, to
            // prove the bounded head-read (first ~64KB) actually works rather than scanning
            // the whole file.
            string junkLine = new string('x', 200);
            for (int i = 0; i < 1000; i++)
            {
                writer.WriteLine($"junk-{i}-{junkLine}");
            }

            writer.WriteLine(UserStringLine("Hello world", cwd: @"C:\projects"));
        }

        long fileSize = new FileInfo(path).Length;
        Assert.True(fileSize > 64 * 1024, "Fixture file must exceed the 64KB head-read bound for this test to be meaningful.");

        var info = TranscriptHeadReader.Read(path);

        Assert.Null(info.Cwd);
        Assert.Null(info.FirstUserMessageText);
    }

    [Fact]
    public void TrailingIncompleteLine_AtHeadBoundary_IsDiscarded_PriorEntryStillUsable()
    {
        string path = NewTempFile();

        using (var writer = new StreamWriter(path))
        {
            writer.WriteLine(ModeLine());
            writer.WriteLine(UserStringLine("Hello world", cwd: @"C:\projects"));

            // Pad past the 64KB boundary with junk lines, then leave a final partial line with
            // no trailing newline to simulate a write-in-progress right at/after the boundary.
            string junkLine = new string('y', 200);
            for (int i = 0; i < 1000; i++)
            {
                writer.WriteLine($"junk-{i}-{junkLine}");
            }

            writer.Write("""{"type":"user","message":{"content":"cut off mid-wri""");
        }

        var info = TranscriptHeadReader.Read(path);

        Assert.Equal(@"C:\projects", info.Cwd);
        Assert.Equal("Hello world", info.FirstUserMessageText);
    }

    // ---- DeriveLabel ----

    [Fact]
    public void DeriveLabel_ShortInput_PassesThroughUnchanged()
    {
        Assert.Equal("Hello world", TranscriptHeadReader.DeriveLabel("Hello world"));
    }

    [Fact]
    public void DeriveLabel_LongInput_TruncatesAtWordBoundary()
    {
        string input = "The quick brown fox jumps over the lazy dog while several onlookers watch in amazement";

        string? label = TranscriptHeadReader.DeriveLabel(input);

        Assert.NotNull(label);
        Assert.True(label!.Length <= 60);
        Assert.False(label.EndsWith(' '));
        Assert.StartsWith(label, input, StringComparison.Ordinal);
        // Confirm it did not cut mid-word: the char right after the label in the original text
        // must be a space (or end of string).
        char next = input[label.Length];
        Assert.True(next == ' ');
    }

    [Fact]
    public void DeriveLabel_WhitespaceNewlinesTabsControlChars_AreCollapsedAndStripped()
    {
        string input = "Hello\n\tworld\r\n  again\u0007done";

        string? label = TranscriptHeadReader.DeriveLabel(input);

        Assert.Equal("Hello world againdone", label);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n  \r")]
    public void DeriveLabel_NullOrEmptyOrWhitespaceOnly_ReturnsNull(string? input)
    {
        Assert.Null(TranscriptHeadReader.DeriveLabel(input));
    }
}
