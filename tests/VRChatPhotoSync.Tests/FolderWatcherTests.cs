using System.Collections.Concurrent;

namespace VRChatPhotoSync.Tests;

public sealed class FolderWatcherTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "PhotoSyncTests", Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly ConcurrentQueue<string> _appeared = new();
    private readonly ConcurrentQueue<(string Old, string New)> _renamed = new();
    private readonly ConcurrentQueue<string> _log = new();
    private int _resyncs;

    public FolderWatcherTests()
    {
        Directory.CreateDirectory(_temp);
        _root = Path.Combine(_temp, "VRChat");
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private FolderWatcher CreateWatcher(TimeSpan? debounce = null)
    {
        var watcher = new FolderWatcher(_root, debounce ?? Debounce, Poll, _log.Enqueue);
        watcher.FileAppeared += _appeared.Enqueue;
        watcher.FileRenamed += (o, n) => _renamed.Enqueue((o, n));
        watcher.ResyncNeeded += (_, _) => Interlocked.Increment(ref _resyncs);
        return watcher;
    }

    private static void WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Timed out waiting for {what}.");
            Thread.Sleep(20);
        }
    }

    /// <summary>Waits long enough for any further debounced events to have fired.</summary>
    private static void Settle() => Thread.Sleep(Debounce * 4);

    [Fact]
    public void Watcher_IsRecursive_With64KBufferAndExpectedFilter()
    {
        Directory.CreateDirectory(_root);
        using var watcher = CreateWatcher();
        watcher.Start();

        Assert.Equal(FolderWatchState.Watching, watcher.State);
        var fsw = watcher.Watcher;
        Assert.NotNull(fsw);
        Assert.True(fsw.IncludeSubdirectories);
        Assert.Equal(64 * 1024, fsw.InternalBufferSize);
        Assert.Equal(NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size, fsw.NotifyFilter);
        Assert.True(fsw.EnableRaisingEvents);
        Assert.Equal(0, _resyncs);
    }

    [Fact]
    public void NewFileInRoot_RaisesAppearedOnce_AfterSeveralWrites()
    {
        Directory.CreateDirectory(_root);
        using var watcher = CreateWatcher();
        watcher.Start();

        var path = Path.Combine(_root, "VRChat_2024-11-23_19-17-27.652_1920x1080.png");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            for (int i = 0; i < 5; i++)
            {
                stream.Write(new byte[4096]);
                stream.Flush(flushToDisk: true);
                Thread.Sleep(20);
            }
        }

        WaitFor(() => !_appeared.IsEmpty, "FileAppeared");
        Settle();
        Assert.Equal([path], _appeared.ToArray());
    }

    [Fact]
    public void NewFileInMonthFolder_IsReported()
    {
        Directory.CreateDirectory(_root);
        using var watcher = CreateWatcher();
        watcher.Start();

        var month = Path.Combine(_root, "2024-11");
        Directory.CreateDirectory(month);
        var path = Path.Combine(month, "VRChat_2024-11-23_19-17-27.652_1920x1080.png");
        File.WriteAllBytes(path, new byte[100]);

        WaitFor(() => _appeared.Contains(path, StringComparer.OrdinalIgnoreCase), "FileAppeared in month folder");
        Settle();
        // Only the file: the new folder and the folder's own "changed" events are not reported.
        Assert.DoesNotContain(month, _appeared, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChangeToExistingFile_RaisesAppeared()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "existing.png");
        File.WriteAllBytes(path, new byte[100]);

        using var watcher = CreateWatcher();
        watcher.Start();
        File.AppendAllText(path, "more");

        WaitFor(() => _appeared.Contains(path, StringComparer.OrdinalIgnoreCase), "FileAppeared after change");
    }

    [Fact]
    public void RenameSoonAfterCreate_ReportsRenameAndOnlyTheNewName()
    {
        Directory.CreateDirectory(_root);
        // A long debounce makes sure the rename happens while the create is still pending.
        using var watcher = CreateWatcher(TimeSpan.FromMilliseconds(600));
        watcher.Start();

        var original = Path.Combine(_root, "VRChat_2026-11-21_21-41-26.711_3840x2160.png");
        var renamed = Path.Combine(_root, "VRChat_2026-11-21_21-41-26.711_3840x2160_wrld_12345678-90ab-4cde-8f01-234567890abc.png");
        File.WriteAllBytes(original, new byte[100]);
        Thread.Sleep(100);
        File.Move(original, renamed);

        WaitFor(() => !_renamed.IsEmpty, "FileRenamed");
        WaitFor(() => !_appeared.IsEmpty, "FileAppeared for the new name");
        Thread.Sleep(1500);

        Assert.Equal([(original, renamed)], _renamed.ToArray());
        Assert.Equal([renamed], _appeared.ToArray());
    }

    [Fact]
    public void DeletedBeforeQuietPeriodEnds_IsNotReported()
    {
        Directory.CreateDirectory(_root);
        using var watcher = CreateWatcher(TimeSpan.FromMilliseconds(500));
        watcher.Start();

        var path = Path.Combine(_root, "temp.png");
        File.WriteAllBytes(path, new byte[10]);
        File.Delete(path);

        Thread.Sleep(1500);
        Assert.Empty(_appeared);
    }

    [Fact]
    public void MissingFolder_WaitsWithoutCreatingIt_ThenStartsAndAsksForResync()
    {
        using var watcher = CreateWatcher();
        var states = new ConcurrentQueue<FolderWatchState>();
        watcher.StateChanged += (_, _) => states.Enqueue(watcher.State);
        watcher.Start();

        Assert.Equal(FolderWatchState.WaitingForFolder, watcher.State);
        Assert.Equal($"Waiting for {_root} to exist…", watcher.StatusText);
        Assert.Null(watcher.Watcher);

        // Several polls go by and the folder is still not created.
        Thread.Sleep(Poll * 5);
        Assert.False(Directory.Exists(_root));
        Assert.Equal(FolderWatchState.WaitingForFolder, watcher.State);
        Assert.Equal(0, _resyncs);

        Directory.CreateDirectory(_root);
        WaitFor(() => watcher.State == FolderWatchState.Watching, "Watching state");
        WaitFor(() => _resyncs == 1, "ResyncNeeded");
        Assert.Contains(FolderWatchState.Watching, states);

        var path = Path.Combine(_root, "VRChat_2024-11-23_19-17-27.652_1920x1080.png");
        File.WriteAllBytes(path, new byte[100]);
        WaitFor(() => _appeared.Contains(path, StringComparer.OrdinalIgnoreCase), "FileAppeared after the folder appeared");
    }

    [Fact]
    public void WatcherError_RecreatesWatcher_AndAsksForResync()
    {
        Directory.CreateDirectory(_root);
        using var watcher = CreateWatcher();
        watcher.Start();
        var before = watcher.Watcher;

        watcher.HandleError(new InternalBufferOverflowException("Too many changes at once."));

        Assert.Equal(1, _resyncs);
        Assert.Equal(FolderWatchState.Watching, watcher.State);
        Assert.NotNull(watcher.Watcher);
        Assert.NotSame(before, watcher.Watcher);
        Assert.Contains(_log, line => line.Contains("recreating", StringComparison.OrdinalIgnoreCase));

        var path = Path.Combine(_root, "after-error.png");
        File.WriteAllBytes(path, new byte[100]);
        WaitFor(() => _appeared.Contains(path, StringComparer.OrdinalIgnoreCase), "FileAppeared after recreating");
    }

    [Fact]
    public void FolderRemovedWhileWatching_GoesBackToWaiting_AndResumesWhenItReturns()
    {
        Directory.CreateDirectory(_root);
        using var watcher = CreateWatcher();
        watcher.Start();
        Assert.Equal(FolderWatchState.Watching, watcher.State);

        Directory.Delete(_root, recursive: true);
        WaitFor(() => watcher.State == FolderWatchState.WaitingForFolder, "WaitingForFolder after delete");
        Thread.Sleep(Poll * 3);
        Assert.False(Directory.Exists(_root));

        Directory.CreateDirectory(_root);
        WaitFor(() => watcher.State == FolderWatchState.Watching, "Watching after the folder returned");
        WaitFor(() => _resyncs >= 1, "ResyncNeeded after the folder returned");
    }

    [Fact]
    public void Stop_DropsPendingEvents()
    {
        Directory.CreateDirectory(_root);
        using var watcher = CreateWatcher(TimeSpan.FromMilliseconds(500));
        watcher.Start();

        File.WriteAllBytes(Path.Combine(_root, "a.png"), new byte[10]);
        Thread.Sleep(100);
        watcher.Stop();

        Assert.Equal(FolderWatchState.Stopped, watcher.State);
        Assert.Null(watcher.Watcher);
        Thread.Sleep(1000);
        Assert.Empty(_appeared);
    }

    [Fact]
    public void ThrowingHandler_DoesNotStopLaterEvents()
    {
        Directory.CreateDirectory(_root);
        using var watcher = CreateWatcher();
        watcher.FileAppeared += _ => throw new InvalidOperationException("boom");
        watcher.Start();

        var first = Path.Combine(_root, "first.png");
        File.WriteAllBytes(first, new byte[10]);
        WaitFor(() => _appeared.Contains(first, StringComparer.OrdinalIgnoreCase), "first FileAppeared");

        var second = Path.Combine(_root, "second.png");
        File.WriteAllBytes(second, new byte[10]);
        WaitFor(() => _appeared.Contains(second, StringComparer.OrdinalIgnoreCase), "second FileAppeared");
        Assert.Contains(_log, line => line.Contains("boom"));
    }
}

public sealed class DebouncerTests
{
    [Fact]
    public void RepeatedTriggers_FireOnceAfterQuietPeriod()
    {
        var fired = new ConcurrentQueue<string>();
        using var debouncer = new Debouncer<string>(TimeSpan.FromMilliseconds(150), fired.Enqueue);

        for (int i = 0; i < 6; i++)
        {
            debouncer.Trigger("a");
            Thread.Sleep(50);
        }
        Assert.Empty(fired); // still inside the quiet period after the last trigger
        Thread.Sleep(600);
        Assert.Equal(["a"], fired.ToArray());
        Assert.Equal(0, debouncer.PendingCount);
    }

    [Fact]
    public void Keys_AreIndependent_AndCancelWorks()
    {
        var fired = new ConcurrentQueue<string>();
        using var debouncer = new Debouncer<string>(TimeSpan.FromMilliseconds(100), fired.Enqueue, StringComparer.OrdinalIgnoreCase);

        debouncer.Trigger("a");
        debouncer.Trigger("B");
        debouncer.Trigger("b");
        debouncer.Trigger("c");
        Assert.True(debouncer.Cancel("C"));
        Assert.False(debouncer.Cancel("zzz"));
        Thread.Sleep(600);

        // "B" and "b" are one key; the callback gets the most recent spelling.
        Assert.Equal(["a", "b"], fired.Order(StringComparer.Ordinal).ToArray());
    }
}
