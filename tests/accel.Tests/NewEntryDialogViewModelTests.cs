namespace Accel.Tests;

using Accel.App.ViewModels;
using Xunit;

/// <summary>Unit tests for <see cref="NewEntryDialogViewModel"/> - pure validation logic, no WPF.
/// Mirrors <see cref="RenameSessionDialogViewModelTests"/>'s shape.</summary>
public class NewEntryDialogViewModelTests
{
    [Fact]
    public void Confirm_ValidName_ClosesWithConfirmedName()
    {
        var viewModel = new NewEntryDialogViewModel(NewFileSystemEntryKind.File, string.Empty) { Name = "  readme.txt  " };

        bool? confirmed = null;
        viewModel.RequestClose += (_, ok) => confirmed = ok;
        viewModel.ConfirmCommand.Execute(null);

        Assert.True(confirmed);
        Assert.Equal("readme.txt", viewModel.ConfirmedName);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public void Confirm_BlankName_SetsErrorAndNeverCloses()
    {
        var viewModel = new NewEntryDialogViewModel(NewFileSystemEntryKind.Folder, string.Empty) { Name = "   " };

        bool closeRaised = false;
        viewModel.RequestClose += (_, _) => closeRaised = true;
        viewModel.ConfirmCommand.Execute(null);

        Assert.False(closeRaised);
        Assert.Null(viewModel.ConfirmedName);
        Assert.NotNull(viewModel.ErrorMessage);
    }

    [Theory]
    [InlineData("bad?name")]
    [InlineData("bad:name")]
    [InlineData("bad/name")]
    public void Confirm_InvalidNameChar_SetsErrorAndNeverCloses(string hostileName)
    {
        var viewModel = new NewEntryDialogViewModel(NewFileSystemEntryKind.File, string.Empty) { Name = hostileName };

        bool closeRaised = false;
        viewModel.RequestClose += (_, _) => closeRaised = true;
        viewModel.ConfirmCommand.Execute(null);

        Assert.False(closeRaised);
        Assert.Null(viewModel.ConfirmedName);
        Assert.NotNull(viewModel.ErrorMessage);
    }

    [Fact]
    public void Cancel_ClosesWithFalse_AndNeverSetsConfirmedName()
    {
        var viewModel = new NewEntryDialogViewModel(NewFileSystemEntryKind.File, string.Empty) { Name = "readme.txt" };

        bool? confirmed = null;
        viewModel.RequestClose += (_, ok) => confirmed = ok;
        viewModel.CancelCommand.Execute(null);

        Assert.False(confirmed);
        Assert.Null(viewModel.ConfirmedName);
    }

    [Theory]
    [InlineData(NewFileSystemEntryKind.File, "New file", "File name")]
    [InlineData(NewFileSystemEntryKind.Folder, "New folder", "Folder name")]
    public void TitleAndPromptLabel_VaryByKind(NewFileSystemEntryKind kind, string expectedTitle, string expectedPrompt)
    {
        var viewModel = new NewEntryDialogViewModel(kind, string.Empty);

        Assert.Equal(expectedTitle, viewModel.Title);
        Assert.Equal(expectedPrompt, viewModel.PromptLabel);
    }
}
