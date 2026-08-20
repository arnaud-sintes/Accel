namespace Accel.Tests;

using Accel.App.Services;
using Accel.App.ViewModels;
using Xunit;

/// <summary>Unit tests for <see cref="MoveRenameDialogViewModel"/> - pure validation/browse logic, no
/// WPF, no real <see cref="System.Windows.Forms.FolderBrowserDialog"/>.</summary>
public class MoveRenameDialogViewModelTests
{
    private sealed class FakeFolderPicker : IFolderPickerService
    {
        private readonly string? _result;
        public FakeFolderPicker(string? result) => _result = result;
        public string? PickFolder(string description) => _result;
    }

    [Fact]
    public void Constructor_PreFillsNewPathFromCurrentPath()
    {
        var viewModel = new MoveRenameDialogViewModel(@"C:\proj\old.txt", isDirectory: false, new FakeFolderPicker(null));

        Assert.Equal(@"C:\proj\old.txt", viewModel.CurrentPath);
        Assert.Equal(@"C:\proj\old.txt", viewModel.NewPath);
    }

    [Fact]
    public void Browse_Cancelled_LeavesNewPathUnchanged()
    {
        var viewModel = new MoveRenameDialogViewModel(@"C:\proj\old.txt", isDirectory: false, new FakeFolderPicker(null));

        viewModel.BrowseCommand.Execute(null);

        Assert.Equal(@"C:\proj\old.txt", viewModel.NewPath);
    }

    [Fact]
    public void Browse_PicksFolder_CombinesWithAlreadyTypedFileName()
    {
        var viewModel = new MoveRenameDialogViewModel(@"C:\proj\old.txt", isDirectory: false, new FakeFolderPicker(@"D:\dest"))
        {
            NewPath = @"C:\proj\renamed.txt",
        };

        viewModel.BrowseCommand.Execute(null);

        Assert.Equal(@"D:\dest\renamed.txt", viewModel.NewPath);
    }

    [Fact]
    public void Confirm_ValidNewPath_ClosesWithConfirmedNewPath()
    {
        var viewModel = new MoveRenameDialogViewModel(@"C:\proj\old.txt", isDirectory: false, new FakeFolderPicker(null))
        {
            NewPath = @"C:\proj\new.txt",
        };

        bool? confirmed = null;
        viewModel.RequestClose += (_, ok) => confirmed = ok;
        viewModel.ConfirmCommand.Execute(null);

        Assert.True(confirmed);
        Assert.Equal(@"C:\proj\new.txt", viewModel.ConfirmedNewPath);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public void Confirm_BlankPath_SetsErrorAndNeverCloses()
    {
        var viewModel = new MoveRenameDialogViewModel(@"C:\proj\old.txt", isDirectory: false, new FakeFolderPicker(null))
        {
            NewPath = "   ",
        };

        bool closeRaised = false;
        viewModel.RequestClose += (_, _) => closeRaised = true;
        viewModel.ConfirmCommand.Execute(null);

        Assert.False(closeRaised);
        Assert.Null(viewModel.ConfirmedNewPath);
        Assert.NotNull(viewModel.ErrorMessage);
    }

    [Fact]
    public void Confirm_UnchangedPath_SetsErrorAndNeverCloses()
    {
        var viewModel = new MoveRenameDialogViewModel(@"C:\proj\old.txt", isDirectory: false, new FakeFolderPicker(null));

        bool closeRaised = false;
        viewModel.RequestClose += (_, _) => closeRaised = true;
        viewModel.ConfirmCommand.Execute(null);

        Assert.False(closeRaised);
        Assert.Null(viewModel.ConfirmedNewPath);
        Assert.NotNull(viewModel.ErrorMessage);
    }

    [Fact]
    public void Cancel_ClosesWithFalse_AndNeverSetsConfirmedNewPath()
    {
        var viewModel = new MoveRenameDialogViewModel(@"C:\proj\old.txt", isDirectory: false, new FakeFolderPicker(null))
        {
            NewPath = @"C:\proj\new.txt",
        };

        bool? confirmed = null;
        viewModel.RequestClose += (_, ok) => confirmed = ok;
        viewModel.CancelCommand.Execute(null);

        Assert.False(confirmed);
        Assert.Null(viewModel.ConfirmedNewPath);
    }
}
