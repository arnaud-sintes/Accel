namespace Accel.Tests;

using Accel.App.Services;

/// <summary>Fake <see cref="IGitActionsDialogService"/>: returns a fixed commit message (or null,
/// for "cancelled") instead of showing a real dialog - same role
/// <see cref="FakeFilesEntryDialogService"/> plays for panel B's file-tree dialogs.</summary>
internal sealed class FakeGitActionsDialogService : IGitActionsDialogService
{
    public string? CommitMessage { get; set; } = "Test commit";

    public string? PromptForCommitMessage(int stagedFileCount) => CommitMessage;
}
