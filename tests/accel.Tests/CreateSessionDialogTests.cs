namespace Accel.Tests;

using System;
using System.IO;
using System.Threading;
using System.Windows;
using Accel.App;
using Accel.App.ViewModels;
using Accel.Orchestration;
using Xunit;

/// <summary>
/// P2-T6: proves the actual <see cref="CreateSessionDialog"/> WPF window - not just its ViewModel -
/// wires the advanced-args warning visibly and round-trips a confirm into a started
/// <see cref="PtySession"/>. Constructing a <see cref="Window"/> requires an STA thread (WPF's
/// <c>Dispatcher</c>/<c>ContentElement</c> machinery asserts on it), which xUnit's default MTA test
/// thread is not - see <see cref="RunOnSta"/>, the same pattern <c>Program.cs</c>'s <c>ui-preview</c>
/// verb uses for the same reason.
/// </summary>
public class CreateSessionDialogTests
{
    [Fact]
    public void ExtraArgsWarningText_IsSetFromTheViewModelsSingleSourceOfTruth()
    {
        RunOnSta(() =>
        {
            var viewModel = new CreateSessionDialogViewModel();
            var dialog = new CreateSessionDialog(viewModel);

            Assert.Equal(CreateSessionDialogViewModel.AdvancedArgsWarning, dialog.ExtraArgsWarningText.Text);

            // Visual flagging must not be colour-only: bold weight carries the "this is different"
            // signal independently of the (also present) warning colour, and the text itself names
            // the trust boundary explicitly.
            Assert.Equal(FontWeights.Bold, dialog.ExtraArgsWarningText.FontWeight);
            Assert.Contains("not validated", dialog.ExtraArgsWarningText.Text, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void ErrorText_HiddenInitially_VisibleAfterAFailedConfirm()
    {
        RunOnSta(() =>
        {
            var viewModel = new CreateSessionDialogViewModel(
                specBuilder: (_, _) => throw new PtySessionLaunchException("boom"));
            var dialog = new CreateSessionDialog(viewModel);

            Assert.Equal(Visibility.Collapsed, dialog.ErrorText.Visibility);

            viewModel.ConfirmCommand.Execute(null);

            // The code-behind's own visibility toggle (UpdateErrorVisibility) runs synchronously off
            // the PropertyChanged event, but the {Binding ErrorMessage} -> Text update is queued onto
            // the Dispatcher - with no message loop running on this STA thread (no Show()/Run()), it
            // needs one explicit pump before it is observable.
            PumpDispatcher();

            Assert.Equal(Visibility.Visible, dialog.ErrorText.Visibility);
            Assert.Equal("boom", dialog.ErrorText.Text);
        });
    }

    [Fact]
    public void ConfirmButton_ActuallyStartsARealPtySessionAndClosesTheDialog()
    {
        RunOnSta(() =>
        {
            var viewModel = new CreateSessionDialogViewModel(
                specBuilder: (arguments, workingDirectory) => new PtyLaunchSpec
                {
                    ExecutablePath = CmdPath(),
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
                })
            {
                DisplayName = "dialog smoke test",
            };
            var dialog = new CreateSessionDialog(viewModel);

            viewModel.ConfirmCommand.Execute(null);

            Assert.True(dialog.Confirmed);
            Assert.NotNull(viewModel.LastStartedSession);
            Assert.True(viewModel.LastStartedSession!.ProcessId > 0);

            viewModel.LastStartedSession.Dispose();
        });
    }

    /// <summary>Drains this thread's Dispatcher queue up to <c>Background</c> priority, so a WPF
    /// data-binding update queued by a source-property change becomes observable without a real
    /// <c>Dispatcher.Run</c> message loop.</summary>
    private static void PumpDispatcher() =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
            () => { }, System.Windows.Threading.DispatcherPriority.Background);

    private static string CmdPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    /// <summary>Runs <paramref name="action"/> on a dedicated STA thread and re-throws any exception
    /// on the calling (xUnit) thread, so assertion failures still fail the test normally.</summary>
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
