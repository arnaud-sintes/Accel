namespace Accel.Tests;

using Accel.App.ViewModels;
using Xunit;

/// <summary>
/// P4-T2: unit tests for <see cref="RenameSessionDialogViewModel"/> - pure validation logic, no WPF, no
/// PTY. The actual injection (writing <c>/rename</c> to a live session) is
/// <c>MainWindow.RenameSession_Click</c>'s job and is out of this file's scope, exactly like
/// <see cref="CreateSessionDialogViewModel"/>'s split between argv-construction (unit-tested here) and
/// launching (unit-tested there, but with a real - if fake-argv - process).
/// </summary>
public class RenameSessionDialogViewModelTests
{
    [Fact]
    public void Constructor_PreFillsNameFromInitialName()
    {
        var viewModel = new RenameSessionDialogViewModel("current-name");
        Assert.Equal("current-name", viewModel.Name);
    }

    [Fact]
    public void Constructor_NullInitialName_LeavesNameEmpty()
    {
        var viewModel = new RenameSessionDialogViewModel(null);
        Assert.Equal(string.Empty, viewModel.Name);
    }

    [Fact]
    public void Confirm_TrimsWhitespaceAndClosesWithTheConfirmedName()
    {
        var viewModel = new RenameSessionDialogViewModel("old");
        viewModel.Name = "  New Name  ";

        bool? confirmed = null;
        viewModel.RequestClose += (_, ok) => confirmed = ok;
        viewModel.ConfirmCommand.Execute(null);

        Assert.True(confirmed);
        Assert.Equal("New Name", viewModel.ConfirmedName);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public void Confirm_BlankName_SetsErrorMessageAndNeverCloses()
    {
        var viewModel = new RenameSessionDialogViewModel("old") { Name = "   " };

        bool closeRaised = false;
        viewModel.RequestClose += (_, _) => closeRaised = true;
        viewModel.ConfirmCommand.Execute(null);

        Assert.False(closeRaised);
        Assert.Null(viewModel.ConfirmedName);
        Assert.NotNull(viewModel.ErrorMessage);
    }

    [Theory]
    [InlineData("bad\rname")]
    [InlineData("bad\nname")]
    [InlineData("bad\u001bname")]
    public void Confirm_NameFailingTheSharedSanitizer_SetsErrorMessageAndNeverCloses(string hostileName)
    {
        var viewModel = new RenameSessionDialogViewModel("old") { Name = hostileName };

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
        var viewModel = new RenameSessionDialogViewModel("old") { Name = "New Name" };

        bool? confirmed = null;
        viewModel.RequestClose += (_, ok) => confirmed = ok;
        viewModel.CancelCommand.Execute(null);

        Assert.False(confirmed);
        Assert.Null(viewModel.ConfirmedName);
    }
}
