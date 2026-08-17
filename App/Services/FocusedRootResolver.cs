namespace Accel.App.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using Accel.App.ViewModels;
using Accel.Metrics;

/// <summary>
/// Pure resolution of "which folder is currently focused" - shared by every panel B section that
/// needs to root itself there (panel B's file tree, <see cref="FilesPanelViewModel"/>; panel B's
/// git status list, <see cref="GitPanelViewModel"/>). The rule itself is panel B's, stated once
/// here rather than duplicated per section: the focused session's cwd
/// (<see cref="ISessionSelectionService.FocusedSessionId"/>, resolved against the given
/// <see cref="RootsTreeDto"/> snapshot) if a session is focused, else panel A's own tree selection
/// (<see cref="RootsPanelViewModel.SelectedRootPath"/>) if a root/session/agent row is selected
/// there instead.
/// </summary>
public static class FocusedRootResolver
{
    public static string? Resolve(
        RootsTreeDto? snapshot,
        ISessionSelectionService? selection,
        RootsPanelViewModel? rootsPanel)
    {
        string? focusedId = selection?.FocusedSessionId;
        if (!string.IsNullOrEmpty(focusedId) && snapshot is not null)
        {
            var session = EnumerateSessions(snapshot).FirstOrDefault(
                s => string.Equals(s.SessionId, focusedId, StringComparison.OrdinalIgnoreCase));

            if (session is not null && !string.IsNullOrEmpty(session.Cwd))
            {
                return session.Cwd;
            }
        }

        return rootsPanel?.SelectedRootPath;
    }

    private static IEnumerable<SessionTreeDto> EnumerateSessions(RootsTreeDto snapshot) =>
        snapshot.Roots.SelectMany(r => r.Sessions ?? Array.Empty<SessionTreeDto>())
            .Concat(snapshot.UnattributedSessions ?? Array.Empty<SessionTreeDto>());
}
