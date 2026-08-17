namespace Accel.App.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using Accel.Cli;
using Accel.Metrics;

/// <summary>Whether an <see cref="AgentGraphNodeViewModel"/> is the focused session (always exactly
/// one per graph, rendered as the left-column parent card) or one of its live sub-agents (rendered
/// in the child columns to its right) - design doc §7.7.</summary>
public enum AgentGraphNodeRole
{
    Parent,
    Child,
}

/// <summary>
/// One card in panel E's graph: a projection of a single <see cref="MonitorSessionNode"/> (role
/// <see cref="AgentGraphNodeRole.Parent"/>) or <see cref="MonitorAgentNode"/> (role
/// <see cref="AgentGraphNodeRole.Child"/>), built by <see cref="AgentGraphViewModel"/>, never by
/// <c>AgentGraphControl</c> itself (design doc §7.7).
/// </summary>
public sealed partial class AgentGraphNodeViewModel : ObservableObject
{
    private readonly bool _consumedTokensIsContextOnly;
    private readonly string _parentName;

    public AgentGraphNodeViewModel(
        string key,
        AgentGraphNodeRole role,
        MonitorNodeState state,
        MonitorRowColumns columns,
        long? durationMs,
        long? consumedTokens,
        bool isFocused,
        bool consumedTokensIsContextOnly,
        string parentName = "")
    {
        Key = key ?? string.Empty;
        Role = role;
        State = state;
        Columns = columns ?? MonitorRowColumns.Empty;
        DurationMs = durationMs;
        ConsumedTokens = consumedTokens;
        _consumedTokensIsContextOnly = consumedTokensIsContextOnly;
        _parentName = parentName ?? string.Empty;
        _isFocused = isFocused;

        ModelBadge = ModelBadgeTable.Resolve(Columns.Model);
        EffortLevel = EffortBarLevel.Resolve(Columns.Effort);
        DisplayName = ResolveDisplayName();
        DetailText = $"{Columns.Duration} · {Columns.Tokens} · {Columns.Context}";
        TooltipText = BuildTooltipText();

        VisualState = SessionVisualStateResolver.Resolve(IsRunning, IsFocused);
        AutomationDescription = BuildAutomationDescription();
    }

    /// <summary>Stable id: the session id (parent) or agent id (child) - the same keys panel A uses.</summary>
    public string Key { get; }

    public AgentGraphNodeRole Role { get; }

    public MonitorNodeState State { get; }

    public MonitorRowColumns Columns { get; }

    /// <summary>Raw, unformatted duration - null for a row whose start time couldn't be resolved,
    /// distinct from a real <c>0</c>. Kept for a future edge/bar-scaling use (design doc §6.6's
    /// stated purpose), not currently rendered directly.</summary>
    public long? DurationMs { get; }

    /// <summary>See <see cref="DurationMs"/>.</summary>
    public long? ConsumedTokens { get; }

    public bool IsRunning => State == MonitorNodeState.Live;

    [ObservableProperty]
    private bool _isFocused;

    partial void OnIsFocusedChanged(bool value)
    {
        VisualState = SessionVisualStateResolver.Resolve(IsRunning, value);
        AutomationDescription = BuildAutomationDescription();
    }

    [ObservableProperty]
    private double _x;

    [ObservableProperty]
    private double _y;

    public SessionVisualState VisualState
    {
        get => _visualState;
        private set => SetProperty(ref _visualState, value);
    }

    private SessionVisualState _visualState;

    public ModelBadge ModelBadge { get; }

    public int EffortLevel { get; }

    /// <summary>The row's name, falling back to its type then its id - never blank.</summary>
    public string DisplayName { get; }

    /// <summary>"7m 04s · 148.2K · 12.3% of 1M (assumed)" - the section-6 formatted strings,
    /// unmodified, composed here (not in XAML) so the separator/ordering is one testable string and
    /// the em-dash "no data" case renders as "— · — · " rather than a blank strip.</summary>
    public string DetailText { get; }

    /// <summary>A session node's tooltip ends with the context-only caveat (design doc §6.4/6.7/7.4);
    /// an agent node's does not, since an agent's <see cref="ConsumedTokens"/> is a genuine total.</summary>
    public string TooltipText { get; }

    public string AutomationDescription
    {
        get => _automationDescription;
        private set => SetProperty(ref _automationDescription, value);
    }

    private string _automationDescription = string.Empty;

    private string ResolveDisplayName()
    {
        if (!string.IsNullOrEmpty(Columns.Name))
        {
            return Columns.Name;
        }

        return !string.IsNullOrEmpty(Columns.Type) ? Columns.Type : Columns.Id;
    }

    private string BuildTooltipText()
    {
        string kindLabel = Role == AgentGraphNodeRole.Parent ? "Session" : "Sub-agent";
        string model = string.IsNullOrEmpty(Columns.Model) ? "unknown-model" : Columns.Model;
        string effort = string.IsNullOrEmpty(Columns.Effort) ? "?" : Columns.Effort;

        string tooltip = $"{kindLabel} {DisplayName} — {model} — effort={effort} — {DetailText}";

        // Design doc §6.4/6.7/7.4: a session's ConsumedTokens is context-window usage only (input +
        // cache, no output tokens) - never comparable to an agent's genuine total. The caveat lives
        // in the tooltip, never in the 52px card itself, and never on an agent node.
        if (Role == AgentGraphNodeRole.Parent && _consumedTokensIsContextOnly)
        {
            tooltip += " (session tokens are context-window usage: input + cache, no output tokens)";
        }

        return tooltip;
    }

    private string BuildAutomationDescription()
    {
        string kindLabel = Role == AgentGraphNodeRole.Parent ? "Session" : "Sub-agent";
        string model = string.IsNullOrEmpty(Columns.Model) ? "unknown-model" : Columns.Model;
        string effort = string.IsNullOrEmpty(Columns.Effort) ? "?" : Columns.Effort;
        string context = string.IsNullOrEmpty(Columns.Context) ? "unknown" : Columns.Context;

        return Role == AgentGraphNodeRole.Parent
            ? $"Session: {DisplayName}. {VisualState.AutomationName}. Model {model}, effort {effort}, context {context}, running {Columns.Duration}, {Columns.Tokens} tokens."
            : $"Sub-agent: {DisplayName}, child of session {_parentName}. {VisualState.AutomationName}. Model {model}, effort {effort}, context {context}, running {Columns.Duration}, {Columns.Tokens} tokens.";
    }
}
