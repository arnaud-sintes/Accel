namespace Glaude.Tests;

using System;
using System.Text.Json;
using Glaude.App.Controls;
using Xunit;

/// <summary>
/// P2-T5b: <see cref="TerminalView.BuildAttachScript"/> is the one piece of the terminal-wiring task
/// that is actually unit-testable outside a real WebView2 - everything else (the WebSocket
/// attach/onData/resize glue) lives in <c>terminal.js</c>, which this stack cannot unit test (see the
/// task's own note). This class pins the generated script's shape and, more importantly, that a
/// hostile <c>tabId</c> cannot break out of the generated JS text.
/// </summary>
public class TerminalViewTests
{
    [Fact]
    public void BuildAttachScript_ProducesTheExpectedGlaudeAttachPtyCall()
    {
        string script = TerminalView.BuildAttachScript("abc123", 40010);

        Assert.Equal("window.glaudeAttachPty(\"abc123\", 40010);", script);
    }

    [Theory]
    [InlineData("\"; alert(1); //")]
    [InlineData("tab\"id")]
    [InlineData("tab\\id")]
    [InlineData("line1\nline2")]
    [InlineData("</script><script>alert(1)</script>")]
    public void BuildAttachScript_JsonEncodesTabId_SoHostileContentCannotBreakOutOfTheScript(string hostileTabId)
    {
        string script = TerminalView.BuildAttachScript(hostileTabId, 12345);

        // The tabId must appear as exactly one JSON string literal argument - round-tripping the
        // generated script's argument list back through JSON must reproduce the original value
        // unchanged, proving nothing inside it was interpreted as script syntax.
        string prefix = "window.glaudeAttachPty(";
        string suffix = ", 12345);";
        Assert.StartsWith(prefix, script, StringComparison.Ordinal);
        Assert.EndsWith(suffix, script, StringComparison.Ordinal);

        string jsonArgument = script[prefix.Length..^suffix.Length];
        string? decoded = JsonSerializer.Deserialize<string>(jsonArgument);
        Assert.Equal(hostileTabId, decoded);
    }

    [Fact]
    public void BuildAttachScript_RejectsNullOrEmptyTabId()
    {
        Assert.Throws<ArgumentException>(() => TerminalView.BuildAttachScript(string.Empty, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void BuildAttachScript_RejectsOutOfRangePorts(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TerminalView.BuildAttachScript("abc", port));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(40010)]
    [InlineData(65535)]
    public void BuildAttachScript_AcceptsTheFullValidPortRange(int port)
    {
        string script = TerminalView.BuildAttachScript("abc", port);

        Assert.Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture), script, StringComparison.Ordinal);
    }
}
