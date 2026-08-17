using Accel.Orchestration;
using Xunit;

namespace Accel.Tests;

/// <summary>
/// P2-T7: unit coverage for <see cref="AccelJobObject"/>. A full "does kill-on-close actually
/// kill a child" integration test is expensive/flaky for a fast xUnit suite, so this sticks to
/// what's deterministic: creation succeeds, and Dispose is safe (including being called twice).
/// The kill-on-close behaviour itself was verified empirically once via a throwaway diagnostic
/// run outside this suite (see the task report).
/// </summary>
public class AccelJobObjectTests
{
    [Fact]
    public void Create_Succeeds()
    {
        using var job = AccelJobObject.Create();

        Assert.NotNull(job);
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var job = AccelJobObject.Create();

        var ex = Record.Exception(job.Dispose);

        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var job = AccelJobObject.Create();
        job.Dispose();

        var ex = Record.Exception(job.Dispose);

        Assert.Null(ex);
    }

    [Fact]
    public void AssignProcess_AfterDispose_Throws()
    {
        var job = AccelJobObject.Create();
        job.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            job.AssignProcess(System.Diagnostics.Process.GetCurrentProcess().Handle));
    }

    // -----------------------------------------------------------------------------------------
    // P2-T3 additions: the SafeProcessHandle overload (P2-T2b finding 2) and the rooted singleton
    // (P2-T2b finding 1). The live behaviour - assign a real suspended child, confirm
    // IsProcessInJob, then confirm closing the job kills it - is proven by the
    // `pty-session-smoke-test` verb (PtySessionSmokeTest check 3/7), which is where a real process
    // belongs; these cover the argument/lifetime contract deterministically.
    //
    // No test here ever assigns the CURRENT process to a job: the job carries
    // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, so that would make the test runner itself die when the
    // job handle closed.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void AssignProcess_SafeProcessHandleOverload_RejectsNull()
    {
        using var job = AccelJobObject.Create();

        Assert.Throws<ArgumentNullException>(() =>
            job.AssignProcess((Microsoft.Win32.SafeHandles.SafeProcessHandle)null!));
    }

    [Fact]
    public void AssignProcess_SafeProcessHandleOverload_RejectsAnInvalidHandle()
    {
        using var job = AccelJobObject.Create();
        using var invalid = new Microsoft.Win32.SafeHandles.SafeProcessHandle(IntPtr.Zero, ownsHandle: false);

        // Must be caught as a managed argument error rather than reaching the P/Invoke with a zero
        // handle - a DangerousAddRef on a closed/invalid handle is the exact footgun this overload
        // exists to prevent.
        Assert.Throws<ArgumentException>(() => job.AssignProcess(invalid));
    }

    [Fact]
    public void AssignProcess_SafeProcessHandleOverload_AfterDispose_Throws()
    {
        var job = AccelJobObject.Create();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var handle = self.SafeHandle;
        job.Dispose();

        Assert.Throws<ObjectDisposedException>(() => job.AssignProcess(handle));
    }

    [Fact]
    public void ContainsProcess_RejectsNullAndThrowsAfterDispose()
    {
        var job = AccelJobObject.Create();
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        Assert.Throws<ArgumentNullException>(() => job.ContainsProcess(null!));

        // A process that was never assigned to this job is simply not in it.
        Assert.False(job.ContainsProcess(self.SafeHandle));

        var handle = self.SafeHandle;
        job.Dispose();
        Assert.Throws<ObjectDisposedException>(() => job.ContainsProcess(handle));
    }

    /// <summary>
    /// The rooting guarantee from the ConPty review: the app-wide job must be a single instance held by a
    /// static field, so it can never be collected (and therefore finalized, and therefore
    /// kill-on-closed) while sessions are live. Deliberately does not dispose it - see the property's
    /// documentation for why there is no correct moment to.
    /// </summary>
    [Fact]
    public void Shared_IsASingleInstanceHeldByAStaticField()
    {
        var first = AccelJobObject.Shared;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.NotNull(first);
        Assert.Same(first, AccelJobObject.Shared);

        // Still usable after a full GC cycle - i.e. it was not finalized out from under us.
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        Assert.False(AccelJobObject.Shared.ContainsProcess(self.SafeHandle));
    }
}
