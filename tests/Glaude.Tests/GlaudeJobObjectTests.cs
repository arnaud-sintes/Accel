using Glaude.Orchestration;
using Xunit;

namespace Glaude.Tests;

/// <summary>
/// P2-T7: unit coverage for <see cref="GlaudeJobObject"/>. A full "does kill-on-close actually
/// kill a child" integration test is expensive/flaky for a fast xUnit suite, so this sticks to
/// what's deterministic: creation succeeds, and Dispose is safe (including being called twice).
/// The kill-on-close behaviour itself was verified empirically once via a throwaway diagnostic
/// run outside this suite (see the task report).
/// </summary>
public class GlaudeJobObjectTests
{
    [Fact]
    public void Create_Succeeds()
    {
        using var job = GlaudeJobObject.Create();

        Assert.NotNull(job);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var job = GlaudeJobObject.Create();

        var ex = Record.Exception(job.Dispose);

        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var job = GlaudeJobObject.Create();
        job.Dispose();

        var ex = Record.Exception(job.Dispose);

        Assert.Null(ex);
    }

    [Fact]
    public void AssignProcess_AfterDispose_Throws()
    {
        var job = GlaudeJobObject.Create();
        job.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            job.AssignProcess(System.Diagnostics.Process.GetCurrentProcess().Handle));
    }

}
