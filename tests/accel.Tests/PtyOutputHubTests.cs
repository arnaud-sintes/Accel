namespace Accel.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Accel.Server;
using Xunit;

/// <summary>
/// A session in a tab nobody is looking at must keep being read. The output channel is bounded and its pump
/// blocks when it is full (real backpressure towards the child), so a hub that only read while a socket was
/// attached froze every busy unselected session within a minute or two. These tests use a tiny queue so a
/// regression shows up in milliseconds instead of minutes.
/// </summary>
public class PtyOutputHubTests
{
    private const string Esc = "\u001b";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private sealed class BoundedEndpoint : IPtyEndpoint
    {
        private readonly Channel<string> _output =
            Channel.CreateBounded<string>(new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

        public ChannelReader<string> Output => _output.Reader;

        public void Write(ReadOnlySpan<byte> bytes)
        {
        }

        public void Resize(int columns, int rows)
        {
        }

        /// <summary>Publishes one chunk; the task only completes once the (tiny) queue has room.</summary>
        public ValueTask Publish(string chunk) => _output.Writer.WriteAsync(chunk);

        public void Complete() => _output.Writer.TryComplete();
    }

    private static async Task PublishAll(BoundedEndpoint endpoint, IEnumerable<string> chunks)
    {
        using var cts = new CancellationTokenSource(Timeout);
        foreach (var chunk in chunks)
        {
            await endpoint.Publish(chunk).AsTask().WaitAsync(cts.Token);
        }
    }

    private sealed class Frames
    {
        private readonly List<string> _frames = new();

        public Task Pump(PtyOutputHub.PtyAttachment attachment) => attachment.PumpAsync((chunk, _) =>
        {
            lock (_frames)
            {
                _frames.Add(chunk);
            }

            return Task.CompletedTask;
        });

        public async Task<IReadOnlyList<string>> WaitFor(int count)
        {
            using var cts = new CancellationTokenSource(Timeout);
            while (true)
            {
                lock (_frames)
                {
                    if (_frames.Count >= count)
                    {
                        return _frames.ToList();
                    }
                }

                await Task.Delay(10, cts.Token);
            }
        }
    }

    [Fact]
    public async Task WithNoTabAttached_EverythingTheSessionWritesIsStillRead()
    {
        var endpoint = new BoundedEndpoint();
        using var hub = new PtyOutputHub(endpoint);

        // 50 writes into a 2-slot queue with nobody attached: each completes only if something drains it.
        await PublishAll(endpoint, Enumerable.Range(0, 50).Select(i => $"line {i}\r\n"));
    }

    [Fact]
    public async Task InTheAlternateScreen_UnattachedOutputIsDiscarded_ButTheModesAreTracked()
    {
        var endpoint = new BoundedEndpoint();
        using var hub = new PtyOutputHub(endpoint);

        await endpoint.Publish($"{Esc}[?1049;1002;1006h").AsTask();
        await PublishAll(endpoint, Enumerable.Range(0, 20).Select(i => $"frame {i}"));

        using var attachment = hub.Attach(CancellationToken.None);
        var frames = new Frames();
        _ = frames.Pump(attachment);

        var received = await frames.WaitFor(1);
        Assert.Equal($"{Esc}c{Esc}[?1049h{Esc}[?1006h{Esc}[?1002h", received[0]);

        // The discarded delta frames are not replayed; live output after the attach is.
        await endpoint.Publish("live").AsTask();
        received = await frames.WaitFor(2);
        Assert.Equal("live", received[1]);
    }

    [Fact]
    public async Task OnTheNormalScreen_UnattachedOutputIsKeptAndReplayedAfterTheReset()
    {
        var endpoint = new BoundedEndpoint();
        using var hub = new PtyOutputHub(endpoint);

        await PublishAll(endpoint, new[] { "build started\r\n", "build finished\r\n" });
        // Let the reader catch up with the last chunk.
        await Task.Delay(150);

        using var attachment = hub.Attach(CancellationToken.None);
        var frames = new Frames();
        _ = frames.Pump(attachment);

        var received = await frames.WaitFor(2);
        Assert.Equal($"{Esc}c", received[0]);
        Assert.Equal("build started\r\nbuild finished\r\n", received[1]);
    }

    [Fact]
    public async Task ANewAttach_SupersedesTheOldOne_AndOutputGoesToTheNewOneOnly()
    {
        var endpoint = new BoundedEndpoint();
        using var hub = new PtyOutputHub(endpoint);

        using var first = hub.Attach(CancellationToken.None);
        var firstFrames = new Frames();
        var firstPump = firstFrames.Pump(first);
        await firstFrames.WaitFor(1);

        using var second = hub.Attach(CancellationToken.None);
        var secondFrames = new Frames();
        _ = secondFrames.Pump(second);
        await secondFrames.WaitFor(1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstPump.WaitAsync(Timeout));

        await endpoint.Publish("after the switch").AsTask();
        var received = await secondFrames.WaitFor(2);
        Assert.Equal("after the switch", received[1]);
        Assert.Single(await firstFrames.WaitFor(1));
    }

    [Fact]
    public async Task AWedgedSocket_CannotBlockTheReader_OnceANewAttachSupersedesIt()
    {
        var endpoint = new BoundedEndpoint();
        using var hub = new PtyOutputHub(endpoint);

        // A send that never completes and ignores cancellation, standing in for a dead socket.
        using var wedged = hub.Attach(CancellationToken.None);
        var never = new TaskCompletionSource();
        _ = wedged.PumpAsync((_, _) => never.Task);

        // Enough output to fill the wedged attachment's queue, so the reader ends up waiting on it.
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 400; i++)
            {
                await endpoint.Publish($"chunk {i}").AsTask();
            }
        });
        await Task.Delay(300);

        using var healthy = hub.Attach(CancellationToken.None);
        var frames = new Frames();
        _ = frames.Pump(healthy);

        // The reader moves on to the new attachment: it receives its prefix and then live output.
        var received = await frames.WaitFor(2);
        Assert.StartsWith($"{Esc}c", received[0]);
        Assert.StartsWith("chunk ", received[1]);
    }

    [Fact]
    public async Task WhenTheSessionEnds_TheAttachmentCompletes()
    {
        var endpoint = new BoundedEndpoint();
        using var hub = new PtyOutputHub(endpoint);

        using var attachment = hub.Attach(CancellationToken.None);
        var frames = new Frames();
        var pump = frames.Pump(attachment);
        await frames.WaitFor(1);

        endpoint.Complete();

        await pump.WaitAsync(Timeout); // returns normally: the stream ended
    }

    [Fact]
    public async Task AttachingAfterTheSessionEnded_CompletesAfterTheReplay()
    {
        var endpoint = new BoundedEndpoint();
        using var hub = new PtyOutputHub(endpoint);
        endpoint.Complete();
        await Task.Delay(150);

        using var attachment = hub.Attach(CancellationToken.None);
        var frames = new Frames();

        await frames.Pump(attachment).WaitAsync(Timeout);

        Assert.Single(await frames.WaitFor(1));
    }

    [Fact]
    public void Resize_IsSerialised_AndForwardedAsIs()
    {
        var sizes = new List<(int, int)>();
        var endpoint = new RecordingResizeEndpoint(sizes);
        using var hub = new PtyOutputHub(endpoint);

        hub.Resize(100, 30);
        hub.Resize(100, 30);

        Assert.Equal(new[] { (100, 30), (100, 30) }, sizes);
    }

    private sealed class RecordingResizeEndpoint : IPtyEndpoint
    {
        private readonly List<(int, int)> _sizes;

        public RecordingResizeEndpoint(List<(int, int)> sizes) => _sizes = sizes;

        public ChannelReader<string> Output { get; } = Channel.CreateUnbounded<string>().Reader;

        public void Write(ReadOnlySpan<byte> bytes)
        {
        }

        public void Resize(int columns, int rows) => _sizes.Add((columns, rows));
    }

    // --- the ring ----------------------------------------------------------------------------------------

    [Fact]
    public void TheRing_DropsTheOldestChunks_AndTrimsTheResultToALineStart()
    {
        var ring = new PtyOutputHub.OutputRing(capacity: 20);
        ring.Append("aaaaaaaaaa\r\n"); // 12
        ring.Append("bbbbbbbbbb\r\n"); // 24 > 20: the first chunk is dropped
        ring.Append("cc");

        var text = ring.Take();

        Assert.Equal("cc", text);
        Assert.Null(ring.Take());
    }

    [Fact]
    public void TheRing_ReturnsEverythingWhenNothingWasDropped_AndThenEmpties()
    {
        var ring = new PtyOutputHub.OutputRing(capacity: 100);
        ring.Append("one\r\n");
        ring.Append("two\r\n");

        Assert.Equal("one\r\ntwo\r\n", ring.Take());
        Assert.Null(ring.Take());
    }
}
