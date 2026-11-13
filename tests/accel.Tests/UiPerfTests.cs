namespace Accel.Tests;

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Accel.App.Services;
using Xunit;

/// <summary>The slow-event log: it must name what was slow, ignore what was not, and be inert until installed.</summary>
public class UiPerfTests
{
    private sealed class Probe
    {
        public Action Queued() => () => Thread.Sleep(1);

        public Action QueuedWithClosure(int captured) => () => Thread.Sleep(captured);
    }

    /// <summary>Runs <paramref name="body"/> with the log enabled into a private sink, then restores the global
    /// state (other test classes run in parallel in this process, so lines are filtered by the caller).</summary>
    private static string[] Capture(Action body)
    {
        var lines = new ConcurrentQueue<string>();
        UiPerf.Sink = lines.Enqueue;
        UiPerf.EnableForTests(true);
        try
        {
            body();
        }
        finally
        {
            UiPerf.EnableForTests(false);
            UiPerf.Sink = null;
        }

        return lines.ToArray();
    }

    [Fact]
    public void ASlowScope_IsLogged_WithItsNameAndDuration()
    {
        var lines = Capture(() =>
        {
            using (UiPerf.Measure("uiperf-test slow scope", thresholdMs: 10))
            {
                Thread.Sleep(40);
            }
        });

        var line = Assert.Single(lines, l => l.Contains("uiperf-test slow scope"));
        Assert.Matches(@"\d+ ms  uiperf-test slow scope", line);
    }

    [Fact]
    public void AFastScope_IsNotLogged()
    {
        var lines = Capture(() =>
        {
            using (UiPerf.Measure("uiperf-test fast scope", thresholdMs: 5_000))
            {
                Thread.Sleep(1);
            }
        });

        Assert.DoesNotContain(lines, l => l.Contains("uiperf-test fast scope"));
    }

    [Fact]
    public void WhileDisabled_NothingIsMeasuredOrLogged()
    {
        var lines = new ConcurrentQueue<string>();
        UiPerf.Sink = lines.Enqueue;
        try
        {
            using (UiPerf.Measure("uiperf-test disabled", thresholdMs: 0))
            {
                Thread.Sleep(5);
            }

            UiPerf.Log("uiperf-test disabled direct");
        }
        finally
        {
            UiPerf.Sink = null;
        }

        Assert.DoesNotContain(lines, l => l.Contains("uiperf-test disabled"));
    }

    [Fact]
    public void ASlowQueuedDelegate_IsLabelledByTheMethodThatQueuedIt()
    {
        var lines = Capture(() =>
        {
            var action = new Probe().QueuedWithClosure(40);
            using (UiPerf.Measure(action, thresholdMs: 10))
            {
                action();
            }
        });

        // "Probe.QueuedWithClosure": the type and method the lambda was written in, not "<>c__DisplayClass0_0.<..>b__0".
        Assert.Contains(lines, l => l.Contains("Probe.QueuedWithClosure"));
        Assert.DoesNotContain(lines, l => l.Contains("DisplayClass") && l.Contains("Probe"));
    }

    [Fact]
    public void LabelOf_HandlesALambdaWithoutCaptures_AndNull()
    {
        Assert.Equal("Probe.Queued", UiPerf.LabelOf(new Probe().Queued()));
        Assert.Equal("?", UiPerf.LabelOf(null));
    }
}
