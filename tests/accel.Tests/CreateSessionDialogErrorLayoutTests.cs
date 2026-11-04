namespace Accel.Tests;

using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Accel.App;
using Accel.App.ViewModels;
using Accel.Orchestration;
using Xunit;

/// <summary>
/// A launch error in the Create-session dialog must be readable. The dialog used to be a fixed-height window
/// whose error card sat in whatever space the fields left over, so a multi-line message (the "claude not
/// found" / "claude is a shim" launch errors) was squeezed into a one-line strip and cut off.
/// </summary>
public class CreateSessionDialogErrorLayoutTests
{
    private const string LongError =
        "Claude Code was found at 'C:\\Users\\me\\AppData\\Roaming\\npm\\claude.cmd', but that is an npm-style " +
        "launcher script (a shim), and Accel can only start the native claude.exe - launching a shim through " +
        "node.exe is not supported yet. Install the native build of Claude Code (it puts claude.exe in " +
        "%USERPROFILE%\\.local\\bin), make sure that folder is on PATH, and restart Accel.";

    [Fact]
    public void TheWindow_SizesToItsContent_SoAnErrorHasRoom()
    {
        RunOnSta(() =>
        {
            var dialog = new CreateSessionDialog(new CreateSessionDialogViewModel());

            // The structural cause: a fixed Height cannot grow for an error. SizeToContent can, and
            // MinHeight keeps the usual size when there is none.
            Assert.True(double.IsNaN(dialog.Height));
            Assert.Equal(SizeToContent.Height, dialog.SizeToContent);
            Assert.True(dialog.MinHeight >= 620);
        });
    }

    [Fact]
    public void AMultiLineError_IsLaidOutInFull_NotClippedToAStrip()
    {
        RunOnSta(() =>
        {
            var viewModel = new CreateSessionDialogViewModel(
                specBuilder: (_, _) => throw new PtySessionLaunchException(LongError));
            var dialog = new CreateSessionDialog(viewModel);
            var withoutError = LayOut(dialog).DesiredSize.Height;

            viewModel.ConfirmCommand.Execute(null);
            Pump();
            var root = LayOut(dialog);

            var scroller = Assert.IsType<ScrollViewer>(dialog.ErrorText.Parent);
            Assert.Equal(LongError, dialog.ErrorText.Text);

            // Several lines tall (the strip in the bug report was a single clipped line)...
            Assert.True(
                dialog.ErrorText.ActualHeight > 3 * dialog.ErrorText.FontSize,
                $"the error text is only {dialog.ErrorText.ActualHeight}px tall");

            // ...and fully visible: nothing left to scroll to for a message of this length.
            Assert.Equal(0, scroller.ScrollableHeight);

            // The content wants more room than it did without the error, which is what SizeToContent
            // turns into a taller window.
            Assert.True(
                root.DesiredSize.Height > withoutError + 2 * dialog.ErrorText.FontSize,
                $"desired height {root.DesiredSize.Height}px vs {withoutError}px without the error");
        });
    }

    [Fact]
    public void AnAbsurdlyLongError_ScrollsInsideItsCard_InsteadOfGrowingWithoutLimit()
    {
        RunOnSta(() =>
        {
            var viewModel = new CreateSessionDialogViewModel(
                specBuilder: (_, _) => throw new PtySessionLaunchException(string.Join(" ", Enumerable.Repeat(LongError, 60))));
            var dialog = new CreateSessionDialog(viewModel);

            viewModel.ConfirmCommand.Execute(null);
            Pump();
            LayOut(dialog);

            var scroller = Assert.IsType<ScrollViewer>(dialog.ErrorText.Parent);
            Assert.True(scroller.ScrollableHeight > 0);
            Assert.True(scroller.ActualHeight <= scroller.MaxHeight + 0.5);
        });
    }

    /// <summary>
    /// Lays the dialog's content out at its natural height. An unshown <see cref="Window"/> does not lay out
    /// its content, so this measures the content root itself - which is the part with the layout under test;
    /// the window's own SizeToContent is covered by the structural test above.
    /// </summary>
    private static FrameworkElement LayOut(CreateSessionDialog dialog)
    {
        var root = Assert.IsAssignableFrom<FrameworkElement>(dialog.Content);
        root.Measure(new Size(dialog.Width, double.PositiveInfinity));
        root.Arrange(new Rect(0, 0, dialog.Width, root.DesiredSize.Height));
        root.UpdateLayout();
        return root;
    }

    private static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static void RunOnSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            throw captured;
        }
    }
}
