namespace Accel.Tests;

using System;
using System.Linq;
using Accel.App.Services;
using Accel.App.ViewModels;
using Accel.Metrics;
using Xunit;

/// <summary>
/// Unit tests for panel A's <see cref="McpSkillsPanelViewModel"/> - driven exactly like
/// <see cref="GitPanelViewModelTests"/>/<see cref="AgentGraphViewModelTests"/>
/// (<see cref="FakeTelemetryFeed"/> + <see cref="RecordingUiThreadDispatcher"/> + a real
/// <see cref="SessionSelectionService"/>). Unlike the git panel, nothing here touches disk: the
/// hit counts ride on the pushed <see cref="SessionTreeDto"/> itself.
/// </summary>
public sealed class McpSkillsPanelViewModelTests
{
    private static (McpSkillsPanelViewModel Vm, FakeTelemetryFeed Feed, SessionSelectionService Selection, ISessionSelectionWriter Writer) Build()
    {
        var feed = new FakeTelemetryFeed();
        var dispatcher = new RecordingUiThreadDispatcher();
        var selection = new SessionSelectionService();
        var writer = selection.AcquireWriter();
        return (new McpSkillsPanelViewModel(feed, dispatcher, selection), feed, selection, writer);
    }

    private static SessionTreeDto SessionWithUsage(
        string sessionId,
        ToolHitCountDto[]? mcp = null,
        ToolHitCountDto[]? skills = null) =>
        TelemetryFixtures.Session(sessionId, isLive: true) with { McpUsage = mcp, SkillUsage = skills };

    [Fact]
    public void NothingFocused_BothCollectionsAreEmpty()
    {
        var (vm, feed, _, _) = Build();

        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(
                @"C:\projects",
                SessionWithUsage("session-1", mcp: new[] { new ToolHitCountDto("serena__find_symbol", 3) })),
        }));

        Assert.Empty(vm.McpUsage);
        Assert.Empty(vm.SkillUsage);
        Assert.Equal("No session focused.", vm.StatusText);
    }

    [Fact]
    public void FocusedSessionWithUsage_PopulatesBothCollectionsSortedByCountThenName()
    {
        var (vm, feed, _, writer) = Build();
        writer.SetFocused("session-1");

        var session = SessionWithUsage(
            "session-1",
            mcp: new[]
            {
                new ToolHitCountDto("jira__jira_search", 1),
                new ToolHitCountDto("serena__find_symbol", 7),
                new ToolHitCountDto("figma__get_metadata", 7),
            },
            skills: new[]
            {
                new ToolHitCountDto("code-review", 2),
                new ToolHitCountDto("dataviz", 5),
            });

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(@"C:\projects", session) }));

        // Count descending, then name ascending for the tie at 7.
        Assert.Equal(
            new[] { "figma__get_metadata", "serena__find_symbol", "jira__jira_search" },
            vm.McpUsage.Select(r => r.Name).ToArray());
        Assert.Equal(new[] { 7, 7, 1 }, vm.McpUsage.Select(r => r.HitCount).ToArray());

        Assert.Equal(new[] { "dataviz", "code-review" }, vm.SkillUsage.Select(r => r.Name).ToArray());
        Assert.Equal(new[] { 5, 2 }, vm.SkillUsage.Select(r => r.HitCount).ToArray());
        Assert.Equal(string.Empty, vm.StatusText);
    }

    [Fact]
    public void FocusChange_RebuildsAgainstTheNewlyFocusedSession()
    {
        var (vm, feed, _, writer) = Build();
        writer.SetFocused("session-1");

        var first = SessionWithUsage("session-1", mcp: new[] { new ToolHitCountDto("serena__find_symbol", 3) });
        var second = SessionWithUsage("session-2", skills: new[] { new ToolHitCountDto("code-review", 9) });

        feed.Publish(TelemetryFixtures.Tree(new[] { TelemetryFixtures.Root(@"C:\projects", first, second) }));

        Assert.Equal("serena__find_symbol", Assert.Single(vm.McpUsage).Name);
        Assert.Empty(vm.SkillUsage);

        // A focus change with no new telemetry must still re-target both lists.
        writer.SetFocused("session-2");

        Assert.Empty(vm.McpUsage);
        var skill = Assert.Single(vm.SkillUsage);
        Assert.Equal("code-review", skill.Name);
        Assert.Equal(9, skill.HitCount);
    }

    [Fact]
    public void FocusedSessionWithNullUsageArrays_YieldsEmptyCollections()
    {
        var (vm, feed, _, writer) = Build();
        writer.SetFocused("session-1");

        // TelemetryFixtures.Session leaves both usage arrays at their null default - what a
        // historical (transcript-only) session and any pre-existing snapshot look like.
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(@"C:\projects", TelemetryFixtures.Session("session-1", isLive: false)),
        }));

        Assert.Empty(vm.McpUsage);
        Assert.Empty(vm.SkillUsage);
        Assert.Equal(string.Empty, vm.StatusText);
    }

    [Fact]
    public void UnattributedSession_IsFoundToo()
    {
        var (vm, feed, _, writer) = Build();
        writer.SetFocused("session-9");

        feed.Publish(TelemetryFixtures.Tree(
            unattributedSessions: new[]
            {
                SessionWithUsage("session-9", mcp: new[] { new ToolHitCountDto("serena__find_symbol", 4) }),
            }));

        Assert.Equal(4, Assert.Single(vm.McpUsage).HitCount);
    }

    [Fact]
    public void FocusedSessionMissingFromTheSnapshot_LeavesBothListsEmpty()
    {
        var (vm, feed, _, writer) = Build();
        writer.SetFocused("session-unknown");

        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(@"C:\projects", SessionWithUsage("session-1")),
        }));

        Assert.Empty(vm.McpUsage);
        Assert.Empty(vm.SkillUsage);
        Assert.Equal("Waiting for session…", vm.StatusText);
    }

    [Fact]
    public void Dispose_UnsubscribesFromTheFeedAndSelection()
    {
        var feed = new FakeTelemetryFeed();
        var dispatcher = new RecordingUiThreadDispatcher();
        var selection = new SessionSelectionService();
        var writer = selection.AcquireWriter();
        var vm = new McpSkillsPanelViewModel(feed, dispatcher, selection);

        vm.Dispose();

        Assert.False(feed.HasSnapshotSubscribers);

        // A post-dispose focus change plus snapshot must not repopulate anything.
        writer.SetFocused("session-1");
        feed.Publish(TelemetryFixtures.Tree(new[]
        {
            TelemetryFixtures.Root(@"C:\projects", SessionWithUsage("session-1", mcp: new[] { new ToolHitCountDto("x", 1) })),
        }));

        Assert.Empty(vm.McpUsage);
    }
}
