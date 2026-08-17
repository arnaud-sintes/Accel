namespace Accel.Tests;

using System;
using Accel.App.Services;
using Accel.App.ViewModels;
using Xunit;

/// <summary>
/// Unit tests for <see cref="EditSessionArgsDialogViewModel"/> - "Edit launch args…"'s ViewModel. Covers
/// the default (nothing-set) state, argv construction matching <see cref="CommonCliFlags"/>'s order, and
/// the round trip through <see cref="EditSessionArgsDialogViewModel.Decompose"/> that lets re-opening the
/// dialog for a session with stored args show them pre-selected.
/// </summary>
public class EditSessionArgsDialogViewModelTests
{
    [Fact]
    public void Constructor_NullInitialArguments_DefaultsToNothingSelected()
    {
        var viewModel = new EditSessionArgsDialogViewModel(initialArguments: null);

        Assert.Equal(PermissionModeOption.None, viewModel.SelectedPermissionMode);
        Assert.Equal(string.Empty, viewModel.ExtraArgsText);
    }

    [Fact]
    public void BuildArguments_MatchesCommonCliFlagsOrder()
    {
        var viewModel = new EditSessionArgsDialogViewModel(initialArguments: null)
        {
            SelectedPermissionMode = PermissionModeOption.AcceptEdits,
            ExtraArgsText = "--add-dir C:\\repo",
        };

        var arguments = viewModel.BuildArguments();

        Assert.Equal(
            new[] { "--permission-mode", "acceptEdits", "--add-dir", "C:\\repo" },
            arguments);
    }

    [Fact]
    public void BuildArguments_DefaultPermissionMode_AddsNoPermissionModeFlag()
    {
        var viewModel = new EditSessionArgsDialogViewModel(initialArguments: null)
        {
            ExtraArgsText = "--add-dir C:\\repo",
        };

        var arguments = viewModel.BuildArguments();

        Assert.Equal(new[] { "--add-dir", "C:\\repo" }, arguments);
    }

    [Fact]
    public void SelectedPermissionModeChoice_ReflectsAndUpdatesSelectedPermissionMode()
    {
        var viewModel = new EditSessionArgsDialogViewModel(initialArguments: null);

        Assert.Equal(PermissionModeOption.None, viewModel.SelectedPermissionModeChoice.Value);

        viewModel.SelectedPermissionModeChoice = viewModel.PermissionModeChoices[3]; // Plan

        Assert.Equal(PermissionModeOption.Plan, viewModel.SelectedPermissionMode);
    }

    [Fact]
    public void Confirm_SetsConfirmedArgumentsAndRaisesRequestCloseTrue()
    {
        var viewModel = new EditSessionArgsDialogViewModel(initialArguments: null)
        {
            SelectedPermissionMode = PermissionModeOption.BypassPermissions,
        };

        bool? confirmed = null;
        viewModel.RequestClose += (_, wasConfirmed) => confirmed = wasConfirmed;

        viewModel.ConfirmCommand.Execute(null);

        Assert.True(confirmed);
        Assert.Equal(new[] { "--permission-mode", "bypassPermissions" }, viewModel.ConfirmedArguments);
    }

    [Fact]
    public void Cancel_LeavesConfirmedArgumentsNullAndRaisesRequestCloseFalse()
    {
        var viewModel = new EditSessionArgsDialogViewModel(initialArguments: null);

        bool? confirmed = null;
        viewModel.RequestClose += (_, wasConfirmed) => confirmed = wasConfirmed;

        viewModel.CancelCommand.Execute(null);

        Assert.False(confirmed);
        Assert.Null(viewModel.ConfirmedArguments);
    }

    // -----------------------------------------------------------------------------------------------
    // Decompose: re-opening the dialog for a session that already has stored args must pre-select the
    // same combo state, round-tripping through BuildArguments unchanged.
    // -----------------------------------------------------------------------------------------------

    [Fact]
    public void Decompose_EmptyOrNull_IsAllDefaults()
    {
        var (mode, remainder) = EditSessionArgsDialogViewModel.Decompose(null);

        Assert.Equal(PermissionModeOption.None, mode);
        Assert.Equal(string.Empty, remainder);
    }

    [Fact]
    public void Decompose_RecognisesPermissionMode()
    {
        var (mode, remainder) = EditSessionArgsDialogViewModel.Decompose(
            new[] { "--permission-mode", "bypassPermissions" });

        Assert.Equal(PermissionModeOption.BypassPermissions, mode);
        Assert.Equal(string.Empty, remainder);
    }

    [Fact]
    public void Decompose_LeavesUnrecognisedTokensInRemainder()
    {
        var (_, remainder) = EditSessionArgsDialogViewModel.Decompose(new[] { "--add-dir", "C:\\repo" });

        Assert.Equal("--add-dir C:\\repo", remainder);
    }

    [Fact]
    public void Decompose_DangerouslySkipPermissions_IsNoLongerSpecialCasedAndStaysInRemainder()
    {
        var (mode, remainder) = EditSessionArgsDialogViewModel.Decompose(new[] { "--dangerously-skip-permissions" });

        Assert.Equal(PermissionModeOption.None, mode);
        Assert.Equal("--dangerously-skip-permissions", remainder);
    }

    [Fact]
    public void RoundTrip_StoredArguments_ReproduceTheSameArgvViaTheDialog()
    {
        var original = new[] { "--permission-mode", "plan", "--add-dir", "C:\\repo" };

        var viewModel = new EditSessionArgsDialogViewModel(original);
        var rebuilt = viewModel.BuildArguments();

        Assert.Equal(original, rebuilt);
    }

    [Fact]
    public void RoundTrip_RemainderTokenContainingASpace_SurvivesAsOneElement()
    {
        var original = new[] { "--add-dir", "C:\\some path\\with spaces" };

        var viewModel = new EditSessionArgsDialogViewModel(original);
        var rebuilt = viewModel.BuildArguments();

        Assert.Equal(original, rebuilt);
    }
}
