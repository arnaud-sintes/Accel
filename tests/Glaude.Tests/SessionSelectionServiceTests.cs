namespace Glaude.Tests;

using System;
using System.Linq;
using System.Reflection;
using Glaude.App.Services;
using Glaude.App.ViewModels;
using Xunit;

/// <summary>
/// P3-T1 / locked-in decision 8: <see cref="ISessionSelectionService"/> is the single hub for the focused
/// session id, and <c>TabsViewModel</c> (panel C) is its ONLY writer. These tests pin the single-writer
/// property down <b>structurally</b>, not by documentation: the read interface has no mutator at all, the
/// concrete service has no public mutator either, and the one write capability can only be acquired once.
/// </summary>
public class SessionSelectionServiceTests
{
    [Fact]
    public void ReadInterface_ExposesNoWayToChangeTheFocusedSession()
    {
        var mutators = typeof(ISessionSelectionService)
            .GetMembers()
            .Where(IsMutator)
            .Select(m => m.Name)
            .ToArray();

        // FocusedSessionId is get-only, and nothing else on the interface can set it. A reader panel
        // (A/B/E) therefore cannot write selection even in principle.
        Assert.Empty(mutators);
        Assert.Null(typeof(ISessionSelectionService).GetProperty(nameof(ISessionSelectionService.FocusedSessionId))!.SetMethod);
    }

    [Fact]
    public void ConcreteService_HasNoPublicMutator_SoDownCastingBuysNothing()
    {
        var mutators = typeof(SessionSelectionService)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(IsMutator)
            .Select(m => m.Name)
            .ToArray();

        Assert.Empty(mutators);
    }

    [Fact]
    public void AcquireWriter_YieldsExactlyOneWriter_ForTheLifeOfTheService()
    {
        var service = new SessionSelectionService();
        Assert.False(service.HasWriter);

        var writer = service.AcquireWriter();
        Assert.True(service.HasWriter);

        // The second attempt is refused - once TabsViewModel holds the writer, nothing else can obtain
        // one, which is the structural half of "panel C is the only writer".
        Assert.Throws<InvalidOperationException>(() => service.AcquireWriter());
        Assert.NotNull(writer);
    }

    [Fact]
    public void OnlyTheWriter_CanChangeFocusedSessionId()
    {
        var service = new SessionSelectionService();
        var writer = service.AcquireWriter();

        writer.SetFocused("session-a");

        Assert.Equal("session-a", service.FocusedSessionId);
        Assert.Equal("session-a", writer.FocusedSessionId);
        Assert.True(service.IsFocused("session-a"));
        Assert.False(service.IsFocused("session-b"));
    }

    [Fact]
    public void TabsViewModel_IsTheWritePathUsedInProduction()
    {
        // The composition-root shape: the writer goes to TabsViewModel, the read interface to everyone
        // else. Proven here by having the ViewModel do the write via ordinary selection.
        var service = new SessionSelectionService();
        var host = new FakePtySessionHost("tab-1");
        using var tabs = new TabsViewModel(host, service.AcquireWriter(), new RecordingUiThreadDispatcher());

        tabs.SelectTab("tab-1");

        Assert.Equal("tab-1", service.FocusedSessionId);
        Assert.Throws<InvalidOperationException>(() => service.AcquireWriter());
    }

    [Fact]
    public void IsFocused_IgnoresHexCasing_AndNeverMatchesNullOrEmpty()
    {
        var service = new SessionSelectionService();
        var writer = service.AcquireWriter();
        var id = Guid.NewGuid().ToString();

        writer.SetFocused(id.ToUpperInvariant());

        Assert.True(service.IsFocused(id.ToLowerInvariant()));
        Assert.False(service.IsFocused(null));
        Assert.False(service.IsFocused(string.Empty));
    }

    [Fact]
    public void Subscribers_AreNotifiedOfChanges_ButNotOfRedundantSets()
    {
        var service = new SessionSelectionService();
        var writer = service.AcquireWriter();
        var recipient = new object();
        var messages = new System.Collections.Generic.List<FocusedSessionChangedMessage>();
        service.Subscribe(recipient, messages.Add);

        writer.SetFocused("a");
        writer.SetFocused("a"); // idempotent - no second message
        writer.SetFocused("b");
        writer.SetFocused(null);

        Assert.Equal(3, messages.Count);
        Assert.Equal((null, "a"), (messages[0].PreviousSessionId, messages[0].FocusedSessionId));
        Assert.Equal(("a", "b"), (messages[1].PreviousSessionId, messages[1].FocusedSessionId));
        Assert.Equal(("b", null), (messages[2].PreviousSessionId, messages[2].FocusedSessionId));

        service.Unsubscribe(recipient);
        writer.SetFocused("c");
        Assert.Equal(3, messages.Count);
    }

    [Fact]
    public void EmptyStringFocus_IsNormalizedToNull()
    {
        var service = new SessionSelectionService();
        var writer = service.AcquireWriter();

        writer.SetFocused(string.Empty);

        Assert.Null(service.FocusedSessionId);
    }

    /// <summary>A member that could change state: any settable property, or any void/Task method that is
    /// not one of the two subscription helpers (which mutate the messenger's registration list, not the
    /// selection).</summary>
    private static bool IsMutator(MemberInfo member) => member switch
    {
        PropertyInfo property => property.SetMethod is not null,
        MethodInfo method when method.IsSpecialName => false,
        MethodInfo method =>
            method.Name is not (nameof(ISessionSelectionService.Subscribe) or nameof(ISessionSelectionService.Unsubscribe)
                or nameof(ISessionSelectionService.IsFocused) or nameof(SessionSelectionService.AcquireWriter)
                or nameof(object.ToString) or nameof(object.Equals) or nameof(object.GetHashCode)
                or nameof(object.GetType))
            && method.ReturnType == typeof(void),
        _ => false,
    };
}
