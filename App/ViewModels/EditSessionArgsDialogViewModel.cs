namespace Accel.App.ViewModels;

using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Accel.App.Services;

/// <summary>
/// "Edit launch args…" popup's ViewModel - lets a user change what `claude --resume &lt;id&gt;` will
/// additionally pass the next time a session is resumed. Deliberately narrower than
/// <see cref="CreateSessionDialogViewModel"/>: no display name, model, effort, or working-directory
/// fields, because none of those are meaningful for a resume (`--resume` reattaches the existing
/// transcript, which already carries its own model/effort/cwd) - only the combo-driven permission
/// mode (<see cref="CommonCliFlags"/>) plus the same free-text extra-args box, both reused unchanged
/// from the create dialog's vocabulary so a user only has to learn one shape.
///
/// <para>Same no-WPF-dependency shape as <see cref="RenameSessionDialogViewModel"/>: pure state and
/// validation here, dialog code-behind only wires <c>DataContext</c> and reacts to
/// <see cref="RequestClose"/>. The actual storage of <see cref="ConfirmedArguments"/> against a
/// session id is the caller's job (<c>MainWindow.EditSessionArgs_Click</c>, via
/// <see cref="SessionResumeArgsStore"/>) - this class never touches that store itself.</para>
/// </summary>
public sealed partial class EditSessionArgsDialogViewModel : ObservableObject
{
    [ObservableProperty]
    private PermissionModeOption _selectedPermissionMode;

    [ObservableProperty]
    private string _extraArgsText = string.Empty;

    /// <param name="initialArguments">The session's currently-stored extra args (from
    /// <see cref="SessionResumeArgsStore.Get"/>), pre-decomposed back into the combo state plus a
    /// free-text remainder - see <see cref="Decompose"/>. Empty/null means "nothing set yet", i.e.
    /// every control starts at its default state.</param>
    public EditSessionArgsDialogViewModel(IReadOnlyList<string>? initialArguments)
    {
        var (permissionMode, remainder) = Decompose(initialArguments);
        _selectedPermissionMode = permissionMode;
        _extraArgsText = remainder;
    }

    /// <summary>Combo-box source for <see cref="SelectedPermissionModeChoice"/> - see <see cref="CommonCliFlags"/>.</summary>
    public IReadOnlyList<PermissionModeChoice> PermissionModeChoices => CommonCliFlags.PermissionModeChoices;

    /// <summary>Same "bind the whole item via SelectedItem" fix as
    /// <see cref="CreateSessionDialogViewModel.SelectedPermissionModeChoice"/> - see that property's
    /// doc comment for why. <see cref="SelectedPermissionMode"/> (the enum) stays the single source of
    /// truth that <see cref="BuildArguments"/> and this class's tests use.</summary>
    public PermissionModeChoice SelectedPermissionModeChoice
    {
        get => PermissionModeChoices.First(choice => choice.Value == SelectedPermissionMode);
        set => SelectedPermissionMode = value.Value;
    }

    partial void OnSelectedPermissionModeChanged(PermissionModeOption value) =>
        OnPropertyChanged(nameof(SelectedPermissionModeChoice));

    /// <summary>Same wording as <see cref="CreateSessionDialogViewModel.AdvancedArgsWarning"/> - one
    /// source of truth for the sentence would need a shared base class for two otherwise-unrelated
    /// ViewModels, which is not worth it for a single constant; the wording itself must stay identical
    /// since both dialogs describe the exact same trust boundary.</summary>
    public const string AdvancedArgsWarning = CreateSessionDialogViewModel.AdvancedArgsWarning;

    /// <summary>The argv tail from the most recent successful <see cref="Confirm"/>, or null before one
    /// has happened (or after <see cref="Cancel"/>).</summary>
    public string[]? ConfirmedArguments { get; private set; }

    /// <summary>Raised once <see cref="Confirm"/> or <see cref="Cancel"/> runs - same "confirmed" bool
    /// contract as <see cref="CreateSessionDialogViewModel.RequestClose"/>.</summary>
    public event EventHandler<bool>? RequestClose;

    /// <summary>
    /// Builds the argv tail this session's next resume will get appended: the combo-driven permission
    /// mode first (only if it differs from the default - see <see cref="CommonCliFlags.BuildArguments"/>),
    /// then the tokenized extra-args tail - same order and same tokenizer (<see cref="ExtraArgsParser"/>)
    /// as <see cref="CreateSessionDialogViewModel.BuildArguments"/>. Pure and side-effect-free, so it is
    /// unit-testable without a dialog.
    /// </summary>
    public string[] BuildArguments()
    {
        var arguments = new List<string>();
        arguments.AddRange(CommonCliFlags.BuildArguments(SelectedPermissionMode));
        arguments.AddRange(ExtraArgsParser.Parse(ExtraArgsText));
        return arguments.ToArray();
    }

    [RelayCommand]
    private void Confirm()
    {
        ConfirmedArguments = BuildArguments();
        RequestClose?.Invoke(this, true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, false);

    /// <summary>
    /// Splits a previously-built argv tail back into combo state plus a free-text remainder, so
    /// re-opening this dialog for a session that already has stored args shows them pre-selected
    /// instead of forcing the user to retype everything into the free-text box. Recognises exactly the
    /// two-element <c>--permission-mode &lt;value&gt;</c> pair (wherever it appears in the array) and
    /// re-joins every other token, space-separated, into the extra-args text box. Any token containing
    /// whitespace is re-quoted so it survives a round trip back through <see cref="ExtraArgsParser.Parse"/>
    /// unchanged.
    /// </summary>
    internal static (PermissionModeOption PermissionMode, string Remainder) Decompose(IReadOnlyList<string>? arguments)
    {
        var permissionMode = PermissionModeOption.None;
        var remainder = new List<string>();

        if (arguments is not null)
        {
            for (var i = 0; i < arguments.Count; i++)
            {
                var token = arguments[i];
                if (string.Equals(token, "--permission-mode", StringComparison.Ordinal) && i + 1 < arguments.Count)
                {
                    var value = arguments[i + 1];
                    foreach (var choice in CommonCliFlags.PermissionModeChoices)
                    {
                        if (string.Equals(CommonCliFlags.ToFlagValue(choice.Value), value, StringComparison.Ordinal))
                        {
                            permissionMode = choice.Value;
                            break;
                        }
                    }

                    i++;
                    continue;
                }

                remainder.Add(RequoteIfNeeded(token));
            }
        }

        return (permissionMode, string.Join(' ', remainder));
    }

    private static string RequoteIfNeeded(string token)
    {
        if (token.Length > 0 && token.IndexOfAny(new[] { ' ', '\t', '\n' }) < 0)
        {
            return token;
        }

        return "\"" + token.Replace("\"", "\"\"") + "\"";
    }
}
