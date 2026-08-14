namespace Glaude.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using Glaude.App.ViewModels;
using Glaude.Metrics;
using Glaude.Orchestration;
using Xunit;

/// <summary>
/// P2-T6: unit tests for <see cref="CreateSessionDialogViewModel"/> - the "Create session" dialog's
/// ViewModel. Covers GUID uniqueness per confirm, argv built as a real array (never a re-split
/// string), model/effort selections mapping to <see cref="ModelBadgeTable"/>/<see cref="EffortBarLevel"/>'s
/// exact vocabularies, and the advanced-args warning text existing. The one test that actually
/// launches a process (<see cref="Confirm_ActuallyStartsAPtySession"/>) points the launch at
/// <c>cmd.exe</c> via the constructor's <c>specBuilder</c> seam - the same "controllable child,
/// never claude.exe" rule <c>PtySessionSmokeTest</c> uses - so this proves a real
/// <see cref="PtySession"/> starts successfully off this ViewModel's argv, without ever touching a
/// real `claude` process.
/// </summary>
public class CreateSessionDialogViewModelTests
{
    private static string CmdPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    // ---------------------------------------------------------------------------------------------
    // Vocabulary reuse: model/effort selections must be exactly ModelBadgeTable/EffortBarLevel's
    // own lists, not a second, independently maintained one.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ModelFamilies_IsExactlyModelBadgeTablesFamilies()
    {
        var viewModel = new CreateSessionDialogViewModel();
        Assert.Equal(ModelBadgeTable.Families, viewModel.ModelFamilies);
    }

    [Fact]
    public void EffortLevels_IsExactlyEffortBarLevelsLevels()
    {
        var viewModel = new CreateSessionDialogViewModel();
        Assert.Equal(EffortBarLevel.Levels, viewModel.EffortLevels);
    }

    [Fact]
    public void DefaultSelections_AreValidMembersOfTheirVocabularies()
    {
        var viewModel = new CreateSessionDialogViewModel();
        Assert.Contains(viewModel.SelectedModelFamily, ModelBadgeTable.Families);
        Assert.Contains(viewModel.SelectedEffortLevel, EffortBarLevel.Levels);
    }

    // ---------------------------------------------------------------------------------------------
    // GUID uniqueness.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void BuildArguments_DifferentGuidsProduceDifferentSessionIdArguments()
    {
        var viewModel = new CreateSessionDialogViewModel();

        var first = viewModel.BuildArguments(Guid.NewGuid());
        var second = viewModel.BuildArguments(Guid.NewGuid());

        Assert.NotEqual(first[1], second[1]); // element 1 is the session-id value
    }

    [Fact]
    public void Confirm_GeneratesAFreshUniqueGuidEachTime()
    {
        var seen = new HashSet<Guid>();
        var viewModel = new CreateSessionDialogViewModel(
            specBuilder: (arguments, workingDirectory) => FakeSpec(),
            sessionStarter: FakeStarter);

        for (var i = 0; i < 25; i++)
        {
            viewModel.ConfirmCommand.Execute(null);
            Assert.Null(viewModel.ErrorMessage);
            Assert.NotNull(viewModel.LastGeneratedSessionId);
            Assert.True(seen.Add(viewModel.LastGeneratedSessionId!.Value), "Session GUID was reused across confirms.");
            viewModel.LastStartedSession!.Dispose();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Argv is a real array: the session-id/name/model/effort/extra-args shape, and (the load-bearing
    // case) an extra-arg value containing a space must arrive as ONE array element, never re-split.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void BuildArguments_HasSessionIdAndNameFirstPerLockedInDecision3()
    {
        var viewModel = new CreateSessionDialogViewModel { DisplayName = "my session" };
        var sessionId = Guid.NewGuid();

        var arguments = viewModel.BuildArguments(sessionId);

        Assert.Equal("--session-id", arguments[0]);
        Assert.Equal(sessionId.ToString(), arguments[1]);
        Assert.Equal("--name", arguments[2]);
        Assert.Equal("my session", arguments[3]);
    }

    [Fact]
    public void BuildArguments_IncludesSelectedModelAndEffort()
    {
        var viewModel = new CreateSessionDialogViewModel
        {
            SelectedModelFamily = "claude-opus",
            SelectedEffortLevel = "high",
        };

        var arguments = viewModel.BuildArguments(Guid.NewGuid());

        Assert.Contains("--model", arguments);
        Assert.Equal("claude-opus", arguments[Array.IndexOf(arguments, "--model") + 1]);
        Assert.Contains("--effort", arguments);
        Assert.Equal("high", arguments[Array.IndexOf(arguments, "--effort") + 1]);
    }

    [Fact]
    public void BuildArguments_ExtraArgValueContainingASpace_IsNotReSplit()
    {
        var viewModel = new CreateSessionDialogViewModel
        {
            ExtraArgsText = "--add-dir \"C:\\some path\\with spaces\"",
        };

        var arguments = viewModel.BuildArguments(Guid.NewGuid());

        // The path (one logical value) must appear as exactly one array element.
        Assert.Contains(@"C:\some path\with spaces", arguments);
        Assert.DoesNotContain("path", arguments); // would appear as its own element if naively re-split
    }

    [Fact]
    public void BuildArguments_ExtraArgsAppendAfterModelAndEffort()
    {
        var viewModel = new CreateSessionDialogViewModel
        {
            ExtraArgsText = "--permission-mode bypassPermissions",
        };

        var arguments = viewModel.BuildArguments(Guid.NewGuid());

        Assert.Equal("--permission-mode", arguments[^2]);
        Assert.Equal("bypassPermissions", arguments[^1]);
    }

    [Fact]
    public void BuildArguments_ReturnsARealArrayNotAConcatenatedString()
    {
        var viewModel = new CreateSessionDialogViewModel { DisplayName = "a b" };
        IReadOnlyList<string> arguments = viewModel.BuildArguments(Guid.NewGuid());

        // Each element is independently addressable and the display name (which itself contains a
        // space) is exactly one element - proof this is argv, not a single joined command string.
        Assert.IsType<string[]>(arguments);
        Assert.Equal("a b", arguments[3]);
        Assert.DoesNotContain(arguments, element => element.Contains(' ') && element != "a b");
    }

    // ---------------------------------------------------------------------------------------------
    // Advanced-args warning.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void AdvancedArgsWarning_FlagsTheFieldAsTrustedUnvalidatedInput()
    {
        Assert.Contains("not validated", CreateSessionDialogViewModel.AdvancedArgsWarning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dangerously", CreateSessionDialogViewModel.AdvancedArgsWarning, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------------------
    // Confirm/Cancel lifecycle.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Cancel_RaisesRequestCloseWithFalseAndNeverStartsASession()
    {
        var starterCalled = false;
        var viewModel = new CreateSessionDialogViewModel(
            specBuilder: (arguments, workingDirectory) => FakeSpec(),
            sessionStarter: spec => { starterCalled = true; return FakeStarter(spec); });

        bool? confirmedResult = null;
        viewModel.RequestClose += (_, confirmed) => confirmedResult = confirmed;

        viewModel.CancelCommand.Execute(null);

        Assert.False(confirmedResult);
        Assert.False(starterCalled);
        Assert.Null(viewModel.LastStartedSession);
    }

    [Fact]
    public void Confirm_Failure_SetsErrorMessageAndDoesNotRaiseRequestClose()
    {
        var viewModel = new CreateSessionDialogViewModel(
            specBuilder: (arguments, workingDirectory) => throw new PtySessionLaunchException("boom"));

        var closeRaised = false;
        viewModel.RequestClose += (_, _) => closeRaised = true;

        viewModel.ConfirmCommand.Execute(null);

        Assert.False(closeRaised);
        Assert.Equal("boom", viewModel.ErrorMessage);
        Assert.Null(viewModel.LastStartedSession);
    }

    [Fact]
    public void Confirm_Success_RaisesRequestCloseWithTrueAndSetsLastStartedSession()
    {
        var viewModel = new CreateSessionDialogViewModel(
            specBuilder: (arguments, workingDirectory) => FakeSpec(),
            sessionStarter: FakeStarter);

        bool? confirmedResult = null;
        viewModel.RequestClose += (_, confirmed) => confirmedResult = confirmed;

        viewModel.ConfirmCommand.Execute(null);

        Assert.True(confirmedResult);
        Assert.NotNull(viewModel.LastStartedSession);
        viewModel.LastStartedSession!.Dispose();
    }

    // ---------------------------------------------------------------------------------------------
    // Real end-to-end proof: an actual PtySession starts off this ViewModel's argv, using cmd.exe as
    // the controllable stand-in for claude.exe (same rule PtySessionSmokeTest follows).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Confirm_ActuallyStartsAPtySession()
    {
        var viewModel = new CreateSessionDialogViewModel(
            specBuilder: (arguments, workingDirectory) => new PtyLaunchSpec
            {
                ExecutablePath = CmdPath(),
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
            })
        {
            DisplayName = "smoke test session",
            SelectedModelFamily = "claude-sonnet",
            SelectedEffortLevel = "medium",
            ExtraArgsText = "--fake-flag \"value with space\"",
        };

        viewModel.ConfirmCommand.Execute(null);

        Assert.Null(viewModel.ErrorMessage);
        Assert.NotNull(viewModel.LastStartedSession);
        Assert.NotNull(viewModel.LastLaunchSpec);

        // Prove the argv actually reached the spec unmangled, including the quoted, space-containing
        // extra-arg value.
        Assert.Contains("value with space", viewModel.LastLaunchSpec!.Arguments);
        Assert.True(viewModel.LastStartedSession!.ProcessId > 0);

        viewModel.LastStartedSession.Dispose();
    }

    private static PtyLaunchSpec FakeSpec() => new()
    {
        ExecutablePath = CmdPath(),
        Arguments = new[] { "/c", "exit" },
        WorkingDirectory = Path.GetTempPath(),
    };

    private static PtySession FakeStarter(PtyLaunchSpec spec) => PtySession.Start(spec);
}
