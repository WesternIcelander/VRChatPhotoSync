using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;

namespace VRChatPhotoSync.Tests;

/// <summary>An <see cref="IUploadClient"/> that records calls and answers from a script.</summary>
public sealed class FakeUploadClient : IUploadClient
{
    public sealed record Upload(string FileName, string Sha1, byte[] Content, DateTimeOffset CreatedAt, DateTimeOffset ModifiedAt, AssetVisibility Visibility);

    private readonly object _lock = new();
    private readonly Queue<UploadResult> _script = new();
    private int _nextId;

    public List<Upload> Uploads { get; } = [];
    public List<string> AlbumLookups { get; } = [];
    public List<(string AlbumId, string[] AssetIds)> AlbumAdds { get; } = [];

    /// <summary>Called before each upload answers (e.g. to block or observe).</summary>
    public Action<Upload>? OnUpload { get; set; }

    /// <summary>Queues the next answers; once the script is empty every upload is <see cref="UploadStatus.Created"/>.</summary>
    public FakeUploadClient Then(params UploadResult[] results)
    {
        lock (_lock)
        {
            foreach (var r in results)
                _script.Enqueue(r);
        }
        return this;
    }

    public List<string> UploadedNames
    {
        get { lock (_lock) return Uploads.Select(u => u.FileName).ToList(); }
    }

    public Task<UploadResult> UploadAsync(
        ReadOnlyMemory<byte> content, string fileName, string sha1, DateTimeOffset createdAt, DateTimeOffset modifiedAt,
        AssetVisibility visibility = AssetVisibility.Timeline, CancellationToken cancellationToken = default)
    {
        var upload = new Upload(fileName, sha1, content.ToArray(), createdAt, modifiedAt, visibility);
        UploadResult result;
        lock (_lock)
        {
            Uploads.Add(upload);
            result = _script.Count > 0 ? _script.Dequeue() : UploadResult.Created($"asset-{++_nextId}");
        }
        OnUpload?.Invoke(upload);
        return Task.FromResult(result);
    }

    public Task<ApiResult<string>> GetOrCreateAlbumAsync(string name, CancellationToken cancellationToken = default)
    {
        lock (_lock) AlbumLookups.Add(name);
        return Task.FromResult(ApiResult<string>.Ok("album-1"));
    }

    public Task<ApiResult<AlbumAddResult>> AddToAlbumAsync(string albumId, IReadOnlyCollection<string> assetIds, CancellationToken cancellationToken = default)
    {
        lock (_lock) AlbumAdds.Add((albumId, assetIds.ToArray()));
        return Task.FromResult(ApiResult<AlbumAddResult>.Ok(new AlbumAddResult(assetIds.ToArray(), [], [])));
    }
}

public sealed class UploadWorkerTests
{
    private const string Root = @"D:\Photos\VRChat";
    private const string Month = Root + @"\2026-11";
    private const string Photo1 = Month + @"\VRChat_2026-11-21_21-34-55.168_3840x2160.png";
    private const string Photo2 = Month + @"\VRChat_2026-11-21_21-36-10.001_3840x2160.png";
    private const string Photo3 = Month + @"\VRChat_2026-11-21_21-37-20.002_3840x2160.png";
    private const string LayerBase = Month + @"\VRChat_2026-11-21_21-35-02.420_3840x2160";
    private const string Combined = LayerBase + ".png";
    private const string Environment = LayerBase + "_Environment.png";
    private const string Print = Month + @"\VRChat_2026-11-04_01-48-06.885_2048x1440.png";

    private readonly FakeClock _clock = new();
    private readonly FakeProbe _probe;
    private readonly ConcurrentDictionary<string, byte[]> _contents = new(StringComparer.OrdinalIgnoreCase);
    private readonly UploadQueue _queue = new();
    private readonly UploadState _state = new() { PictureFolder = Root, LastScanUtc = DateTime.UtcNow };
    private readonly FakeUploadClient _client = new();
    private readonly List<UploadEvent> _events = [];
    private readonly List<WorkerStatus> _statuses = [];
    private readonly List<string> _log = [];
    private readonly HashSet<string> _prints = new(StringComparer.OrdinalIgnoreCase);
    private int _saves;
    private int _printChecks;
    private int _reads;

    private AppConfig _config = new() { VrcxWaitSeconds = 0 };
    private Func<string, bool>? _isPrint;
    private Action<string, int>? _onRead;
    private IUploadClient? _currentClient;

    public UploadWorkerTests()
    {
        _probe = new FakeProbe(_clock);
        _currentClient = _client;
    }

    private void AddFile(string path, int size = 1000, TimeSpan? age = null)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        _contents[path] = bytes;
        _probe.Add(path, size, age);
    }

    private void ChangeFile(string path, int size)
    {
        _contents[path] = RandomNumberGenerator.GetBytes(size);
        _probe.Write(path, size);
    }

    private UploadWorker CreateWorker(IClock? clock = null, IFileProbe? probe = null, FileReadiness? readiness = null)
    {
        clock ??= _clock;
        probe ??= _probe;
        var worker = new UploadWorker(
            _queue, _state,
            _ => Interlocked.Increment(ref _saves),
            () => _config,
            () => _currentClient,
            probe, clock,
            readiness: readiness,
            isPrint: path =>
            {
                Interlocked.Increment(ref _printChecks);
                return _isPrint?.Invoke(path) ?? _prints.Contains(path);
            },
            readFile: path =>
            {
                var n = Interlocked.Increment(ref _reads);
                var bytes = _contents[path];
                _onRead?.Invoke(path, n);
                return bytes;
            });
        worker.Uploaded += e => { lock (_events) _events.Add(e); };
        worker.Duplicate += e => { lock (_events) _events.Add(e); };
        worker.Skipped += e => { lock (_events) _events.Add(e); };
        worker.Failed += e => { lock (_events) _events.Add(e); };
        worker.StatusChanged += s => { lock (_statuses) _statuses.Add(s); };
        worker.Log += l => { lock (_log) _log.Add(l); };
        return worker;
    }

    /// <summary>Queues the paths, completes the queue and runs the worker to the end.</summary>
    private async Task<UploadWorker> RunAll(params string[] paths)
    {
        var worker = CreateWorker();
        foreach (var p in paths)
            Assert.True(_queue.Enqueue(p));
        _queue.Complete();
        await worker.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        return worker;
    }

    private UploadEvent Single(UploadOutcome outcome) => Assert.Single(_events, e => e.Outcome == outcome);

    private static async Task WaitFor(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10))
                throw new TimeoutException("Condition not reached.");
            await Task.Delay(5);
        }
    }

    // ---- The normal path ----

    [Fact]
    public async Task Uploads_From_Snapshot_And_Records_State()
    {
        AddFile(Photo1);

        await RunAll(Photo1);

        var upload = Assert.Single(_client.Uploads);
        Assert.Equal(Path.GetFileName(Photo1), upload.FileName);
        Assert.Equal(_contents[Photo1], upload.Content);
        Assert.Equal(Convert.ToHexStringLower(SHA1.HashData(_contents[Photo1])), upload.Sha1);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 11, 21, 21, 34, 55, 168, DateTimeKind.Local)), upload.CreatedAt);
        Assert.Equal(AssetVisibility.Timeline, upload.Visibility);

        var evt = Single(UploadOutcome.Uploaded);
        Assert.Equal("asset-1", evt.AssetId);
        Assert.Equal(@"2026-11\" + Path.GetFileName(Photo1), evt.RelativePath);

        var entry = _state.Get(Photo1)!;
        Assert.Equal("asset-1", entry.AssetId);
        Assert.Equal(upload.Sha1, entry.Sha1);
        Assert.Equal(1000, entry.Size);
        Assert.Null(entry.Skipped);
        Assert.Equal(1, _state.Session.Uploaded);
        Assert.Equal(evt.RelativePath, _state.Session.LastFile);
        Assert.Equal(_clock.UtcNow, _state.HeartbeatUtc);
        Assert.True(_saves >= 1);
        Assert.Empty(_client.AlbumLookups);
        Assert.Equal(WorkerState.Idle, _statuses[^1].State);
        Assert.Contains(_statuses, s => s.State == WorkerState.Uploading && s.Text == "Uploading " + Path.GetFileName(Photo1));
    }

    [Fact]
    public async Task Duplicate_Counts_As_Success()
    {
        AddFile(Photo1);
        _client.Then(UploadResult.Duplicate("existing"));

        await RunAll(Photo1);

        Assert.Equal("existing", Single(UploadOutcome.Duplicate).AssetId);
        Assert.Equal("existing", _state.Get(Photo1)!.AssetId);
        Assert.Equal(1, _state.Session.Duplicates);
        Assert.Equal(0, _state.Session.Uploaded);
        Assert.Single(_client.Uploads);
    }

    [Fact]
    public async Task Uploads_In_Arrival_Order()
    {
        AddFile(Photo1);
        AddFile(Photo2);
        AddFile(Photo3);

        await RunAll(Photo3, Photo1, Photo2);

        Assert.Equal([Path.GetFileName(Photo3), Path.GetFileName(Photo1), Path.GetFileName(Photo2)], _client.UploadedNames);
        Assert.Equal(3, _state.Session.Uploaded);
    }

    [Fact]
    public async Task Filtered_And_Missing_Files_Are_Ignored()
    {
        const string copy = Month + @"\VRChat_2026-11-21_21-34-55.168_3840x2160 - Copy.png";
        const string sticker = Root + @"\Stickers\2026-11\VRChat_2026-11-21_21-34-55.168_3840x2160.png";
        AddFile(copy);
        AddFile(sticker);

        await RunAll(copy, sticker, Photo2 /* doesn't exist */);

        Assert.Empty(_client.Uploads);
        Assert.Empty(_events);
        Assert.Empty(_state.Entries);
    }

    [Fact]
    public async Task Visibility_From_Settings_Is_Sent()
    {
        AddFile(Photo1);
        _config = _config with { Visibility = AssetVisibility.Archive };

        await RunAll(Photo1);

        Assert.Equal(AssetVisibility.Archive, Assert.Single(_client.Uploads).Visibility);
    }

    // ---- Layers and prints ----

    [Fact]
    public async Task Layer_Is_Skipped_When_UploadLayers_Off()
    {
        AddFile(Combined);
        AddFile(Environment);

        await RunAll(Combined, Environment);

        Assert.Equal([Path.GetFileName(Combined)], _client.UploadedNames);
        var skip = Single(UploadOutcome.Skipped);
        Assert.Equal(SkipReason.Layer, skip.Skip);
        Assert.Equal(Environment, skip.Path);
        Assert.Equal(StateEntry.SkippedLayer, _state.Get(Environment)!.Skipped);
        Assert.Null(_state.Get(Environment)!.AssetId);
        Assert.Equal(1, _state.Session.LayersSkipped);
    }

    [Fact]
    public async Task Layer_Is_Uploaded_When_UploadLayers_On()
    {
        AddFile(Combined);
        AddFile(Environment);
        _config = _config with { UploadLayers = true };

        await RunAll(Combined, Environment);

        Assert.Equal([Path.GetFileName(Combined), Path.GetFileName(Environment)], _client.UploadedNames);
        Assert.Equal(0, _state.Session.LayersSkipped);
        Assert.NotNull(_state.Get(Environment)!.AssetId);
    }

    [Fact]
    public async Task Skipped_Layer_Is_Not_Counted_Twice()
    {
        AddFile(Environment);
        await RunAll(Environment);

        var queue2 = new UploadQueue();
        var worker = new UploadWorker(queue2, _state, _ => { }, () => _config, () => _client, _probe, _clock);
        var skips = new List<UploadEvent>();
        worker.Skipped += skips.Add;
        queue2.Enqueue(Environment);
        queue2.Complete();
        await worker.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SkipReason.Unchanged, Assert.Single(skips).Skip);
        Assert.Equal(1, _state.Session.LayersSkipped);
    }

    [Fact]
    public async Task Print_Is_Skipped_When_ExcludePrints_On()
    {
        AddFile(Print);
        AddFile(Photo1);
        _prints.Add(Print);

        await RunAll(Print, Photo1);

        Assert.Equal([Path.GetFileName(Photo1)], _client.UploadedNames);
        Assert.Equal(SkipReason.Print, Single(UploadOutcome.Skipped).Skip);
        Assert.Equal(StateEntry.SkippedPrint, _state.Get(Print)!.Skipped);
        Assert.Equal(1, _state.Session.PrintsSkipped);
    }

    [Fact]
    public async Task Print_Is_Uploaded_When_ExcludePrints_Off_Without_Detection()
    {
        AddFile(Print);
        _prints.Add(Print);
        _config = _config with { ExcludePrints = false };

        await RunAll(Print);

        Assert.Equal([Path.GetFileName(Print)], _client.UploadedNames);
        Assert.Equal(0, _printChecks);
        Assert.Equal(0, _state.Session.PrintsSkipped);
    }

    [Fact]
    public async Task Print_Detection_Exception_Uploads_Anyway()
    {
        AddFile(Print);
        _isPrint = _ => throw new ArgumentException("Parameter is not valid.");

        await RunAll(Print);

        Assert.Equal([Path.GetFileName(Print)], _client.UploadedNames);
        Assert.Single(_events, e => e.Outcome == UploadOutcome.Uploaded);
        Assert.Contains(_log, l => l.Contains("Print check failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Settings_Are_Read_Live_For_The_Next_File()
    {
        AddFile(Environment);
        const string player = LayerBase + "_Player.png";
        AddFile(player);
        var worker = CreateWorker();
        var run = worker.RunAsync();

        _queue.Enqueue(Environment);
        await WaitFor(() => { lock (_events) return _events.Count == 1; });
        Assert.Equal(SkipReason.Layer, _events[0].Skip);

        _config = _config with { UploadLayers = true };
        _queue.Enqueue(player);
        await WaitFor(() => { lock (_events) return _events.Count == 2; });
        _queue.Complete();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(UploadOutcome.Uploaded, _events[1].Outcome);
        Assert.Equal([Path.GetFileName(player)], _client.UploadedNames);
    }

    // ---- Unchanged skip ----

    [Fact]
    public async Task Unchanged_File_In_State_Is_Not_Uploaded()
    {
        AddFile(Photo1);
        var stat = _probe.TryStat(Photo1)!.Value;
        _state.Set(Photo1, new StateEntry { Size = stat.Length, MtimeUtc = stat.LastWriteUtc, Sha1 = "x", AssetId = "old" });

        await RunAll(Photo1);

        Assert.Empty(_client.Uploads);
        Assert.Equal(SkipReason.Unchanged, Single(UploadOutcome.Skipped).Skip);
        Assert.Equal("old", _state.Get(Photo1)!.AssetId);
        Assert.Equal(0, _state.Session.Uploaded);
    }

    [Fact]
    public async Task Changed_File_In_State_Is_Uploaded_Again()
    {
        AddFile(Photo1);
        _state.Set(Photo1, new StateEntry { Size = 999, MtimeUtc = _clock.UtcNow.AddMinutes(-1), Sha1 = "x", AssetId = "old" });

        await RunAll(Photo1);

        Assert.Single(_client.Uploads);
        Assert.Equal("asset-1", _state.Get(Photo1)!.AssetId);
    }

    // ---- Snapshot ----

    [Fact]
    public async Task Snapshot_Change_During_Read_Settles_Again()
    {
        AddFile(Photo1);
        // VRCX rewrites the file while the first snapshot is being read.
        _onRead = (path, n) => { if (n == 1) ChangeFile(path, 1201); };

        await RunAll(Photo1);

        Assert.Equal(2, _reads);
        Assert.Equal(2, _probe.MetadataChecks); // the settle ran twice
        var upload = Assert.Single(_client.Uploads);
        Assert.Equal(_contents[Photo1], upload.Content);
        Assert.Equal(1201, upload.Content.Length);
        Assert.Equal(1201, _state.Get(Photo1)!.Size);
        Assert.Contains(_log, l => l.Contains("changed while it was being read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Snapshot_That_Never_Stabilises_Is_Not_Uploaded()
    {
        AddFile(Photo1);
        _onRead = (path, n) => ChangeFile(path, 1000 + n);

        await RunAll(Photo1);

        Assert.Equal(UploadWorker.MaxSnapshotAttempts, _reads);
        Assert.Empty(_client.Uploads);
    }

    // ---- Server answers ----

    [Fact]
    public void RetryDelay_Follows_Backoff_Schedule()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), UploadWorker.RetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(15), UploadWorker.RetryDelay(2));
        Assert.Equal(TimeSpan.FromMinutes(1), UploadWorker.RetryDelay(3));
        Assert.Equal(TimeSpan.FromMinutes(5), UploadWorker.RetryDelay(4));
        Assert.Equal(TimeSpan.FromMinutes(5), UploadWorker.RetryDelay(5));
        Assert.Equal(TimeSpan.FromMinutes(5), UploadWorker.RetryDelay(50));
    }

    [Fact]
    public async Task Transient_Is_Retried_With_Backoff_Without_Losing_Order()
    {
        AddFile(Photo1);
        AddFile(Photo2);
        _client.Then(
            UploadResult.Transient(503, "down"),
            UploadResult.Transient(null, "timeout"),
            UploadResult.Transient(502, "bad gateway"),
            UploadResult.Transient(504, "gateway timeout"),
            UploadResult.Transient(500, "error"));

        await RunAll(Photo1, Photo2);

        var n1 = Path.GetFileName(Photo1);
        Assert.Equal([n1, n1, n1, n1, n1, n1, Path.GetFileName(Photo2)], _client.UploadedNames);
        var retries = _statuses.Where(s => s.State == WorkerState.Retrying).ToList();
        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)],
            retries.Select(s => s.RetryIn!.Value));
        Assert.All(retries, s => Assert.Equal(Photo1, s.File));
        Assert.Equal("Retrying in 5s", retries[0].Text);
        Assert.Equal(2, _state.Session.Uploaded);
        Assert.Equal(0, _state.Session.Failed);
    }

    [Fact]
    public async Task Backoff_Waits_On_The_Clock()
    {
        AddFile(Photo1);
        _client.Then(UploadResult.Transient(503, "down"), UploadResult.Transient(503, "down"));
        var times = new List<DateTime>();
        _client.OnUpload = _ => times.Add(_clock.UtcNow);

        await RunAll(Photo1);

        Assert.Equal(3, times.Count);
        Assert.True(times[1] - times[0] >= TimeSpan.FromSeconds(5));
        Assert.True(times[2] - times[1] >= TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task AuthError_Pauses_Until_Resume()
    {
        AddFile(Photo1);
        AddFile(Photo2);
        _client.Then(UploadResult.AuthError(401, "Invalid API key"));
        var worker = CreateWorker();
        _queue.Enqueue(Photo1);
        _queue.Enqueue(Photo2);
        _queue.Complete();

        var run = worker.RunAsync();
        await WaitFor(() => worker.IsPaused);
        Assert.Equal(WorkerState.PausedAuth, worker.Status.State);
        Assert.Equal("Key rejected — uploads paused", worker.Status.Text);
        await Task.Delay(50);
        Assert.Single(_client.Uploads); // nothing else is tried while paused
        Assert.False(run.IsCompleted);

        worker.Resume();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        var n1 = Path.GetFileName(Photo1);
        Assert.Equal([n1, n1, Path.GetFileName(Photo2)], _client.UploadedNames);
        Assert.False(worker.IsPaused);
        Assert.Equal(2, _state.Session.Uploaded);
        Assert.Equal(0, _state.Session.Failed);
    }

    [Fact]
    public async Task No_Client_Pauses_As_Not_Set_Up_Until_Resume()
    {
        AddFile(Photo1);
        _currentClient = null;
        var worker = CreateWorker();
        _queue.Enqueue(Photo1);
        _queue.Complete();

        var run = worker.RunAsync();
        await WaitFor(() => worker.IsPaused);
        Assert.Equal(WorkerState.PausedNotSetUp, worker.Status.State);

        _currentClient = _client;
        worker.Resume();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(_client.Uploads);
    }

    [Fact]
    public async Task Rejected_Is_Marked_Failed_And_The_Queue_Moves_On()
    {
        AddFile(Photo1);
        AddFile(Photo2);
        _client.Then(UploadResult.Rejected(400, "Unsupported file type"));

        await RunAll(Photo1, Photo2);

        var failed = Single(UploadOutcome.Failed);
        Assert.Equal(Photo1, failed.Path);
        Assert.Contains("Unsupported file type", failed.Detail);
        Assert.Equal(1, _state.Session.Failed);
        Assert.Null(_state.Get(Photo1));
        Assert.Equal(1, _state.Session.Uploaded);
        Assert.Equal(2, _client.Uploads.Count);
    }

    [Fact]
    public async Task Client_Exception_Is_Treated_As_Transient()
    {
        AddFile(Photo1);
        var calls = 0;
        _client.OnUpload = _ => { if (++calls == 1) throw new HttpRequestException("connection reset"); };

        await RunAll(Photo1);

        Assert.Equal(2, _client.Uploads.Count);
        Assert.Single(_statuses, s => s.State == WorkerState.Retrying);
        Assert.Equal(1, _state.Session.Uploaded);
    }

    // ---- Album ----

    [Fact]
    public async Task Album_Add_When_Album_Set()
    {
        AddFile(Photo1);
        AddFile(Photo2);
        _config = _config with { AlbumName = "VRChat" };
        _client.Then(UploadResult.Created("a1"), UploadResult.Duplicate("a2"));

        await RunAll(Photo1, Photo2);

        Assert.Equal(["VRChat", "VRChat"], _client.AlbumLookups);
        Assert.Equal(2, _client.AlbumAdds.Count);
        Assert.Equal("album-1", _client.AlbumAdds[0].AlbumId);
        Assert.Equal(new[] { "a1" }, _client.AlbumAdds[0].AssetIds);
        Assert.Equal(new[] { "a2" }, _client.AlbumAdds[1].AssetIds);
    }

    [Fact]
    public async Task No_Album_Calls_Without_Album()
    {
        AddFile(Photo1);
        _config = _config with { AlbumName = "  " };

        await RunAll(Photo1);

        Assert.Empty(_client.AlbumLookups);
        Assert.Empty(_client.AlbumAdds);
    }

    // ---- Settle ----

    [Fact]
    public async Task Metadata_Present_Uploads_Without_Waiting()
    {
        AddFile(Photo1);
        _probe.AddChunk(Photo1);
        _contents[Photo1] = RandomNumberGenerator.GetBytes(1201);
        _config = _config with { VrcxWaitSeconds = 10 };
        var start = _clock.UtcNow;

        await RunAll(Photo1);

        Assert.Single(_client.Uploads);
        Assert.True(_clock.UtcNow - start < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Several_Files_Settle_At_Once()
    {
        // Real time: three photos that never get VRCX metadata each wait the full 1 s. In sequence that would take 3 s.
        var clock = SystemClock.Instance;
        var probe = new FakeProbe(clock);
        string[] photos = [Photo1, Photo2, Photo3];
        foreach (var p in photos)
        {
            _contents[p] = RandomNumberGenerator.GetBytes(100);
            probe.Add(p, 100);
        }
        _config = _config with { VrcxWaitSeconds = 1 };
        var worker = CreateWorker(clock, probe, new FileReadiness(probe, clock, stableFor: TimeSpan.FromMilliseconds(10)));
        var maxWaiting = 0;
        worker.StatusChanged += s => { lock (_statuses) maxWaiting = Math.Max(maxWaiting, s.Waiting); };
        foreach (var p in photos)
            _queue.Enqueue(p);
        _queue.Complete();

        var sw = Stopwatch.StartNew();
        await worker.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        sw.Stop();

        Assert.Equal(photos.Select(Path.GetFileName), _client.UploadedNames);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2.5), $"took {sw.Elapsed}");
        Assert.True(maxWaiting >= 2, $"at most {maxWaiting} waiting at once");
        Assert.Contains(_statuses, s => s.State == WorkerState.WaitingForVrcx && s.Text.StartsWith("Waiting for VRCX (", StringComparison.Ordinal));
    }

    // ---- Cancellation ----

    [Fact]
    public async Task Cancel_Stops_A_Paused_Worker()
    {
        AddFile(Photo1);
        _client.Then(UploadResult.AuthError(403, "Missing permission"));
        var worker = CreateWorker();
        _queue.Enqueue(Photo1);
        using var cts = new CancellationTokenSource();

        var run = worker.RunAsync(cts.Token);
        await WaitFor(() => worker.IsPaused);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(_client.Uploads);
    }
}
