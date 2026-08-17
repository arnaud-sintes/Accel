namespace Accel.App.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using Accel.App.Services;

/// <summary>
/// P3-T3: a minimal read-only stub for panels B (files) and E (agent graph) - both still render their
/// P1-T1b placeholder text, this only proves the wiring exists ahead of their real content (Phases 5/6).
/// Subscribes to <see cref="ISessionSelectionService"/> and exposes nothing but the focused session id, so
/// each panel's placeholder can show "which session am I about to reflect" instead of a static string with
/// no connection to selection at all.
///
/// <para>Deliberately one class shared by both panels rather than two near-identical ones: neither has any
/// panel-specific behavior yet, and Phases 5/6 replace this outright rather than extend it.</para>
/// </summary>
public sealed partial class FocusedSessionStubViewModel : ObservableObject, System.IDisposable
{
    private readonly ISessionSelectionService _selection;

    public FocusedSessionStubViewModel(ISessionSelectionService selection)
    {
        _selection = selection;
        _selection.Subscribe(this, OnFocusedSessionChanged);
        UpdateText();
    }

    [ObservableProperty]
    private string _statusText = string.Empty;

    private void OnFocusedSessionChanged(FocusedSessionChangedMessage message) => UpdateText();

    private void UpdateText() =>
        StatusText = _selection.FocusedSessionId is { } id
            ? $"Focused session: {id}"
            : "No session focused";

    public void Dispose() => _selection.Unsubscribe(this);
}
