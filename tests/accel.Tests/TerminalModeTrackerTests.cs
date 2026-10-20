namespace Accel.Tests;

using Accel.Orchestration;
using Xunit;

public class TerminalModeTrackerTests
{
    private const string Esc = "\u001b";
    private const string Ris = "\u001bc";

    private static TerminalModeTracker Fed(params string[] chunks)
    {
        var tracker = new TerminalModeTracker();
        foreach (string chunk in chunks)
        {
            tracker.Observe(chunk);
        }

        return tracker;
    }

    [Fact]
    public void FreshTracker_ReplaysOnlyAReset()
    {
        Assert.Equal(Ris, new TerminalModeTracker().BuildReplay());
    }

    [Fact]
    public void AlternateScreenAndMouse_AreReplayedEachInItsOwnSequence_MouseLast()
    {
        var tracker = Fed($"{Esc}[?1049;1002;1006h");

        Assert.Equal($"{Ris}{Esc}[?1049h{Esc}[?1006h{Esc}[?1002h", tracker.BuildReplay());
        Assert.True(tracker.InAlternateScreen);
        Assert.True(tracker.MouseTrackingEnabled);
    }

    [Fact]
    public void ResettingModes_RemovesThemFromTheReplay()
    {
        var tracker = Fed($"{Esc}[?1049;1000;1006h", $"{Esc}[?1000;1006l", $"{Esc}[?1049l");

        Assert.Equal(Ris, tracker.BuildReplay());
        Assert.False(tracker.InAlternateScreen);
        Assert.False(tracker.MouseTrackingEnabled);
    }

    [Theory]
    [InlineData(47)]
    [InlineData(1047)]
    [InlineData(1049)]
    public void EveryAlternateScreenVariant_IsReplayedAsItself(int mode)
    {
        Assert.Equal($"{Ris}{Esc}[?{mode}h", Fed($"{Esc}[?{mode}h").BuildReplay());
    }

    [Fact]
    public void MouseTracking_LastSetWins_AndAnyResetTurnsItOff()
    {
        Assert.Equal($"{Ris}{Esc}[?1003h", Fed($"{Esc}[?1000h", $"{Esc}[?1003h").BuildReplay());
        Assert.Equal(Ris, Fed($"{Esc}[?1000h", $"{Esc}[?1002l").BuildReplay());
    }

    [Fact]
    public void OtherPrivateModes_AreTracked_AndCursorVisibilityIsReplayedOnlyWhenHidden()
    {
        var tracker = Fed($"{Esc}[?1h{Esc}[?2004h{Esc}[?1004h{Esc}[?25l");

        Assert.Equal($"{Ris}{Esc}[?1h{Esc}[?25l{Esc}[?2004h{Esc}[?1004h", tracker.BuildReplay());

        tracker.Observe($"{Esc}[?25h");
        Assert.DoesNotContain("[?25", tracker.BuildReplay());
    }

    [Fact]
    public void SynchronizedOutput2026_IsNeverReplayed()
    {
        Assert.Equal(Ris, Fed($"{Esc}[?2026h").BuildReplay());
    }

    [Fact]
    public void SequenceSplitAtEveryOffset_GivesTheSameState()
    {
        const string seq = "\u001b[?1049;1002;1006h";
        string expected = Fed(seq).BuildReplay();

        for (int split = 1; split < seq.Length; split++)
        {
            Assert.Equal(expected, Fed(seq[..split], seq[split..]).BuildReplay());
        }
    }

    [Fact]
    public void ASnapshotTakenMidSequence_EndsWithThePendingBytes()
    {
        var tracker = Fed("hello", $"{Esc}[?10");

        Assert.Equal($"{Ris}{Esc}[?10", tracker.BuildReplay());

        // Replayed to a fresh terminal and followed by the rest, the sequence still completes.
        tracker.Observe("49h");
        Assert.Equal($"{Ris}{Esc}[?1049h", tracker.BuildReplay());
    }

    [Theory]
    [InlineData("[1049h")]      // not private
    [InlineData("[?u")]         // kitty keyboard query
    [InlineData("[>1u")]        // other prefix
    [InlineData("[?1049$p")]    // DECRQM request, not a set
    [InlineData("[?1049;x")]
    public void NonModeSequences_AreIgnored(string body)
    {
        Assert.Equal(Ris, Fed(Esc + body).BuildReplay());
    }

    [Fact]
    public void Ris_ResetsEverything()
    {
        Assert.Equal(Ris, Fed($"{Esc}[?1049;1000;2004h", Ris).BuildReplay());
    }

    [Fact]
    public void EscapeRestartsASequence_AndCanAbortsOne()
    {
        // The second ESC begins a fresh sequence, so the first one never dispatches.
        Assert.Equal(Ris, Fed($"{Esc}[?1049{Esc}[0m").BuildReplay());
        Assert.Equal(Ris, Fed($"{Esc}[?1049\u0018h").BuildReplay());
    }

    [Fact]
    public void ControlCharacterInsideACsi_DoesNotEndIt()
    {
        Assert.Equal($"{Ris}{Esc}[?1049h", Fed($"{Esc}[?10\r\n49h").BuildReplay());
    }

    [Fact]
    public void OversizedParameters_AreDropped_WithoutThrowing()
    {
        var tracker = Fed($"{Esc}[?{new string('1', 500)}h");

        Assert.Equal(Ris, tracker.BuildReplay());

        tracker.Observe($"{Esc}[?1049h");
        Assert.True(tracker.InAlternateScreen);
    }

    [Fact]
    public void PlainText_AndOtherEscapes_DoNotDisturbState()
    {
        var tracker = Fed($"{Esc}[?1049h", $"text {Esc}[31m red {Esc}]0;title\u0007 more");

        Assert.True(tracker.InAlternateScreen);
    }
}
