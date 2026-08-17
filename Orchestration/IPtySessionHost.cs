namespace Accel.Orchestration;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// P3-T1: the slice of <see cref="PtyRegistry"/> that panel C's <c>TabsViewModel</c> actually needs -
/// which tabs exist, when one ends, and the one blessed way to close one.
///
/// <para><b>Why an interface at all.</b> <see cref="PtyRegistry"/> only ever holds real
/// <see cref="PtySession"/> objects, and a real session means a real ConPTY plus a real child process:
/// there is no way to fabricate one for a unit test. Projecting the registry behind this three-member
/// interface lets <c>TabsViewModel</c>'s pure logic (tab add/remove/select, self-exit handling, close
/// routing) be tested against a trivial double, while the real-process behaviour keeps being proven where
/// it belongs - <c>pty-registry-stress-test</c> and the <c>tabs-e2e-smoke-test</c> verb.</para>
///
/// <para><b>What it deliberately omits: <see cref="PtySession"/> itself.</b> Note that neither
/// <see cref="TabIds"/> nor anything else here hands out a session. A tab ViewModel has no business
/// holding one (input/output/resize all flow through the <c>/pty/{tabId}</c> route, not through the
/// ViewModel), and not exposing one is the cheapest possible enforcement of
/// <see cref="PtyRegistry"/>'s ownership rule: no session reference in the ViewModel layer means no
/// <see cref="PtySession.Dispose"/> call is even expressible there. Closing goes through
/// <see cref="CloseAsync"/>, i.e. <see cref="PtyRegistry.CloseAsync"/>, always.</para>
/// </summary>
public interface IPtySessionHost
{
    /// <summary>See <see cref="PtyRegistry.SessionEnded"/> - raised on a thread-pool thread, so a
    /// ViewModel must marshal to the UI thread itself.</summary>
    event EventHandler<PtySessionEndedEventArgs>? SessionEnded;

    /// <summary>Every currently registered tabId, in no particular order (the tab strip keeps its own
    /// insertion order and only uses this to reconcile).</summary>
    IReadOnlyList<string> TabIds();

    /// <summary>See <see cref="PtyRegistry.CloseAsync"/>: removes, disposes, verifies, force-kills if
    /// needed. Never throws, and a repeat/unknown close is a no-op.</summary>
    Task<PtyCloseResult> CloseAsync(string tabId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The child process id registered under <paramref name="tabId"/>, or null for an unknown tabId.
    /// Read-only - unlike a <see cref="PtySession"/> reference, a bare pid confers no way to write to,
    /// resize, or dispose the session, so handing it out does not weaken the ownership rule above.
    ///
    /// <para>Exists so a tab ViewModel can correlate its own tabId (fixed for the pty's whole lifetime,
    /// per <c>TabsViewModel</c>'s remarks) against Claude Code's own per-pid status file
    /// (<see cref="ClaudeSessionStatusFile"/>), whose <c>sessionId</c> field can drift away from the
    /// launch-time tabId - e.g. the user typing <c>/clear</c>, which starts a new transcript under a new
    /// session id on the very same pid.</para>
    /// </summary>
    int? TryGetProcessId(string tabId);
}
