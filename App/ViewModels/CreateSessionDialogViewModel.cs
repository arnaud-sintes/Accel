namespace Accel.App.ViewModels;

using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Accel.App.Services;
using Accel.Metrics;
using Accel.Orchestration;

/// <summary>
/// P2-T6: the "Create session" popup's ViewModel - model/effort selection (reusing
/// <see cref="ModelBadgeTable"/>/<see cref="EffortBarLevel"/>'s exact vocabularies, never a second
/// list), a free-text extra-CLI-args field, and the argv-construction logic that turns a confirm
/// into a real launch: a fresh session GUID, <c>--session-id &lt;guid&gt; --name "&lt;display&gt;"</c>
/// plus the model/effort/extra-args, built and validated as a real argument array all the way to
/// <see cref="PtySession.CreateClaudeSpec"/> (locked-in decision 3 / the plan's hard security
/// requirement - see <see cref="BuildArguments"/> and <see cref="ExtraArgsParser"/>).
///
/// <para><b>No dependency on WPF.</b> Same shape as <c>RootsPanelViewModel</c>'s
/// <c>IFolderPickerService</c>/<c>IUserConfirmationService</c> split: this class never shows a
/// dialog itself, so it is unit-testable headlessly. <see cref="RequestClose"/> is what the actual
/// <c>Window</c> code-behind subscribes to, to close itself.</para>
///
/// <para><b>Test seams.</b> <paramref name="specBuilder"/>/<paramref name="sessionStarter"/> (ctor
/// parameters) let tests point the launch at <c>cmd.exe</c> and/or intercept the actual
/// <see cref="PtySession.Start"/> call, instead of resolving and launching the real `claude` - the
/// same split <c>PtySessionSmokeTest</c> already uses between "prove argv/launch mechanics" and
/// "actually run claude.exe".</para>
/// </summary>
public sealed partial class CreateSessionDialogViewModel : ObservableObject
{
    private readonly Func<Guid> _guidFactory;
    private readonly Func<IReadOnlyList<string>, string?, PtyLaunchSpec> _specBuilder;
    private readonly Func<PtyLaunchSpec, PtySession> _sessionStarter;
    private readonly IFolderPickerService _folderPicker;

    /// <summary>
    /// Shown next to the extra-args field, and read by the dialog's code-behind to set the warning
    /// label's text. This field's contents are passed to `claude` verbatim, one array element per
    /// token (see <see cref="ExtraArgsParser"/>) - including any <c>--dangerously-*</c> flag the
    /// user types - and are deliberately never validated or allowlisted here: locked-in decision 3's
    /// scope is correct argv construction, not a flag allowlist. This constant's job is to make that
    /// trust boundary visible in the UI rather than let the field look like an ordinary, safe text
    /// box.
    /// </summary>
    public const string AdvancedArgsWarning =
        "Advanced / trusted input - not validated. Anything typed here (including " +
        "--dangerously-* flags) is passed to claude verbatim, as separate arguments.";

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _selectedModelFamily;

    [ObservableProperty]
    private string _selectedEffortLevel;

    [ObservableProperty]
    private PermissionModeOption _selectedPermissionMode = PermissionModeOption.None;

    [ObservableProperty]
    private string _extraArgsText = string.Empty;

    [ObservableProperty]
    private string? _workingDirectory;

    [ObservableProperty]
    private string? _errorMessage;

    /// <param name="initialWorkingDirectory">Pre-fills <see cref="WorkingDirectory"/> - typically panel
    /// A's currently selected root (<c>RootsPanelViewModel.SelectedRootPath</c>), so the new session
    /// starts wherever the user is already looking rather than defaulting to some unrelated directory.
    /// Still user-editable via <see cref="BrowseWorkingDirectoryCommand"/> or direct text entry before
    /// confirming.</param>
    public CreateSessionDialogViewModel(
        Func<Guid>? guidFactory = null,
        Func<IReadOnlyList<string>, string?, PtyLaunchSpec>? specBuilder = null,
        Func<PtyLaunchSpec, PtySession>? sessionStarter = null,
        IFolderPickerService? folderPicker = null,
        string? initialWorkingDirectory = null)
    {
        _guidFactory = guidFactory ?? Guid.NewGuid;
        _specBuilder = specBuilder ?? DefaultSpecBuilder;
        _sessionStarter = sessionStarter ?? (spec => PtySession.Start(spec));
        _folderPicker = folderPicker ?? new WinFormsFolderPickerService();

        _selectedModelFamily = ModelFamilies.FirstOrDefault(f => f == "Sonnet") ?? ModelFamilies[0];
        _selectedEffortLevel = EffortLevels.FirstOrDefault(l => l == "medium") ?? EffortLevels[0];
        _workingDirectory = initialWorkingDirectory;
    }

    /// <summary>Model-family vocabulary - exactly <see cref="ModelBadgeTable.Families"/>, the same
    /// table panel A's badges resolve against.</summary>
    public IReadOnlyList<string> ModelFamilies => ModelBadgeTable.Families;

    /// <summary>What the dialog's model picker actually displays: the same families as
    /// <see cref="ModelFamilies"/>, ordered by ascending complexity (Haiku, Sonnet, Opus, Fable),
    /// each paired with a version-specific label (e.g. "Haiku 4.5") - see
    /// <see cref="ModelBadgeTable.FamilyDisplayNames"/>.</summary>
    public IReadOnlyList<ModelFamilyOption> ModelOptions => ModelBadgeTable.FamilyDisplayNames;

    /// <summary>Effort vocabulary - exactly <see cref="EffortBarLevel.Levels"/>, the same table
    /// panel A's effort bars resolve against.</summary>
    public IReadOnlyList<string> EffortLevels => EffortBarLevel.Levels;

    /// <summary>Whether <see cref="SelectedModelFamily"/> recognizes an effort level at all - per
    /// <see cref="ModelEffortTable"/>, false only for Haiku. The view binds this to the Effort
    /// field's visibility/enabled state, and <see cref="BuildArguments"/> uses it to omit
    /// <c>--effort</c> entirely for a family that doesn't support it, regardless of whatever value
    /// <see cref="SelectedEffortLevel"/> was left at from a previous model selection.</summary>
    public bool EffortSupported => ModelEffortTable.SupportsEffort(SelectedModelFamily);

    partial void OnSelectedModelFamilyChanged(string value) => OnPropertyChanged(nameof(EffortSupported));

    /// <summary>Combo-box source for <see cref="SelectedPermissionModeChoice"/> - see <see cref="CommonCliFlags"/>.</summary>
    public IReadOnlyList<PermissionModeChoice> PermissionModeChoices => CommonCliFlags.PermissionModeChoices;

    /// <summary>
    /// The dialog's Permission-mode ComboBox binds its <c>SelectedItem</c> to this full-object
    /// property rather than <c>SelectedValue</c>/<c>SelectedValuePath</c> against
    /// <see cref="SelectedPermissionMode"/> directly - the ComboBox's closed-box display was found
    /// (reported bug) to fall back to the record's own <c>ToString()</c> (e.g.
    /// <c>"PermissionModeChoice { Value = None, DisplayName = Default }"</c>) rather than honouring
    /// <c>DisplayMemberPath</c> when the bound value is a plain enum reached via
    /// <c>SelectedValuePath</c>. Binding the whole <see cref="PermissionModeChoice"/> item via
    /// <c>SelectedItem</c> sidesteps that path entirely - <see cref="SelectedPermissionMode"/> (the
    /// enum <see cref="BuildArguments"/> and every existing caller/test actually use) stays the
    /// single source of truth; this property is a thin, order-independent view over it.
    /// </summary>
    public PermissionModeChoice SelectedPermissionModeChoice
    {
        get => PermissionModeChoices.First(choice => choice.Value == SelectedPermissionMode);
        set => SelectedPermissionMode = value.Value;
    }

    partial void OnSelectedPermissionModeChanged(PermissionModeOption value) =>
        OnPropertyChanged(nameof(SelectedPermissionModeChoice));

    /// <summary>The session id generated by the most recent successful <see cref="Confirm"/>, or
    /// null before the first confirm (or after one that failed).</summary>
    public Guid? LastGeneratedSessionId { get; private set; }

    /// <summary>The launch spec built by the most recent successful <see cref="Confirm"/>.</summary>
    public PtyLaunchSpec? LastLaunchSpec { get; private set; }

    /// <summary>
    /// The live session started by the most recent successful <see cref="Confirm"/>. This
    /// ViewModel never disposes a session it started - ownership passes to whoever reads this
    /// (the dialog's code-behind / eventually <c>PtyRegistry</c>, Phase 3) exactly once
    /// <see cref="RequestClose"/> fires with <c>true</c>.
    /// </summary>
    public PtySession? LastStartedSession { get; private set; }

    /// <summary>
    /// Raised once <see cref="Confirm"/> succeeds or <see cref="Cancel"/> runs, telling the dialog's
    /// code-behind to close itself. The bool is "confirmed" (true, <see cref="LastStartedSession"/>
    /// is set) vs "cancelled" (false). Not raised if <see cref="Confirm"/> fails - the dialog stays
    /// open with <see cref="ErrorMessage"/> set so the user can fix the input and retry.
    /// </summary>
    public event EventHandler<bool>? RequestClose;

    /// <summary>
    /// Builds the argument array for a launch with session id <paramref name="sessionId"/>:
    /// <c>--session-id &lt;guid&gt; --name &lt;display&gt;</c> first (locked-in decision 3's exact
    /// shape), then <c>--model</c>/<c>--effort</c> if selected, then the combo-driven common flags
    /// (<see cref="CommonCliFlags"/>), then the tokenized extra-args tail
    /// (via <see cref="ExtraArgsParser"/> - never a naive whitespace split, so a token the user
    /// deliberately quoted to contain a space is not re-split). Pure and side-effect-free, so it is
    /// unit-testable without launching anything and without generating a GUID itself.
    /// </summary>
    public string[] BuildArguments(Guid sessionId)
    {
        var arguments = new List<string>
        {
            "--session-id",
            sessionId.ToString(),
            "--name",
            DisplayName ?? string.Empty,
        };

        if (!string.IsNullOrWhiteSpace(SelectedModelFamily))
        {
            arguments.Add("--model");
            arguments.Add(SelectedModelFamily);
        }

        if (!string.IsNullOrWhiteSpace(SelectedEffortLevel) && EffortSupported)
        {
            arguments.Add("--effort");
            arguments.Add(SelectedEffortLevel);
        }

        arguments.AddRange(CommonCliFlags.BuildArguments(SelectedPermissionMode));
        arguments.AddRange(ExtraArgsParser.Parse(ExtraArgsText));
        return arguments.ToArray();
    }

    /// <summary>
    /// Generates a fresh GUID, builds the argv array, builds a launch spec from it, and starts a
    /// session. On success, sets <see cref="LastGeneratedSessionId"/>/<see cref="LastLaunchSpec"/>/
    /// <see cref="LastStartedSession"/> and raises <see cref="RequestClose"/>(true). On failure,
    /// sets <see cref="ErrorMessage"/> and leaves the dialog open - never throws out of this method.
    /// </summary>
    [RelayCommand]
    private void Confirm()
    {
        ErrorMessage = null;
        try
        {
            // A blank field is never passed through as null: PtySession/ConPtySession treat a null
            // working directory as "inherit the parent process's own current directory" - which for
            // Accel.exe is its build output folder, not a real project and not anything the user has
            // ever seen or trusted. Claude Code's first-run trust prompt then blocks the session until
            // someone notices and answers it inside the terminal, which made a freshly created
            // session look like it had simply never started (reported bug). MainWindow already
            // defaults this field to panel A's selection or the first configured root before the
            // dialog ever opens; this is the last-resort fallback for the case where neither exists
            // (no roots configured at all) and the user confirmed without typing/browsing to one.
            var workingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : WorkingDirectory;
            if (!Directory.Exists(workingDirectory))
            {
                ErrorMessage = $"Working directory '{workingDirectory}' does not exist.";
                return;
            }

            var sessionId = _guidFactory();
            var arguments = BuildArguments(sessionId);
            var spec = _specBuilder(arguments, workingDirectory);
            var session = _sessionStarter(spec);

            LastGeneratedSessionId = sessionId;
            LastLaunchSpec = spec;
            LastStartedSession = session;
            RequestClose?.Invoke(this, true);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(this, false);

    /// <summary>Lets the user override the pre-filled/panel-A-derived working directory, reusing the
    /// same folder picker panel A's "Add root…" already uses rather than a second dialog type.</summary>
    [RelayCommand]
    private void BrowseWorkingDirectory()
    {
        string? folder = _folderPicker.PickFolder("Select a working directory for this session");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            WorkingDirectory = folder;
        }
    }

    private static PtyLaunchSpec DefaultSpecBuilder(IReadOnlyList<string> arguments, string? workingDirectory) =>
        PtySession.CreateClaudeSpec(arguments, workingDirectory);
}
