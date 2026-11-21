using System.Diagnostics;

namespace VRChatPhotoSync.Tests;

public sealed class AppRuntimeTests : IDisposable
{
    private readonly string _dir;
    private readonly string _photos;
    private readonly string _config;
    private readonly UploadStateStore _store;
    private readonly FakeUploadClient _client = new();
    private readonly AppConfig _settings = new() { VrcxWaitSeconds = 0 };
    private readonly List<string> _log = [];
    private readonly List<AppRuntime> _runtimes = [];
    private DateTime _now = new(2026, 11, 21, 20, 0, 0, DateTimeKind.Utc);

    public AppRuntimeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "PhotoSyncRuntime_" + Guid.NewGuid().ToString("N"));
        _photos = Path.Combine(_dir, "photos");
        _config = Path.Combine(_dir, "config");
        Directory.CreateDirectory(_photos);
        Directory.CreateDirectory(_config);
        _store = new UploadStateStore(_config);
    }

    public void Dispose()
    {
        foreach (var runtime in _runtimes)
            runtime.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private AppRuntime Create(bool fakeTime = true)
    {
        var runtime = new AppRuntime(
            _store, _photos, () => _settings, () => _client,
            utcNow: fakeTime ? () => _now : null,
            log: l => { lock (_log) _log.Add(l); },
            watcherDebounce: TimeSpan.FromMilliseconds(100));
        _runtimes.Add(runtime);
        return runtime;
    }

    private static void WaitFor(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException($"Timed out waiting for {what}.");
            Thread.Sleep(25);
        }
    }

    private string WritePhoto(string month, string time, int seed)
    {
        var folder = Path.Combine(_photos, month);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"VRChat_{month}-21_{time}_64x48.png");
        var temp = Path.Combine(_dir, $"tmp-{seed}.png");
        PngTestFiles.Write(temp, 64, 48, seed);
        File.Move(temp, path);
        return path;
    }

    // ---- Session continuation ----

    [Fact]
    public void SessionContinues_AfterSimulatedKill_WhenTheHeartbeatIsRecent()
    {
        var start = _now;
        var first = Create();
        Assert.False(first.SessionContinued);
        lock (first.Worker.StateLock)
        {
            first.State.Session.Uploaded = 3;
            first.State.Session.Duplicates = 1;
            first.State.Session.PrintsSkipped = 2;
            first.State.Session.LastFile = @"2026-11\VRChat_2026-11-21_21-03-08.102_3840x2160.png";
        }
        _now += TimeSpan.FromSeconds(30);
        first.Heartbeat();

        // Killed: no StopAsync, nothing more is saved. VRCX relaunches straight away (here 90 s later).
        _now += TimeSpan.FromSeconds(90);
        var second = Create();

        Assert.True(second.SessionContinued);
        Assert.Equal(start, second.State.Session.StartUtc);
        Assert.Equal(3, second.State.Session.Uploaded);
        Assert.Equal(1, second.State.Session.Duplicates);
        Assert.Equal(2, second.State.Session.PrintsSkipped);
        Assert.Equal(@"2026-11\VRChat_2026-11-21_21-03-08.102_3840x2160.png", second.State.Session.LastFile);
        // The relaunch records its own heartbeat at once, so another quick kill still continues.
        Assert.Equal(_now, _store.Load().HeartbeatUtc);
    }

    [Fact]
    public void NewSession_AfterSimulatedKill_WhenTheHeartbeatIsOld()
    {
        var first = Create();
        lock (first.Worker.StateLock)
            first.State.Session.Uploaded = 5;
        first.Heartbeat();

        _now += TimeSpan.FromMinutes(10);
        var second = Create();

        Assert.False(second.SessionContinued);
        Assert.Equal(_now, second.State.Session.StartUtc);
        Assert.Equal(0, second.State.Session.Uploaded);
        Assert.Null(second.State.Session.LastFile);
    }

    [Fact]
    public void Heartbeat_IsSavedToDisk()
    {
        var runtime = Create();
        _now += TimeSpan.FromSeconds(30);
        runtime.Heartbeat();
        Assert.Equal(_now, _store.Load().HeartbeatUtc);
    }

    [Fact]
    public void SessionContinues_AfterKillDuringRealUploads()
    {
        var first = Create(fakeTime: false);
        first.Start();
        var path = WritePhoto("2026-11", "21-03-08.102", seed: 1);
        WaitFor(() => _store.Load().Session.Uploaded == 1, "the first upload to be saved");
        Assert.Single(_store.Load().Entries);

        // A hard kill: the process just goes away. Here the in-process objects are torn down without StopAsync.
        first.Dispose();

        var second = Create(fakeTime: false);
        Assert.True(second.SessionContinued);
        Assert.Equal(1, second.State.Session.Uploaded);

        // The relaunch keeps counting in the same session, and doesn't upload the first photo again.
        second.Start();
        var next = WritePhoto("2026-11", "21-05-58.427", seed: 2);
        WaitFor(() => _store.Load().Session.Uploaded == 2, "the second upload to be saved");
        Assert.Equal(2, _client.Uploads.Count);
        Assert.Equal([Path.GetFileName(path), Path.GetFileName(next)], _client.UploadedNames);
    }

    // ---- Graceful stop ----

    [Fact]
    public async Task StopAsync_LetsTheCurrentUploadFinish()
    {
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        _client.OnUpload = _ =>
        {
            entered.Set();
            gate.Wait(TimeSpan.FromSeconds(30));
        };
        var runtime = Create(fakeTime: false);
        runtime.Start();
        WritePhoto("2026-11", "21-03-08.102", seed: 1);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(20)), "the upload didn't start");

        var stop = runtime.StopAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(300);
        Assert.False(stop.IsCompleted);
        gate.Set();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(runtime.WorkerTask.IsCompleted);
        var saved = _store.Load();
        Assert.Equal(1, saved.Session.Uploaded);
        Assert.Single(saved.Entries);
    }

    [Fact]
    public async Task StopAsync_GivesUpAfterTheGracePeriod_AndSavesTheState()
    {
        using var entered = new ManualResetEventSlim();
        using var gate = new ManualResetEventSlim();
        _client.OnUpload = _ =>
        {
            entered.Set();
            gate.Wait(TimeSpan.FromSeconds(30));
        };
        try
        {
            var runtime = Create(fakeTime: false);
            runtime.Start();
            WritePhoto("2026-11", "21-03-08.102", seed: 1);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(20)), "the upload didn't start");

            var watch = Stopwatch.StartNew();
            var before = DateTime.UtcNow;
            await runtime.StopAsync(TimeSpan.FromMilliseconds(300)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));

            var saved = _store.Load();
            Assert.True(saved.HeartbeatUtc >= before.AddSeconds(-1));
            Assert.Empty(saved.Entries); // the stuck upload is redone at the next start
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task StopAsync_StopsIntake()
    {
        var runtime = Create(fakeTime: false);
        runtime.Start();
        WritePhoto("2026-11", "21-03-08.102", seed: 1);
        WaitFor(() => _client.Uploads.Count == 1, "the first upload");

        await runtime.StopAsync(TimeSpan.FromSeconds(5));
        await runtime.StopAsync(); // a second call does nothing
        Assert.True(runtime.WorkerTask.IsCompleted);

        WritePhoto("2026-11", "21-05-58.427", seed: 2);
        await Task.Delay(800);
        Assert.Single(_client.Uploads);
        Assert.Equal(0, runtime.Queue.Count);
    }

    [Fact]
    public void Start_BindsTheStateToTheFolder_BeforeTheWorkerRuns()
    {
        var runtime = Create(fakeTime: false);
        runtime.Start();
        WaitFor(() => _store.Load().PictureFolder is not null, "the state to be bound");
        Assert.Equal(Path.GetFullPath(_photos), _store.Load().PictureFolder, ignoreCase: true);
        Assert.NotNull(runtime.Watcher);
    }

    [Fact]
    public void SwitchFolder_WatchesTheNewFolder()
    {
        var runtime = Create(fakeTime: false);
        runtime.Start();
        var other = Path.Combine(_dir, "other");
        Directory.CreateDirectory(other);
        FolderWatcher? changed = null;
        runtime.WatcherChanged += w => changed = w;

        runtime.SwitchFolder(other);

        Assert.NotNull(changed);
        Assert.Same(changed, runtime.Watcher);
        Assert.Equal(Path.GetFullPath(other), Path.GetFullPath(runtime.Watcher!.Root), ignoreCase: true);
        Assert.Equal(other, runtime.PictureFolder);
    }
}
