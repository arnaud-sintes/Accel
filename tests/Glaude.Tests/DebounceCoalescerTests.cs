using Glaude.Cli;
using Xunit;

namespace Glaude.Tests;

/// <summary>
/// Unit tests for the pure debounce/coalescing helper backing <c>MonitorForm</c>'s event-driven
/// refresh - see <see cref="DebounceCoalescer"/>'s doc comment for why this is a debounce device
/// and not a polling loop. The actual timer mechanism is faked here via simple counters, since
/// the class takes it as injected delegates specifically so it never needs a real WinForms Timer
/// (or any wall-clock wait) to be tested.
/// </summary>
public class DebounceCoalescerTests
{
    [Fact]
    public void Signal_RestartsTheTimer()
    {
        int restarts = 0;
        int stops = 0;
        var coalescer = new DebounceCoalescer(() => restarts++, () => stops++);

        coalescer.Signal();

        Assert.Equal(1, restarts);
        Assert.Equal(0, stops);
    }

    [Fact]
    public void Signal_MultipleTimesBeforeElapsed_OnlyCollapsesToOnePendingRebuild()
    {
        int restarts = 0;
        var coalescer = new DebounceCoalescer(() => restarts++, () => { });

        coalescer.Signal();
        coalescer.Signal();
        coalescer.Signal();

        Assert.Equal(3, restarts); // each signal still restarts the window...
        Assert.True(coalescer.Elapsed()); // ...but only a single rebuild is owed once it fires.
    }

    [Fact]
    public void Elapsed_WithNoPriorSignal_ReturnsFalseAndStopsTimer()
    {
        int stops = 0;
        var coalescer = new DebounceCoalescer(() => { }, () => stops++);

        bool shouldRebuild = coalescer.Elapsed();

        Assert.False(shouldRebuild);
        Assert.Equal(1, stops);
    }

    [Fact]
    public void Elapsed_AfterASignal_ReturnsTrueAndClearsPending()
    {
        var coalescer = new DebounceCoalescer(() => { }, () => { });

        coalescer.Signal();
        bool first = coalescer.Elapsed();
        bool second = coalescer.Elapsed(); // no new signal since the first Elapsed()

        Assert.True(first);
        Assert.False(second);
    }

    [Fact]
    public void HasPendingSignal_ReflectsWhetherASignalIsOwed()
    {
        var coalescer = new DebounceCoalescer(() => { }, () => { });

        Assert.False(coalescer.HasPendingSignal);

        coalescer.Signal();
        Assert.True(coalescer.HasPendingSignal);

        coalescer.Elapsed();
        Assert.False(coalescer.HasPendingSignal);
    }

    [Fact]
    public void SignalAfterElapsed_StartsANewPendingCycle()
    {
        var coalescer = new DebounceCoalescer(() => { }, () => { });

        coalescer.Signal();
        Assert.True(coalescer.Elapsed());

        // A brand new signal after the previous window elapsed must be treated as fresh - not
        // as if it were part of the already-consumed cycle.
        coalescer.Signal();
        Assert.True(coalescer.Elapsed());
    }
}
