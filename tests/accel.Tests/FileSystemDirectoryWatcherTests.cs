namespace Accel.Tests;

using System;
using System.IO;
using System.Threading;
using Accel.App.Services;
using Xunit;

/// <summary>
/// Unit tests for <see cref="FileSystemDirectoryWatcher"/>. Two halves, deliberately kept apart:
/// the targeting/filtering rules are asserted with no filesystem events at all (a real directory
/// exists, but nothing changes inside it), and exactly one test provokes a genuine
/// <see cref="FileSystemWatcher"/> event to prove the plumbing is connected - with the debounce
/// window driven by <see cref="FakeDebounceTimer"/> rather than the clock, so even that test never
/// sleeps for a fixed 250 ms.
///
/// <para>No negative "wait and assert nothing arrived" test lives here: the only honest way to write
/// one is a fixed sleep long enough to be slow and still short enough to pass for the wrong reason.
/// The filters it would cover (<c>.git</c> and build output) are asserted directly instead, via
/// <see cref="FileSystemDirectoryWatcher.IsIgnoredPath"/>.</para>
/// </summary>
public sealed class FileSystemDirectoryWatcherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "accel-dir-watcher-tests-" + Guid.NewGuid().ToString("N"));

    public FileSystemDirectoryWatcherTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup only.
        }
    }

    private static (FileSystemDirectoryWatcher Watcher, FakeDebounceTimer Timer, RecordingUiThreadDispatcher Dispatcher) Build(
        bool includeContentChanges = true,
        bool ignoreGitInternals = false)
    {
        var timer = new FakeDebounceTimer();
        var dispatcher = new RecordingUiThreadDispatcher();
        return (new FileSystemDirectoryWatcher(timer: timer, dispatcher: dispatcher,
            includeContentChanges: includeContentChanges, ignoreGitInternals: ignoreGitInternals), timer, dispatcher);
    }

    [Fact]
    public void Watch_ExistingDirectory_ReportsIt()
    {
        var (watcher, _, _) = Build();
        using (watcher)
        {
            watcher.Watch(_root);

            Assert.Equal(_root, watcher.WatchedPath);
        }
    }

    [Fact]
    public void Watch_NullOrMissingDirectory_WatchesNothing()
    {
        var (watcher, _, _) = Build();
        using (watcher)
        {
            watcher.Watch(null);
            Assert.Null(watcher.WatchedPath);

            watcher.Watch(Path.Combine(_root, "does-not-exist"));
            Assert.Null(watcher.WatchedPath);
        }
    }

    [Fact]
    public void Watch_ThenNull_StopsWatching()
    {
        var (watcher, _, _) = Build();
        using (watcher)
        {
            watcher.Watch(_root);
            watcher.Watch(null);

            Assert.Null(watcher.WatchedPath);
        }
    }

    [Fact]
    public void Watch_SamePathTwice_StaysWatchingIt()
    {
        var (watcher, _, _) = Build();
        using (watcher)
        {
            watcher.Watch(_root);
            watcher.Watch(_root);

            Assert.Equal(_root, watcher.WatchedPath);
        }
    }

    [Fact]
    public void Watch_ReTargeted_ReportsTheNewPath()
    {
        string other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);

        var (watcher, _, _) = Build();
        using (watcher)
        {
            watcher.Watch(_root);
            watcher.Watch(other);

            Assert.Equal(other, watcher.WatchedPath);
        }
    }

    [Fact]
    public void Dispose_StopsWatchingAndDisposesTheTimer()
    {
        var (watcher, timer, _) = Build();
        watcher.Watch(_root);

        watcher.Dispose();

        Assert.Null(watcher.WatchedPath);
        Assert.True(timer.Disposed);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var (watcher, _, _) = Build();
        watcher.Dispose();
        watcher.Dispose();
    }

    [Fact]
    public void Watch_AfterDispose_IsANoOp()
    {
        var (watcher, _, _) = Build();
        watcher.Dispose();

        watcher.Watch(_root);

        Assert.Null(watcher.WatchedPath);
    }

    [Theory]
    // Repository internals, at the root and nested, either separator - only when asked for.
    [InlineData(@"C:\repo\.git\index", true, true)]
    [InlineData(@"C:\repo\.git\refs\heads\main", true, true)]
    [InlineData("C:/repo/.git/HEAD", true, true)]
    [InlineData(@"C:\repo\.git", true, true)]
    [InlineData(@"C:\repo\.git\index", false, false)]
    // Build output and tool caches: dropped either way, since a build under one of these is the
    // single biggest source of pointless refreshes.
    [InlineData(@"C:\repo\bin\Debug\accel.dll", false, true)]
    [InlineData(@"C:\repo\src\obj\project.assets.json", false, true)]
    [InlineData(@"C:\repo\web\node_modules\left-pad\index.js", false, true)]
    [InlineData(@"C:\repo\.vs\slnx.sqlite", false, true)]
    // Whole-segment matching only: every one of these is a real name a substring check would swallow.
    [InlineData(@"C:\repo\.github\workflows\ci.yml", true, false)]
    [InlineData(@"C:\repo\.gitignore", true, false)]
    [InlineData(@"C:\repo\src\foo.gitattributes", true, false)]
    [InlineData(@"C:\repo\not.git.txt", true, false)]
    [InlineData(@"C:\repo\binaries\tool.exe", true, false)]
    [InlineData(@"C:\repo\src\objects.cs", true, false)]
    [InlineData(@"C:\repo\src\Program.cs", true, false)]
    public void IsIgnoredPath_MatchesWholeSegmentsBelowTheRootOnly(string fullPath, bool ignoreGitInternals, bool expected) =>
        Assert.Equal(expected, FileSystemDirectoryWatcher.IsIgnoredPath(@"C:\repo", fullPath, ignoreGitInternals));

    /// <summary>
    /// The watched root's own path is not the user's content: a project that genuinely lives under a
    /// folder called <c>target</c> (or <c>bin</c>, or <c>node_modules</c>) must not have every single
    /// one of its events dropped.
    /// </summary>
    [Theory]
    [InlineData(@"C:\work\target\app", @"C:\work\target\app\src\main.rs", false)]
    [InlineData(@"C:\work\target\app", @"C:\work\target\app\target\debug\app.exe", true)]
    [InlineData(@"C:\work\bin", @"C:\work\bin\notes.txt", false)]
    public void IsIgnoredPath_IgnoresOnlyBelowTheWatchedRoot(string root, string fullPath, bool expected) =>
        Assert.Equal(expected, FileSystemDirectoryWatcher.IsIgnoredPath(root, fullPath, ignoreGitInternals: true));

    /// <summary>
    /// The one test that uses a real <see cref="FileSystemWatcher"/>: proves a created file reaches
    /// the coalescer (the timer gets restarted) and that <see cref="IDirectoryWatcher.Changed"/> then
    /// fires when - and only when - the debounce window elapses. The wait is a bounded poll rather
    /// than a fixed sleep, so it costs whatever the OS actually takes and fails loudly if the event
    /// never arrives.
    /// </summary>
    [Fact]
    public void CreatedFile_RestartsTheDebounceWindow_AndChangedFiresWhenItElapses()
    {
        var (watcher, timer, _) = Build();
        using (watcher)
        {
            int changed = 0;
            watcher.Changed += () => changed++;
            watcher.Watch(_root);

            File.WriteAllText(Path.Combine(_root, "appeared.txt"), "hello");

            Assert.True(WaitUntil(() => timer.Restarts > 0), "no filesystem event reached the debounce coalescer");

            // Nothing yet: a signal only arms the window, it never publishes on its own.
            Assert.Equal(0, changed);

            timer.Fire();
            Assert.Equal(1, changed);

            // And the window is one-shot - a second elapse with nothing pending publishes nothing.
            timer.Fire();
            Assert.Equal(1, changed);
        }
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        for (int waited = 0; waited < timeoutMs; waited += 25)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return condition();
    }
}
