namespace Glaude.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Glaude.Orchestration;
using Xunit;

/// <summary>
/// Unit tests for everything in <see cref="PtySession"/> that is provable without a real OS process:
/// the stateful-UTF-8-decoder behaviour of <see cref="PtyOutputPump"/> (the correctness bug this task was
/// most at risk of), the pump's bounded-channel/backpressure and cancellation/teardown behaviour, argv
/// construction, and the launch-spec guards.
///
/// <para>The pump is deliberately constructed here against fake <see cref="Stream"/>s rather than a real
/// pseudoconsole: that is what makes "a multi-byte character split across two pipe reads" a deterministic
/// test instead of a timing accident. The live behaviour (a real child, real Job Object assignment, real
/// teardown) is covered by the hidden <c>pty-session-smoke-test</c> verb
/// (<see cref="PtySessionSmokeTest"/>), same split as <c>ConPtyTests</c> vs <c>pty-smoke-test</c>.</para>
/// </summary>
public class PtySessionTests
{
    // ---------------------------------------------------------------------------------------------
    // Stateful UTF-8 decoding across chunk boundaries. This is THE correctness requirement of the
    // pump: pipe reads land wherever bytes happen to be available, so a 2/3/4-byte character
    // regularly straddles two reads.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The load-bearing case: one 4-byte character (U+1F600, which is also a surrogate pair in UTF-16)
    /// delivered as two separate reads split at byte 2. A stateful decoder holds the incomplete sequence
    /// and completes it on the next read; per-chunk <c>Encoding.UTF8.GetString</c> would emit U+FFFD.
    /// </summary>
    [Fact]
    public async Task PumpDecodesAFourByteCharacterSplitAcrossTwoReads()
    {
        const string expected = "a\U0001F600b";
        var bytes = Encoding.UTF8.GetBytes(expected);
        Assert.Equal(6, bytes.Length); // 'a' + 4 bytes of emoji + 'b'

        // Split in the middle of the emoji: read 1 = "a" + first 2 emoji bytes, read 2 = the rest.
        var text = await PumpAll(new ChunkedStream(bytes[..3], bytes[3..]));

        Assert.Equal(expected, text);
        Assert.DoesNotContain('\uFFFD', text);

        // And the control: the naive per-chunk approach really does corrupt it, so the test above is
        // testing something real rather than a tautology.
        var naive = Encoding.UTF8.GetString(bytes[..3]) + Encoding.UTF8.GetString(bytes[3..]);
        Assert.NotEqual(expected, naive);
        Assert.Contains('\uFFFD', naive);
    }

    /// <summary>Every possible split point of a 4-byte character, plus the 2- and 3-byte cases: the
    /// decoded result must be identical regardless of where the reads happened to land.</summary>
    [Theory]
    [InlineData("\u00e9", 1)]                 // 2-byte: e-acute
    [InlineData("\u4e2d", 1)]                 // 3-byte: CJK
    [InlineData("\u4e2d", 2)]
    [InlineData("\U0001F600", 1)]             // 4-byte: emoji
    [InlineData("\U0001F600", 2)]
    [InlineData("\U0001F600", 3)]
    [InlineData("\u2500\u2502\u250c", 4)]     // box-drawing run, split mid-character
    public async Task PumpDecodesMultiByteCharactersSplitAtEveryByteBoundary(string character, int splitAt)
    {
        var payload = "start:" + character + ":end";
        var bytes = Encoding.UTF8.GetBytes(payload);
        var offset = Encoding.UTF8.GetByteCount("start:") + splitAt;

        var text = await PumpAll(new ChunkedStream(bytes[..offset], bytes[offset..]));

        Assert.Equal(payload, text);
        Assert.DoesNotContain('\uFFFD', text);
    }

    /// <summary>One byte at a time - the pathological case for a stateful decoder, and a realistic one
    /// for a pty that is echoing keystrokes.</summary>
    [Fact]
    public async Task PumpDecodesCorrectlyWhenEveryReadReturnsASingleByte()
    {
        const string expected = "\u4e2d\u6587 \U0001F600 \u2500\u2502 ok";
        var bytes = Encoding.UTF8.GetBytes(expected);
        var singleBytes = bytes.Select(b => new[] { b }).ToArray();

        var text = await PumpAll(new ChunkedStream(singleBytes));

        Assert.Equal(expected, text);
        Assert.DoesNotContain('\uFFFD', text);
    }

    /// <summary>A read that is nothing but the leading bytes of a multi-byte character decodes to zero
    /// characters, and the pump must publish nothing at all rather than an empty chunk (an empty chunk is
    /// a lie about the stream and would make consumers' "got data" logic wrong).</summary>
    [Fact]
    public async Task PumpPublishesNothingForAReadThatIsOnlyTheStartOfAMultiByteSequence()
    {
        var bytes = Encoding.UTF8.GetBytes("\U0001F600");
        var (chunks, pump) = await PumpChunks(new ChunkedStream(bytes[..2], bytes[2..]));

        Assert.DoesNotContain(string.Empty, chunks);
        Assert.Single(chunks);
        Assert.Equal("\U0001F600", chunks[0]);
        Assert.Equal(4, pump.BytesRead);
    }

    /// <summary>A stream that ends mid-character: the decoder is flushed exactly once at EOF, so the
    /// truncation surfaces as a single U+FFFD instead of the bytes being silently dropped.</summary>
    [Fact]
    public async Task PumpFlushesTheDecoderAtEofSoATruncatedTrailingSequenceIsNotSilentlyDropped()
    {
        var bytes = Encoding.UTF8.GetBytes("ok\U0001F600");
        var truncated = bytes[..(bytes.Length - 2)]; // "ok" + the first 2 of 4 emoji bytes

        var text = await PumpAll(new ChunkedStream(truncated));

        Assert.Equal("ok\uFFFD", text);
    }

    /// <summary>
    /// The same truncation, but ended by a <i>read failure</i> (the pty read end closed under the pump)
    /// rather than by a clean zero-length read. This is the real teardown shape - a child killed
    /// mid-emoji - and the two exits from the loop are different code paths, so the decoder flush has to
    /// be reached from both. Nothing may be dropped, nothing may fault the consumer.
    /// </summary>
    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(ObjectDisposedException))]
    [InlineData(typeof(OperationCanceledException))]
    public void PumpStillFlushesTheDecoderWhenTheStreamFailsMidMultiByteSequence(Type exceptionType)
    {
        Exception exception = exceptionType == typeof(IOException) ? new IOException("pipe broken")
            : exceptionType == typeof(ObjectDisposedException) ? new ObjectDisposedException("stream")
            : new OperationCanceledException();
        var bytes = Encoding.UTF8.GetBytes("ok\U0001F600");
        var source = new ThrowingStream(exception, bytes[..(bytes.Length - 2)]);
        var channel = Channel.CreateUnbounded<string>();
        var pump = new PtyOutputPump(source, channel.Writer, bufferSize: 16);

        pump.RunLoop(CancellationToken.None);

        Assert.Null(pump.Error);
        Assert.True(pump.SawEof);
        Assert.Equal("ok�", string.Concat(DrainSync(channel.Reader)));
        Assert.True(channel.Reader.Completion.IsCompletedSuccessfully);
    }

    /// <summary>
    /// Genuinely malformed UTF-8 - not a split valid sequence, but bytes that cannot be valid anywhere: a
    /// lone continuation byte, an overlong encoding, a UTF-8-encoded surrogate, 0xFF/0xFE (never legal in
    /// UTF-8), and a truncated sequence followed by ASCII. A buggy or hostile child can emit any of these,
    /// and the pump must substitute U+FFFD and keep going rather than throw: <see cref="Encoding.UTF8"/>
    /// carries a <see cref="DecoderReplacementFallback"/>, so a
    /// <see cref="DecoderFallbackException"/> is impossible here - but only as long as the decoder comes
    /// from <see cref="Encoding.UTF8"/> and not from a <c>new UTF8Encoding(false, throwOnInvalidBytes: true)</c>,
    /// which is exactly what this test pins down.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x80 })]                                     // lone continuation byte
    [InlineData(new byte[] { 0xC0, 0x80 })]                               // overlong encoding of NUL
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })]                         // UTF-8-encoded surrogate D800
    [InlineData(new byte[] { 0xFF, 0xFE })]                               // never legal in UTF-8
    [InlineData(new byte[] { 0xF0, 0x9F, 0x41 })]                         // 4-byte lead, then ASCII
    [InlineData(new byte[] { 0xF5, 0x80, 0x80, 0x80 })]                   // above U+10FFFF
    [InlineData(new byte[] { 0xE0, 0x80, 0x80 })]                         // overlong 3-byte
    [InlineData(new byte[] { 0x41, 0xC3, 0x28, 0x42 })]                   // bad continuation between ASCII
    public async Task PumpSubstitutesReplacementCharactersForMalformedUtf8InsteadOfThrowing(byte[] malformed)
    {
        var payload = new byte[malformed.Length + 4];
        Encoding.UTF8.GetBytes("[").CopyTo(payload, 0);
        malformed.CopyTo(payload, 1);
        Encoding.UTF8.GetBytes("]ok").CopyTo(payload, malformed.Length + 1);

        var (chunks, pump) = await PumpChunks(new ChunkedStream(payload));
        var text = string.Concat(chunks);

        Assert.Null(pump.Error); // no DecoderFallbackException, no anything
        Assert.True(pump.SawEof);
        Assert.StartsWith("[", text, StringComparison.Ordinal);
        Assert.EndsWith("]ok", text, StringComparison.Ordinal);
        Assert.Contains('�', text);

        // The streaming decode must agree with a one-shot decode of the same bytes: whatever the
        // maximal-subpart rule decides, the pump must not add or lose replacement characters just
        // because the bytes arrived in one read rather than another.
        Assert.Equal(Encoding.UTF8.GetString(payload), text);
    }

    /// <summary>
    /// A brute-force version of the two tests above: random bytes (so a healthy share of them are invalid
    /// UTF-8), delivered in random-sized reads, through a read buffer small enough that the pump's own
    /// buffer keeps splitting sequences. Two invariants: the pump never throws, and the streamed decode is
    /// byte-for-byte identical to a one-shot decode of the whole input.
    ///
    /// <para>It also exercises the output-buffer sizing. The pump allocates
    /// <c>GetMaxCharCount(bufferSize) + 4</c> chars, and worst-case malformed input produces one U+FFFD per
    /// byte <i>plus</i> whatever the decoder was holding from the previous read - if that slack were wrong,
    /// <c>Decoder.GetChars</c> would throw <see cref="ArgumentException"/> here rather than silently
    /// truncate.</para>
    /// </summary>
    [Fact]
    public async Task PumpNeverThrowsAndMatchesAOneShotDecodeForRandomlySplitRandomBytes()
    {
        var random = new Random(20260814); // fixed seed: a failure must be reproducible
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var all = new byte[random.Next(1, 300)];
            random.NextBytes(all);

            var reads = new List<byte[]>();
            for (var offset = 0; offset < all.Length;)
            {
                var take = Math.Min(random.Next(1, 9), all.Length - offset);
                reads.Add(all[offset..(offset + take)]);
                offset += take;
            }

            var (chunks, pump) = await PumpChunks(new ChunkedStream(reads.ToArray()), bufferSize: 4);

            Assert.Null(pump.Error);
            Assert.Equal(all.Length, pump.BytesRead);
            Assert.Equal(Encoding.UTF8.GetString(all), string.Concat(chunks));
            Assert.DoesNotContain(string.Empty, chunks);
        }
    }

    /// <summary>
    /// Write-after-complete on the pump thread: if the channel is completed by anyone else (a consumer
    /// calling <c>Complete</c>, or <see cref="PtySession.Dispose"/> completing it after the pump join timed
    /// out), the pump's next write must be swallowed as "cannot publish any more" rather than throwing
    /// <see cref="ChannelClosedException"/> on a thread with no handler. The pump must still drain the pipe
    /// to EOF, because that is what stops <c>ClosePseudoConsole</c> wedging.
    /// </summary>
    [Fact]
    public void PumpSurvivesTheChannelBeingCompletedUnderneathItAndStillDrainsToEof()
    {
        var chunks = Enumerable.Range(0, 20).Select(_ => Encoding.UTF8.GetBytes("cccc")).ToArray();
        var channel = Channel.CreateUnbounded<string>();
        var pump = new PtyOutputPump(new ChunkedStream(chunks), channel.Writer, bufferSize: 4);

        channel.Writer.Complete(); // the race, made deterministic

        pump.RunLoop(CancellationToken.None); // must not throw

        Assert.Null(pump.Error);
        Assert.True(pump.SawEof);
        Assert.Equal(chunks.Length * 4, pump.BytesRead);
        Assert.Equal(0, pump.ChunksPublished);
        Assert.True(pump.ChunksDiscarded > 0);
    }

    /// <summary>A chunk larger than the read buffer is split by the pump itself, which is the same
    /// boundary problem from the other direction.</summary>
    [Fact]
    public async Task PumpDecodesCorrectlyWhenItsOwnReadBufferSplitsACharacter()
    {
        // 7 bytes of ASCII then a 4-byte emoji, read with an 8-byte buffer: the buffer boundary lands
        // inside the emoji no matter what the source stream does.
        var expected = "AAAAAAA\U0001F600BBBB";
        var bytes = Encoding.UTF8.GetBytes(expected);

        var text = await PumpAll(new ChunkedStream(bytes), bufferSize: 8);

        Assert.Equal(expected, text);
        Assert.DoesNotContain('\uFFFD', text);
    }

    // ---------------------------------------------------------------------------------------------
    // Backpressure, cancellation and completion.
    // ---------------------------------------------------------------------------------------------

    // xUnit1031 (no blocking task operations) is suppressed for the two backpressure tests below on
    // purpose: what they assert IS that the pump thread is *blocked* at a given moment, and the negative
    // Wait() with a timeout is the only way to observe that. The waits are all bounded and the pump runs
    // on its own Task, so there is no deadlock risk - only a bounded failure if the behaviour regresses.
#pragma warning disable xUnit1031

    /// <summary>
    /// With a bounded channel and nobody reading, the pump must block rather than buffer: it may read at
    /// most capacity+1 chunks (capacity in the channel, one in its hand) and then stop pulling bytes.
    /// </summary>
    [Fact]
    public void PumpStopsReadingOnceTheBoundedChannelIsFullInsteadOfBufferingUnboundedly()
    {
        const int capacity = 2;
        const int chunkSize = 4;
        var chunks = Enumerable.Range(0, 100).Select(_ => Encoding.UTF8.GetBytes("aaaa")).ToArray();
        var source = new ChunkedStream(chunks);
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });

        var pump = new PtyOutputPump(source, channel.Writer, bufferSize: chunkSize);
        using var stop = new CancellationTokenSource();
        var loop = Task.Run(() => pump.RunLoop(stop.Token));

        // Give it every chance to over-read. The bound is what stops it, not the clock.
        Assert.False(loop.Wait(TimeSpan.FromMilliseconds(500)), "the pump should still be blocked on the full channel");
        Assert.True(
            pump.BytesRead <= (capacity + 1) * chunkSize,
            $"pump read {pump.BytesRead} bytes with a capacity of {capacity}; it must not read past capacity+1 chunks");
        Assert.Equal(0, pump.ChunksDiscarded);

        // Draining unblocks it; it then runs to EOF and completes the channel.
        var drained = Task.Run(async () =>
        {
            var total = 0;
            await foreach (var chunk in channel.Reader.ReadAllAsync())
            {
                total += chunk.Length;
            }

            return total;
        });

        Assert.True(loop.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(drained.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(chunks.Length * chunkSize, drained.Result);
        Assert.Equal(chunks.Length * chunkSize, pump.BytesRead);
        Assert.True(pump.SawEof);
    }

    /// <summary>
    /// The teardown escape hatch: cancelling the pump while it is blocked on a full channel must unblock
    /// it and flip it into drain-and-discard - it keeps reading to EOF (so <c>ClosePseudoConsole</c> can
    /// never wedge on an undrained pipe) but stops publishing. Without this, a stalled consumer would turn
    /// into a hung <see cref="PtySession.Dispose"/>.
    /// </summary>
    [Fact]
    public void CancellingAStalledPumpSwitchesItToDrainAndDiscardAndItStillReachesEof()
    {
        const int chunkSize = 4;
        var chunks = Enumerable.Range(0, 50).Select(_ => Encoding.UTF8.GetBytes("bbbb")).ToArray();
        var source = new ChunkedStream(chunks);
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
        });

        var pump = new PtyOutputPump(source, channel.Writer, bufferSize: chunkSize);
        using var stop = new CancellationTokenSource();
        var loop = Task.Run(() => pump.RunLoop(stop.Token));

        Assert.False(loop.Wait(TimeSpan.FromMilliseconds(300)), "the pump should be blocked on the full channel");
        var stalledBytes = pump.BytesRead;

        stop.Cancel();

        Assert.True(loop.Wait(TimeSpan.FromSeconds(10)), "a cancelled pump must not stay blocked");
        Assert.True(pump.SawEof, "the pump must keep reading to EOF even after cancellation");
        Assert.Equal(chunks.Length * chunkSize, pump.BytesRead);
        Assert.True(pump.BytesRead > stalledBytes);
        Assert.True(pump.ChunksDiscarded > 0, "the chunks read after cancellation must be discarded, not published");
        Assert.Null(pump.Error);

        // And the consumer's `await foreach` still ends: drain whatever was published before the
        // cancellation (Completion only signals once the channel is both completed and empty).
        while (channel.Reader.TryRead(out _))
        {
        }

        Assert.True(channel.Reader.Completion.Wait(TimeSpan.FromSeconds(5)));
    }

#pragma warning restore xUnit1031

    /// <summary>A pump cancelled before it starts publishes nothing but still drains to EOF and completes
    /// the channel - the "Dispose raced the launch" case.</summary>
    [Fact]
    public void APumpCancelledBeforeItStartsDiscardsEverythingAndStillCompletesTheChannel()
    {
        var source = new ChunkedStream(Encoding.UTF8.GetBytes("hello"));
        var channel = Channel.CreateUnbounded<string>();
        var pump = new PtyOutputPump(source, channel.Writer, bufferSize: 16);

        using var stop = new CancellationTokenSource();
        stop.Cancel();
        pump.RunLoop(stop.Token);

        Assert.True(pump.SawEof);
        Assert.Equal(5, pump.BytesRead);
        Assert.Equal(0, pump.ChunksPublished);
        Assert.Equal(1, pump.ChunksDiscarded);
        Assert.True(channel.Reader.Completion.IsCompletedSuccessfully);
        Assert.False(channel.Reader.TryRead(out _));
    }

    /// <summary>The pump completes the channel exactly once on every exit path, including the error path,
    /// so a consumer can never be left awaiting forever.</summary>
    [Fact]
    public void PumpCompletesTheChannelAndSurfacesAnUnexpectedErrorToTheConsumer()
    {
        var source = new ThrowingStream(new InvalidOperationException("boom"));
        var channel = Channel.CreateUnbounded<string>();
        var pump = new PtyOutputPump(source, channel.Writer, bufferSize: 16);

        pump.RunLoop(CancellationToken.None);

        Assert.NotNull(pump.Error);
        Assert.True(channel.Reader.Completion.IsFaulted);
    }

    /// <summary>An <see cref="IOException"/>/<see cref="ObjectDisposedException"/> from the read is the
    /// <i>normal</i> end of the loop (Dispose closes the read end while the pump is blocked in a read), so
    /// it must be treated as EOF, not surfaced as a fault to the consumer.</summary>
    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(ObjectDisposedException))]
    public void PumpTreatsATeardownReadFailureAsEofRatherThanAsAnError(Type exceptionType)
    {
        var exception = exceptionType == typeof(IOException)
            ? new IOException("pipe broken")
            : (Exception)new ObjectDisposedException("stream");
        var source = new ThrowingStream(exception, Encoding.UTF8.GetBytes("partial"));
        var channel = Channel.CreateUnbounded<string>();
        var pump = new PtyOutputPump(source, channel.Writer, bufferSize: 16);

        pump.RunLoop(CancellationToken.None);

        Assert.Null(pump.Error);
        Assert.True(pump.SawEof);
        Assert.True(channel.Reader.TryRead(out var chunk));
        Assert.Equal("partial", chunk);

        // ChannelReader.Completion completes once the writer is completed AND the channel is drained,
        // hence after the TryRead above rather than before it.
        Assert.True(channel.Reader.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public void PumpRejectsNullsAndNonPositiveBufferSizes()
    {
        var channel = Channel.CreateUnbounded<string>();
        Assert.Throws<ArgumentNullException>(() => new PtyOutputPump(null!, channel.Writer, 16));
        Assert.Throws<ArgumentNullException>(() => new PtyOutputPump(new ChunkedStream(), null!, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PtyOutputPump(new ChunkedStream(), channel.Writer, 0));
    }

    // ---------------------------------------------------------------------------------------------
    // Launch-spec / argv construction. The plan's hard requirement: a real argument array, never a
    // concatenated command string and never a shell.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void CommandLineIsBuiltFromTheArgvArrayWithNoShellAndNoCmdSlashC()
    {
        var spec = new PtyLaunchSpec
        {
            ExecutablePath = @"C:\Users\me\.local\bin\claude.exe",
            Arguments = new[] { "--session-id", "11111111-2222-3333-4444-555555555555", "--name", "my session" },
        };

        var commandLine = spec.BuildCommandLine();

        Assert.Equal(
            @"C:\Users\me\.local\bin\claude.exe --session-id 11111111-2222-3333-4444-555555555555 --name ""my session""",
            commandLine);

        // No shell anywhere: not the image, not the arguments.
        Assert.DoesNotContain("cmd.exe", commandLine, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/c", commandLine, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", commandLine, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("&", commandLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// Round-trip proof rather than string comparison: the command line, re-split by the same rules
    /// <c>CommandLineToArgvW</c>/the CRT use, must yield exactly the original argv - which is what
    /// guarantees a session name containing spaces, quotes or backslashes cannot become extra arguments.
    /// </summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("with spaces")]
    [InlineData("with\ttab")]
    [InlineData("")]
    [InlineData("quote\"inside")]
    [InlineData("\"fully quoted\"")]
    [InlineData(@"trailing\")]
    [InlineData(@"trailing\\")]
    [InlineData(@"back\slash mid")]
    [InlineData(@"C:\path with space\dir\")]
    [InlineData(@"ends with backslash before quote\""x")]
    [InlineData("--permission-mode bypassPermissions")]
    [InlineData("a b\" c\\\\\" d")]
    public void EveryArgumentRoundTripsThroughTheBuiltCommandLine(string argument)
    {
        var spec = new PtyLaunchSpec
        {
            ExecutablePath = @"C:\dir with space\claude.exe",
            Arguments = new[] { "--name", argument, "--tail" },
        };

        var parsed = SplitCommandLineLikeWindows(spec.BuildCommandLine());

        Assert.Equal(
            new[] { @"C:\dir with space\claude.exe", "--name", argument, "--tail" },
            parsed);
    }

    [Fact]
    public void AnEmptyArgumentSurvivesAsAnEmptyArgumentRatherThanVanishing()
    {
        var spec = new PtyLaunchSpec { ExecutablePath = "a.exe", Arguments = new[] { string.Empty, "x" } };

        Assert.Equal(@"a.exe """" x", spec.BuildCommandLine());
        Assert.Equal(new[] { "a.exe", string.Empty, "x" }, SplitCommandLineLikeWindows(spec.BuildCommandLine()));
    }

    [Fact]
    public void ADefaultSpecHasNoArgumentsNoWorkingDirectoryAndNoEnvironmentOverrides()
    {
        var spec = new PtyLaunchSpec { ExecutablePath = "a.exe" };

        Assert.Empty(spec.Arguments);
        Assert.Null(spec.WorkingDirectory);
        Assert.Null(spec.EnvironmentOverrides);
        Assert.Equal("a.exe", spec.BuildCommandLine());
    }

    // ---------------------------------------------------------------------------------------------
    // Launch guards: the shim case (locked-in decision 2) must fail loudly, never silently misbehave.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Roaming\npm\claude.cmd")]
    [InlineData(@"C:\Users\me\AppData\Roaming\npm\claude.bat")]
    [InlineData(@"C:\Users\me\AppData\Roaming\npm\claude.ps1")]
    [InlineData(@"C:\Users\me\AppData\Roaming\npm\CLAUDE.CMD")]
    public void ValidateRefusesAShimPathAndSaysWhy(string shimPath)
    {
        var spec = new PtyLaunchSpec { ExecutablePath = shimPath };

        var exception = Assert.Throws<PtySessionLaunchException>(spec.Validate);
        Assert.Contains("shim", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("node.exe", exception.Message, StringComparison.Ordinal);
        Assert.Contains(shimPath, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The shim guard must not be bypassable by spellings of the same path that Win32 resolves to the same
    /// file. Measured on this machine: <c>File.Exists(@"...\claude.cmd.")</c> and
    /// <c>File.Exists(@"...\claude.cmd ")</c> are both true, because Win32 path normalisation strips
    /// trailing dots and spaces from the last path component - while
    /// <see cref="Path.GetExtension(string)"/> reports <c>"."</c> and <c>".cmd "</c> respectively and so
    /// would not match <c>".cmd"</c>. Forward slashes and <c>..</c> segments do not change the extension
    /// but are covered here so a future path-normalisation change cannot silently open a hole.
    /// </summary>
    [Theory]
    [InlineData(@"C:\npm\claude.cmd.")]
    [InlineData(@"C:\npm\claude.cmd...")]
    [InlineData("C:\\npm\\claude.cmd ")]
    [InlineData("C:\\npm\\claude.CMD. ")]
    [InlineData("C:/npm/claude.bat")]
    [InlineData(@"C:\npm\..\npm\claude.ps1")]
    [InlineData(@"\\?\C:\npm\claude.cmd")]
    [InlineData(@"claude.cmd")]
    public void ValidateRefusesShimPathsSpelledSoTheExtensionCheckCouldBeEvaded(string shimPath)
    {
        var spec = new PtyLaunchSpec { ExecutablePath = shimPath };

        var exception = Assert.Throws<PtySessionLaunchException>(spec.Validate);
        Assert.Contains("shim", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The trailing-dot/space trimming must not start rejecting legitimate executables whose name
    /// merely contains a shim extension somewhere.</summary>
    [Theory]
    [InlineData(@"C:\bin\claude.exe")]
    [InlineData(@"C:\bin\claude.cmd.exe")]
    [InlineData(@"C:\cmd\claude.exe")]
    [InlineData(@"C:\bin\node.exe")]
    [InlineData(@"C:\bin\claude.exe ")]
    public void ValidateAcceptsNativeExecutablesThatOnlyLookLikeShims(string path)
    {
        new PtyLaunchSpec { ExecutablePath = path }.Validate();
    }

    [Fact]
    public void StartRefusesAShimBeforeAllocatingAnyOsResource()
    {
        // The path does not exist, so if the shim guard did not run first this would fail with a Win32
        // "cannot find the file" instead - i.e. the assertion is on the guard, not on the filesystem.
        var exception = Assert.Throws<PtySessionLaunchException>(() =>
            PtySession.Start(new PtyLaunchSpec { ExecutablePath = @"C:\nope\claude.cmd" }));
        Assert.Contains("shim", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateRefusesAnEmptyExecutablePath(string path)
    {
        var spec = new PtyLaunchSpec { ExecutablePath = path };
        Assert.Throws<PtySessionLaunchException>(spec.Validate);
    }

    [Fact]
    public void ValidateRefusesArgumentsContainingNulOrNull()
    {
        Assert.Throws<PtySessionLaunchException>(() =>
            new PtyLaunchSpec { ExecutablePath = "a.exe", Arguments = new[] { "ok\0--evil" } }.Validate());
        Assert.Throws<PtySessionLaunchException>(() =>
            new PtyLaunchSpec { ExecutablePath = "a.exe", Arguments = new[] { (string)null! } }.Validate());
    }

    [Fact]
    public void CreateClaudeSpecUsesTheResolvedNativeExeAsArgvZero()
    {
        var resolution = new ClaudeCliResolution(ClaudeCliResolutionKind.NativeExe, @"C:\bin\claude.exe");

        var spec = PtySession.CreateClaudeSpec(
            new[] { "--session-id", "abc" },
            workingDirectory: @"C:\work",
            environmentOverrides: null,
            resolution: resolution);

        Assert.Equal(@"C:\bin\claude.exe", spec.ExecutablePath);
        Assert.Equal(@"C:\work", spec.WorkingDirectory);
        Assert.Equal(@"C:\bin\claude.exe --session-id abc", spec.BuildCommandLine());
    }

    [Fact]
    public void CreateClaudeSpecFailsLoudlyForAShimResolution()
    {
        var resolution = new ClaudeCliResolution(ClaudeCliResolutionKind.Shim, @"C:\npm\claude.cmd");

        var exception = Assert.Throws<PtySessionLaunchException>(() =>
            PtySession.CreateClaudeSpec(Array.Empty<string>(), resolution: resolution));

        Assert.Contains("shim", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("node.exe", exception.Message, StringComparison.Ordinal);
        Assert.Contains(@"C:\npm\claude.cmd", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateClaudeSpecFailsLoudlyWhenClaudeIsMissing()
    {
        var resolution = new ClaudeCliResolution(ClaudeCliResolutionKind.Missing, null);

        var exception = Assert.Throws<PtySessionLaunchException>(() =>
            PtySession.CreateClaudeSpec(Array.Empty<string>(), resolution: resolution));

        Assert.Contains("not found on PATH", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionOptionsDefaultToAnEightyByTwentyFiveTerminalWithABoundedOutputChannel()
    {
        var options = new PtySessionOptions();

        Assert.Equal(80, options.Columns);
        Assert.Equal(25, options.Rows);
        Assert.True(options.OutputChannelCapacity > 0, "the output channel must be bounded");
        Assert.True(options.ReadBufferSize > 0);
        Assert.Null(options.JobObject); // null == GlaudeJobObject.Shared, resolved at Start
    }

    [Fact]
    public void StartValidatesOptionsBeforeSpawningAnything()
    {
        var spec = new PtyLaunchSpec { ExecutablePath = @"C:\nope\thing.exe" };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PtySession.Start(spec, new PtySessionOptions { Columns = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PtySession.Start(spec, new PtySessionOptions { OutputChannelCapacity = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PtySession.Start(spec, new PtySessionOptions { ReadBufferSize = 0 }));
        Assert.Throws<ArgumentNullException>(() => PtySession.Start(null!));
    }

    // ---------------------------------------------------------------------------------------------
    // Environment block marshalling (the ConPty addition PtyLaunchSpec.EnvironmentOverrides needs).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void NoEnvironmentOverridesMeansNoBlockAtAllSoTheChildInheritsOurs()
    {
        Assert.Null(ConPtySession.BuildEnvironmentBlock(null, BaseEnvironment()));
        Assert.Null(ConPtySession.BuildEnvironmentBlock(
            new Dictionary<string, string?>(), BaseEnvironment()));
    }

    [Fact]
    public void EnvironmentBlockMergesOverridesOntoTheBaseAndIsDoubleNulTerminated()
    {
        var block = ConPtySession.BuildEnvironmentBlock(
            new Dictionary<string, string?> { ["TERM"] = "xterm-256color", ["path"] = @"C:\new" },
            BaseEnvironment());

        Assert.NotNull(block);
        var entries = SplitEnvironmentBlock(block!);

        // Case-insensitive override: "path" replaces "PATH", it does not add a second entry.
        // The surviving entry keeps the base environment's spelling of the name, which is what Windows
        // itself does; what matters is that there is exactly one and it carries the new value.
        var pathEntries = entries.Where(e => e.StartsWith("path=", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Single(pathEntries);
        Assert.Equal(@"C:\new", pathEntries[0].Split('=', 2)[1]);
        Assert.Contains("TERM=xterm-256color", entries);
        Assert.Contains("EXISTING=1", entries);

        // Double NUL terminated, and only at the end.
        Assert.Equal('\0', block![^1]);
        Assert.Equal('\0', block[^2]);
    }

    [Fact]
    public void ANullOverrideValueRemovesTheVariable()
    {
        var block = ConPtySession.BuildEnvironmentBlock(
            new Dictionary<string, string?> { ["EXISTING"] = null, ["KEEP"] = "yes" },
            BaseEnvironment());

        var entries = SplitEnvironmentBlock(block!);

        Assert.DoesNotContain(entries, e => e.StartsWith("EXISTING=", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("KEEP=yes", entries);
    }

    [Fact]
    public void EnvironmentBlockEntriesAreSortedAsWindowsProducesThem()
    {
        var block = ConPtySession.BuildEnvironmentBlock(
            new Dictionary<string, string?> { ["zzz"] = "1", ["aaa"] = "2" },
            new[] { new KeyValuePair<string, string>("MMM", "3") });

        var entries = SplitEnvironmentBlock(block!);

        Assert.Equal(new[] { "aaa=2", "MMM=3", "zzz=1" }, entries);
    }

    [Theory]
    [InlineData("BAD=NAME", "v")]
    [InlineData("BAD\0NAME", "v")]
    [InlineData("", "v")]
    [InlineData("OK", "bad\0value")]
    public void EnvironmentBlockRejectsNamesOrValuesThatCannotBeRepresented(string name, string value)
    {
        Assert.Throws<ArgumentException>(() => ConPtySession.BuildEnvironmentBlock(
            new Dictionary<string, string?> { [name] = value },
            BaseEnvironment()));
    }

    [Fact]
    public void ConPtyLaunchSpecHasNoEnvironmentOverridesByDefault()
    {
        Assert.Null(new ConPtyLaunchSpec { CommandLine = "cmd.exe" }.EnvironmentOverrides);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    private static IEnumerable<KeyValuePair<string, string>> BaseEnvironment() => new[]
    {
        new KeyValuePair<string, string>("PATH", @"C:\old"),
        new KeyValuePair<string, string>("EXISTING", "1"),
    };

    private static string[] SplitEnvironmentBlock(char[] block)
    {
        var text = new string(block);
        Assert.EndsWith("\0\0", text);
        return text[..^1].Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Runs the pump to completion against <paramref name="source"/> and returns the whole
    /// concatenated decoded text.</summary>
    private static async Task<string> PumpAll(Stream source, int bufferSize = 4096)
    {
        var (chunks, _) = await PumpChunks(source, bufferSize);
        return string.Concat(chunks);
    }

    /// <summary>Drains an already-completed channel synchronously (no await, so it can be used from a
    /// non-async test).</summary>
    private static List<string> DrainSync(ChannelReader<string> reader)
    {
        var chunks = new List<string>();
        while (reader.TryRead(out var chunk))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private static async Task<(List<string> Chunks, PtyOutputPump Pump)> PumpChunks(Stream source, int bufferSize = 4096)
    {
        var channel = Channel.CreateUnbounded<string>();
        var pump = new PtyOutputPump(source, channel.Writer, bufferSize);

        // Synchronously on the test thread: unbounded channel, so it cannot block.
        pump.RunLoop(CancellationToken.None);

        var chunks = new List<string>();
        await foreach (var chunk in channel.Reader.ReadAllAsync())
        {
            chunks.Add(chunk);
        }

        return (chunks, pump);
    }

    /// <summary>
    /// A minimal re-implementation of the <c>CommandLineToArgvW</c> parsing rules, used to prove
    /// <see cref="PtyLaunchSpec.BuildCommandLine"/> round-trips. Deliberately independent of the quoting
    /// code under test (it is a parser, not the same algorithm run backwards).
    /// </summary>
    internal static string[] SplitCommandLineLikeWindows(string commandLine)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var started = false;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];

            if (c == '\\')
            {
                var backslashes = 0;
                while (i < commandLine.Length && commandLine[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i < commandLine.Length && commandLine[i] == '"')
                {
                    current.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 0)
                    {
                        inQuotes = !inQuotes;
                        started = true;
                    }
                    else
                    {
                        current.Append('"');
                    }
                }
                else
                {
                    current.Append('\\', backslashes);
                    i--;
                    started = true;
                }

                continue;
            }

            if (c == '"')
            {
                inQuotes = !inQuotes;
                started = true;
                continue;
            }

            if (!inQuotes && (c == ' ' || c == '\t'))
            {
                if (started)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    started = false;
                }

                continue;
            }

            current.Append(c);
            started = true;
        }

        if (started)
        {
            arguments.Add(current.ToString());
        }

        return arguments.ToArray();
    }

    /// <summary>A read-only stream that returns pre-decided chunks, one per <c>Read</c> call, then EOF -
    /// the fake pty. This is what makes "split at exactly this byte" deterministic.</summary>
    private sealed class ChunkedStream : Stream
    {
        private readonly Queue<byte[]> _chunks;
        private int _offsetInHead;

        public ChunkedStream(params byte[][] chunks)
        {
            _chunks = new Queue<byte[]>(chunks);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_chunks.Count == 0)
            {
                return 0;
            }

            // One chunk per Read at most - never merged, so a chunk boundary in the test data really is a
            // read boundary for the pump. A chunk larger than the caller's buffer is served across
            // several reads (which is how the "the pump's own buffer splits a character" case arises).
            var chunk = _chunks.Peek();
            var available = chunk.Length - _offsetInHead;
            var take = Math.Min(count, available);
            Array.Copy(chunk, _offsetInHead, buffer, offset, take);
            _offsetInHead += take;
            if (_offsetInHead == chunk.Length)
            {
                _chunks.Dequeue();
                _offsetInHead = 0;
            }

            return take;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Returns optional data, then throws - models the pty read end being closed under the pump.</summary>
    private sealed class ThrowingStream : Stream
    {
        private readonly Exception _exception;
        private byte[]? _prefix;

        public ThrowingStream(Exception exception, byte[]? prefix = null)
        {
            _exception = exception;
            _prefix = prefix;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_prefix is not null)
            {
                var take = Math.Min(count, _prefix.Length);
                Array.Copy(_prefix, 0, buffer, offset, take);
                _prefix = null;
                return take;
            }

            throw _exception;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
