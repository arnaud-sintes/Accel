namespace Accel.Tests;

using System;
using System.Collections.Generic;
using Accel.App.Services;

/// <summary>
/// Stands in for <see cref="FileSystemDirectoryWatcher"/> when testing panel B's two ViewModels:
/// records what they asked to watch and lets a test raise <see cref="Changed"/> at an exact moment,
/// so "an agent changed something on disk" is a single deterministic call rather than a real
/// <see cref="System.IO.FileSystemWatcher"/> plus a wall-clock debounce window. Same role
/// <see cref="FakeDebounceTimer"/> plays for the telemetry feed.
/// </summary>
internal sealed class FakeDirectoryWatcher : IDirectoryWatcher
{
    public event Action? Changed;

    public string? WatchedPath { get; private set; }

    /// <summary>Every <see cref="Watch"/> argument in order, so a test can assert not just where the
    /// watcher ended up but that it was re-targeted (or deliberately not re-targeted) along the way.</summary>
    public List<string?> WatchCalls { get; } = new();

    public bool Disposed { get; private set; }

    public void Watch(string? directoryPath)
    {
        WatchCalls.Add(directoryPath);
        WatchedPath = directoryPath;
    }

    /// <summary>Simulates the debounce window elapsing with at least one filesystem event pending -
    /// i.e. exactly what the production watcher does once something changed under
    /// <see cref="WatchedPath"/>.</summary>
    public void RaiseChanged() => Changed?.Invoke();

    public void Dispose() => Disposed = true;
}
