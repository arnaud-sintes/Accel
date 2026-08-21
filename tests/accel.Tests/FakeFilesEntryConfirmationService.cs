namespace Accel.Tests;

using Accel.App.Services;

/// <summary>Fake <see cref="IFilesEntryConfirmationService"/>: returns a fixed yes/no instead of
/// showing a real dialog - shared between <see cref="FilesPanelViewModelTests"/> (delete/permanent
/// delete confirmations) and <see cref="GitPanelViewModelTests"/> (discard confirmations).</summary>
internal sealed class FakeFilesEntryConfirmationService : IFilesEntryConfirmationService
{
    public bool ConfirmDeleteResult { get; set; } = true;
    public bool ConfirmPermanentDeleteResult { get; set; } = true;
    public bool ConfirmDiscardChangesResult { get; set; } = true;
    public bool ConfirmMarkResolvedWithMarkersResult { get; set; } = true;
    public bool ConfirmAbortOperationResult { get; set; } = true;

    /// <summary>How many marker regions the last <see cref="ConfirmMarkResolvedWithMarkers"/> call was
    /// told about - lets a test assert the prompt happened at all, and with the right count, rather
    /// than only observing its effect.</summary>
    public int? LastMarkerRegionCount { get; private set; }

    public bool ConfirmDelete(string name, bool isDirectory) => ConfirmDeleteResult;
    public bool ConfirmPermanentDelete(string name, bool isDirectory) => ConfirmPermanentDeleteResult;
    public bool ConfirmDiscardChanges(string path, bool isStaged) => ConfirmDiscardChangesResult;

    public bool ConfirmMarkResolvedWithMarkers(string path, int markerRegionCount)
    {
        LastMarkerRegionCount = markerRegionCount;
        return ConfirmMarkResolvedWithMarkersResult;
    }

    public bool ConfirmAbortOperation(string operationName) => ConfirmAbortOperationResult;
}
