using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Accel.Cli;
using Accel.Settings;
using Xunit;

namespace Accel.Tests;

/// <summary>
/// Phase 5. The invariant under test everywhere below: <c>accel statusline</c> stdout <i>is</i>
/// the user's status bar, so it must always be non-empty and the command must always exit 0 —
/// server down, chained command broken, stdin garbage, no exceptions.
/// </summary>
public class StatusLineCommandTests
{
    /// <summary>A port nothing is listening on — stands in for "Accel server is not running".</summary>
    private const int ClosedPort = 40119;

    private const string SamplePayload = """
        {
          "session_id": "abc-123",
          "model": { "id": "claude-opus-5", "display_name": "Opus 5" },
          "workspace": { "current_dir": "C:\\projects\\Accel" },
          "version": "2.1.224"
        }
        """;

    // ---- no prior chained command ---------------------------------------------------

    [Fact]
    public async Task NoChainedCommand_SynthesizesDefaultLineFromStdin()
    {
        var (exit, output) = await RunAsync(SamplePayload, new InMemoryStatusLineChainStore());

        Assert.Equal(0, exit);
        Assert.False(string.IsNullOrWhiteSpace(output));
        Assert.Contains("Opus 5", output);
        Assert.Contains(@"C:\projects\Accel", output);
    }

    [Fact]
    public async Task NoChainStoreAtAll_StillPrintsSomething()
    {
        var (exit, output) = await RunAsync(SamplePayload, chainStore: null);

        Assert.Equal(0, exit);
        Assert.Contains("Opus 5", output);
    }

    [Fact]
    public async Task CaptureOfNothing_IsTreatedAsNoChain()
    {
        // HadOriginal == false is materially different from "not captured": fresh install where
        // the field simply did not exist. Must still print the synthesized default.
        var store = new InMemoryStatusLineChainStore();
        store.Save(StatusLineField.StatusLine, StatusLineCapture.None);

        var (exit, output) = await RunAsync(SamplePayload, store);

        Assert.Equal(0, exit);
        Assert.Contains("Opus 5", output);
    }

    // ---- chained command relayed verbatim -------------------------------------------

    [Fact]
    public async Task ChainedCommand_OutputRelayedVerbatim()
    {
        var store = StoreWith("echo ACCEL-CHAIN-MARKER");

        var (exit, output) = await RunAsync(SamplePayload, store);

        Assert.Equal(0, exit);

        // Byte-for-byte: whatever the shell produced, including its trailing newline.
        Assert.Equal("ACCEL-CHAIN-MARKER" + Environment.NewLine, output);

        // And never our own synthesized text.
        Assert.DoesNotContain("Opus 5", output);
    }

    [Fact]
    public async Task ChainedCommand_ReceivesTheSameStdinBuffer()
    {
        // The original status-line process is long gone and cannot re-read stdin, so Accel must
        // re-feed the exact buffer it consumed. `findstr` only matches if the payload arrived.
        var store = StoreWith("findstr /c:\"claude-opus-5\"");

        var (exit, output) = await RunAsync(SamplePayload, store);

        Assert.Equal(0, exit);
        Assert.Contains("claude-opus-5", output);
    }

    [Fact]
    public async Task ChainedCommand_OutputIsNeverParsedOrRewritten()
    {
        // Chained stdout is opaque display text (may be ANSI-coloured, truncated, or produced
        // by an unrelated third-party script) — relayed as-is, never parsed for metrics.
        var store = StoreWith("echo [32mOK[0m tokens=999");

        var (exit, output) = await RunAsync(SamplePayload, store);

        Assert.Equal(0, exit);
        Assert.Contains("OK", output);
        Assert.DoesNotContain("Opus 5", output);
    }

    // ---- malformed / empty stdin ----------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"model\":")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    public async Task MalformedOrEmptyStdin_StillProducesNonEmptyOutput(string payload)
    {
        var (exit, output) = await RunAsync(payload, new InMemoryStatusLineChainStore());

        Assert.Equal(0, exit);
        Assert.False(string.IsNullOrWhiteSpace(output));
    }

    [Fact]
    public async Task PartialJson_UsesWhicheverFieldsItCouldFind()
    {
        var (_, output) = await RunAsync(
            """{"model":{"display_name":"Haiku 4.5"}}""",
            new InMemoryStatusLineChainStore());

        Assert.Contains("Haiku 4.5", output);
    }

    // ---- failing / hanging chained command ------------------------------------------

    [Fact]
    public async Task ChainedCommandFails_FallsBackToSynthesizedDefault()
    {
        var store = StoreWith("exit /b 3");

        var (exit, output) = await RunAsync(SamplePayload, store);

        Assert.Equal(0, exit);
        Assert.Contains("Opus 5", output);
    }

    [Fact]
    public async Task ChainedCommandDoesNotExist_FallsBackToSynthesizedDefault()
    {
        var store = StoreWith("accel-no-such-executable-xyz --nope");

        var (exit, output) = await RunAsync(SamplePayload, store);

        Assert.Equal(0, exit);
        Assert.Contains("Opus 5", output);
    }

    [Fact]
    public async Task ChainedCommandPrintsNothing_FallsBackToSynthesizedDefault()
    {
        var store = StoreWith("cd .");

        var (exit, output) = await RunAsync(SamplePayload, store);

        Assert.Equal(0, exit);
        Assert.False(string.IsNullOrWhiteSpace(output));
        Assert.Contains("Opus 5", output);
    }

    [Fact]
    public async Task ChainedCommandHangs_TimesOutAndFallsBack()
    {
        // 3 s of ping vs a 300 ms budget: must be killed and replaced by the default line.
        var store = StoreWith("ping -n 4 127.0.0.1 > NUL");

        var stopwatch = Stopwatch.StartNew();
        var (exit, output) = await RunAsync(
            SamplePayload,
            store,
            chainedTimeout: TimeSpan.FromMilliseconds(300));
        stopwatch.Stop();

        Assert.Equal(0, exit);
        Assert.Contains("Opus 5", output);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task MalformedCaptureNode_DoesNotThrow()
    {
        // A capture whose stored node is not a {type, command} object at all.
        var store = new InMemoryStatusLineChainStore();
        store.Save(StatusLineField.StatusLine, StatusLineCapture.Capture(JsonValue.Create("garbage")));

        var (exit, output) = await RunAsync(SamplePayload, store);

        Assert.Equal(0, exit);
        Assert.Contains("Opus 5", output);
    }

    // ---- fire-and-forget POST -------------------------------------------------------

    [Fact]
    public async Task DeadServer_DoesNotDelayOrBreakOutput()
    {
        var stopwatch = Stopwatch.StartNew();
        var (exit, output) = await RunAsync(SamplePayload, new InMemoryStatusLineChainStore());
        stopwatch.Stop();

        Assert.Equal(0, exit);
        Assert.Contains("Opus 5", output);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task DeadServer_DoesNotDelayTheChainedOutputEither()
    {
        var store = StoreWith("echo CHAINED");

        var stopwatch = Stopwatch.StartNew();
        var (exit, output) = await RunAsync(SamplePayload, store);
        stopwatch.Stop();

        Assert.Equal(0, exit);
        Assert.Equal("CHAINED" + Environment.NewLine, output);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3), $"took {stopwatch.Elapsed}");
    }

    // ---- always exit 0 --------------------------------------------------------------

    [Fact]
    public async Task AlwaysExitsZero_AcrossEveryScenario()
    {
        var scenarios = new (string Payload, IStatusLineChainStore? Store)[]
        {
            (SamplePayload, null),
            (SamplePayload, new InMemoryStatusLineChainStore()),
            (string.Empty, new InMemoryStatusLineChainStore()),
            ("}{", StoreWith("echo x")),
            (SamplePayload, StoreWith("exit /b 9")),
            (SamplePayload, StoreWith("accel-no-such-executable-xyz")),
        };

        foreach (var (payload, store) in scenarios)
        {
            var (exit, output) = await RunAsync(payload, store);

            Assert.Equal(0, exit);
            Assert.False(string.IsNullOrWhiteSpace(output));
        }
    }

    [Fact]
    public async Task UnwritableOutputStream_StillExitsZero()
    {
        var options = new StatusLineCommandOptions
        {
            Port = ClosedPort,
            ChainStore = new InMemoryStatusLineChainStore(),
            Input = new MemoryStream(Encoding.UTF8.GetBytes(SamplePayload)),
            Output = new ThrowingStream(),
            PostCompletionGrace = TimeSpan.Zero,
        };

        var exit = await StatusLineCommand.RunAsync(options);

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task ThrowingInputStream_StillExitsZeroAndPrints()
    {
        var options = new StatusLineCommandOptions
        {
            Port = ClosedPort,
            ChainStore = new InMemoryStatusLineChainStore(),
            Input = new ThrowingStream(),
            Output = new MemoryStream(),
            PostCompletionGrace = TimeSpan.Zero,
        };

        var exit = await StatusLineCommand.RunAsync(options);

        Assert.Equal(0, exit);
        Assert.NotEmpty(((MemoryStream)options.Output!).ToArray());
    }

    // ---- default-line synthesis unit checks -----------------------------------------

    [Fact]
    public void SynthesizeDefaultLine_NeverEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(StatusLineCommand.SynthesizeDefaultLine((string?)null)));
        Assert.False(string.IsNullOrWhiteSpace(StatusLineCommand.SynthesizeDefaultLine(string.Empty)));
        Assert.False(string.IsNullOrWhiteSpace(StatusLineCommand.SynthesizeDefaultLine("[]")));
        Assert.False(string.IsNullOrWhiteSpace(StatusLineCommand.SynthesizeDefaultLine((byte[]?)null)));
    }

    [Fact]
    public void SynthesizeDefaultLine_FallsBackToModelIdWhenNoDisplayName()
    {
        var line = StatusLineCommand.SynthesizeDefaultLine("""{"model":{"id":"claude-opus-5"},"cwd":"C:\\tmp"}""");

        Assert.Contains("claude-opus-5", line);
        Assert.Contains(@"C:\tmp", line);
    }

    // ---- helpers --------------------------------------------------------------------

    private static InMemoryStatusLineChainStore StoreWith(string command)
    {
        var store = new InMemoryStatusLineChainStore();
        store.Save(
            StatusLineField.StatusLine,
            StatusLineCapture.Capture(new JsonObject { ["type"] = "command", ["command"] = command }));
        return store;
    }

    private static async Task<(int Exit, string Output)> RunAsync(
        string payload,
        IStatusLineChainStore? chainStore,
        TimeSpan? chainedTimeout = null)
    {
        var output = new MemoryStream();
        var options = new StatusLineCommandOptions
        {
            // Nothing is listening here: proves the POST is fire-and-forget.
            Port = ClosedPort,
            ChainStore = chainStore,
            Input = new MemoryStream(Encoding.UTF8.GetBytes(payload)),
            Output = output,
            ChainedCommandTimeout = chainedTimeout ?? TimeSpan.FromSeconds(2),
            PostCompletionGrace = TimeSpan.Zero,
        };

        var exit = await StatusLineCommand.RunAsync(options);
        return (exit, Encoding.UTF8.GetString(output.ToArray()));
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new IOException("boom");

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("boom");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("boom");
    }
}
