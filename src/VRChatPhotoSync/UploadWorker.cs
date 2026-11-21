using System.Security.Cryptography;
using System.Threading.Channels;

namespace VRChatPhotoSync;

/// <summary>What happened to one file.</summary>
public enum UploadOutcome
{
    /// <summary>A new Immich asset was created.</summary>
    Uploaded,
    /// <summary>The server already had this file (by checksum).</summary>
    Duplicate,
    /// <summary>Not uploaded on purpose (see <see cref="SkipReason"/>).</summary>
    Skipped,
    /// <summary>The server refused the file, or it couldn't be processed.</summary>
    Failed,
}

/// <summary>Why a file wasn't uploaded.</summary>
public enum SkipReason
{
    /// <summary>A multi-layer photo's layer file, and <see cref="AppConfig.UploadLayers"/> is off.</summary>
    Layer,
    /// <summary>A VRChat print, and <see cref="AppConfig.ExcludePrints"/> is on.</summary>
    Print,
    /// <summary>Already uploaded or skipped with the same size and write time.</summary>
    Unchanged,
}

/// <summary>An event for the UI about one file.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Path">The file's full path.</param>
/// <param name="RelativePath">The path relative to the picture folder (the <c>state.json</c> key).</param>
/// <param name="AssetId">The Immich asset id for <see cref="UploadOutcome.Uploaded"/> and <see cref="UploadOutcome.Duplicate"/>.</param>
/// <param name="Skip">The reason for <see cref="UploadOutcome.Skipped"/>.</param>
/// <param name="Detail">The error message for <see cref="UploadOutcome.Failed"/>.</param>
public sealed record UploadEvent(
    UploadOutcome Outcome, string Path, string RelativePath, string? AssetId = null, SkipReason? Skip = null, string? Detail = null);

/// <summary>What the worker is doing right now.</summary>
public enum WorkerState
{
    /// <summary>Nothing to do; watching for new photos.</summary>
    Idle,
    /// <summary>One or more files are waiting for VRCX's metadata.</summary>
    WaitingForVrcx,
    /// <summary>Uploading a file.</summary>
    Uploading,
    /// <summary>The last upload failed temporarily; waiting before the next try.</summary>
    Retrying,
    /// <summary>The key was rejected; nothing uploads until <see cref="UploadWorker.Resume"/>.</summary>
    PausedAuth,
    /// <summary>No server or key is set up; nothing uploads until <see cref="UploadWorker.Resume"/>.</summary>
    PausedNotSetUp,
}

/// <summary>The worker's status, for the status line.</summary>
/// <param name="State">What it is doing.</param>
/// <param name="Text">A short text for the window.</param>
/// <param name="File">The file being uploaded or retried, if any.</param>
/// <param name="RetryIn">The wait before the next try (<see cref="WorkerState.Retrying"/> only).</param>
/// <param name="Waiting">Number of files waiting for VRCX.</param>
public sealed record WorkerStatus(WorkerState State, string Text, string? File = null, TimeSpan? RetryIn = null, int Waiting = 0)
{
    public static WorkerStatus Idle { get; } = new(WorkerState.Idle, "Watching");
}

/// <summary>
/// Takes paths from the <see cref="UploadQueue"/> and runs each through the pipeline: filter → readiness → layer skip →
/// print skip → VRCX settle → unchanged check → snapshot → SHA-1 → upload → album → save state → UI events.
/// <para>
/// The steps up to the settle run for several files at once (up to <see cref="MaxInFlight"/>), so many photos can wait for
/// VRCX together. Uploads then happen one at a time, in arrival order. A <see cref="UploadStatus.Transient"/> failure is
/// retried with backoff (<see cref="RetryDelays"/>) on the same file, so nothing overtakes it. An
/// <see cref="UploadStatus.AuthError"/> (or no client) pauses everything until <see cref="Resume"/>. A
/// <see cref="UploadStatus.Rejected"/> file is logged and counted as failed.
/// </para>
/// <para>
/// The settings are read through a delegate for every file, so a checkbox change applies to the next file. The
/// <see cref="UploadState"/> is shared; every access to it from outside must hold <see cref="StateLock"/>.
/// </para>
/// </summary>
public sealed class UploadWorker
{
    /// <summary>Waits between tries after a transient failure; the last one repeats.</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    /// <summary>How many files can be in the pipeline (being checked or settled, or waiting to upload) at once.</summary>
    public const int DefaultMaxInFlight = 16;

    /// <summary>How often a snapshot may find the file changed (each time it settles again) before the file is re-queued.</summary>
    public const int MaxSnapshotAttempts = 5;

    private readonly UploadQueue _queue;
    private readonly UploadState _state;
    private readonly Action<UploadState> _saveState;
    private readonly Func<AppConfig> _settings;
    private readonly Func<IUploadClient?> _client;
    private readonly IFileProbe _probe;
    private readonly IClock _clock;
    private readonly FileReadiness _readiness;
    private readonly Func<string, bool> _isPrint;
    private readonly Func<string, byte[]> _readFile;

    private readonly object _statusLock = new();
    private WorkerStatus _status = WorkerStatus.Idle;
    private WorkerState _activity = WorkerState.Idle; // what the upload stage is doing (Idle/Uploading/Retrying/Paused*)
    private string? _activityFile;
    private TimeSpan? _retryIn;
    private TaskCompletionSource _wake = NewSignal();

    /// <param name="queue">Where the paths come from.</param>
    /// <param name="state">The shared state (uploaded and skipped files, session counters).</param>
    /// <param name="saveState">Saves the state (normally <see cref="UploadStateStore.Save"/>); called under <see cref="StateLock"/>.</param>
    /// <param name="settings">The current settings, read for every file.</param>
    /// <param name="client">The current Immich client, or null when not set up.</param>
    /// <param name="probe">File-system checks (fake in tests).</param>
    /// <param name="clock">Time and waiting (fake in tests).</param>
    /// <param name="readiness">The "fully written" check; defaults to one on <paramref name="probe"/> and <paramref name="clock"/>.</param>
    /// <param name="settle">The VRCX wait; defaults to one on <paramref name="probe"/> and <paramref name="clock"/>.</param>
    /// <param name="isPrint">Print detection; defaults to <see cref="PrintDetector.IsPrint(string)"/>.</param>
    /// <param name="readFile">Reads a whole file for the snapshot; defaults to a shared read from disk.</param>
    /// <param name="stateLock">The lock that guards <paramref name="state"/>; a new one if null.</param>
    public UploadWorker(
        UploadQueue queue,
        UploadState state,
        Action<UploadState> saveState,
        Func<AppConfig> settings,
        Func<IUploadClient?> client,
        IFileProbe? probe = null,
        IClock? clock = null,
        FileReadiness? readiness = null,
        VrcxSettle? settle = null,
        Func<string, bool>? isPrint = null,
        Func<string, byte[]>? readFile = null,
        object? stateLock = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _saveState = saveState ?? throw new ArgumentNullException(nameof(saveState));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _probe = probe ?? FileProbe.Instance;
        _clock = clock ?? SystemClock.Instance;
        _readiness = readiness ?? new FileReadiness(_probe, _clock);
        Settle = settle ?? new VrcxSettle(_probe, _clock);
        _isPrint = isPrint ?? PrintDetector.IsPrint;
        _readFile = readFile ?? ReadShared;
        StateLock = stateLock ?? new object();
    }

    /// <summary>The lock that guards the shared <see cref="UploadState"/>.</summary>
    public object StateLock { get; }

    /// <summary>The VRCX wait (shared with the rename handling, which calls <see cref="VrcxSettle.NotifyRenamed"/>).</summary>
    public VrcxSettle Settle { get; }

    /// <summary>How many files can be in the pipeline at once.</summary>
    public int MaxInFlight { get; init; } = DefaultMaxInFlight;

    /// <summary>The current status.</summary>
    public WorkerStatus Status
    {
        get { lock (_statusLock) return _status; }
    }

    /// <summary>True while uploads are paused (key rejected or not set up).</summary>
    public bool IsPaused
    {
        get { lock (_statusLock) return _activity is WorkerState.PausedAuth or WorkerState.PausedNotSetUp; }
    }

    /// <summary>A new asset was created.</summary>
    public event Action<UploadEvent>? Uploaded;

    /// <summary>The server already had the file.</summary>
    public event Action<UploadEvent>? Duplicate;

    /// <summary>The file was skipped (layer, print, or unchanged).</summary>
    public event Action<UploadEvent>? Skipped;

    /// <summary>The file was rejected by the server or couldn't be processed.</summary>
    public event Action<UploadEvent>? Failed;

    /// <summary>The status changed. Raised on a worker thread; the UI must marshal it.</summary>
    public event Action<WorkerStatus>? StatusChanged;

    /// <summary>A line for the log. Never contains the API key.</summary>
    public event Action<string>? Log;

    /// <summary>The wait before try number <paramref name="attempt"/> + 1 after <paramref name="attempt"/> transient failures in a row (1-based).</summary>
    public static TimeSpan RetryDelay(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        return RetryDelays[Math.Min(attempt, RetryDelays.Count) - 1];
    }

    /// <summary>
    /// Ends a pause (after the user fixed the key or set up the server), or cuts a retry wait short.
    /// The file that was waiting is tried again first.
    /// </summary>
    public void Resume()
    {
        TaskCompletionSource wake;
        lock (_statusLock)
        {
            wake = _wake;
            _wake = NewSignal();
        }
        wake.TrySetResult();
    }

    /// <summary>
    /// Processes files until the queue is completed and empty, or <paramref name="cancellationToken"/> is cancelled
    /// (then returns without throwing).
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = cts.Token;
        var pipeline = Channel.CreateBounded<Task<string?>>(new BoundedChannelOptions(Math.Max(1, MaxInFlight - 1))
        {
            SingleReader = true,
            SingleWriter = true,
        });

        var producer = Task.Run(async () =>
        {
            try
            {
                await foreach (var path in _queue.ReadAllAsync(token).ConfigureAwait(false))
                {
                    var prepare = Task.Run(() => PrepareSafeAsync(path, token), token);
                    await pipeline.Writer.WriteAsync(prepare, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            finally
            {
                pipeline.Writer.TryComplete();
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var prepare in pipeline.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var path = await prepare.ConfigureAwait(false);
                if (path is not null)
                    await UploadSafeAsync(path, token).ConfigureAwait(false);
                UpdateStatus();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
            await producer.ConfigureAwait(false);
            SetActivity(WorkerState.Idle);
        }
    }

    // ---- Stage 1 (concurrent): filter, readiness, layer/print skip, settle. Returns the path to upload, or null. ----

    private async Task<string?> PrepareSafeAsync(string path, CancellationToken token)
    {
        try
        {
            return await PrepareAsync(path, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            Fail(path, $"Could not process {path}: {ex.Message}");
            return null;
        }
    }

    private async Task<string?> PrepareAsync(string path, CancellationToken token)
    {
        var settings = _settings();

        // 1. Filter.
        if (!Accepted(path, settings))
            return null;
        WriteLog($"Queued {path}");

        // 2. Readiness.
        if (!await WaitReadyAsync(path, token).ConfigureAwait(false))
            return null;

        // 3. Layer skip.
        settings = _settings();
        if (VrcFileName.IsLayer(path) && !settings.UploadLayers)
        {
            RecordSkip(path, SkipReason.Layer);
            return null;
        }

        // 4. Print skip.
        if (settings.ExcludePrints)
        {
            bool print;
            try
            {
                print = _isPrint(path);
            }
            catch (Exception ex)
            {
                WriteLog($"Print check failed for {path} ({ex.Message}); uploading it anyway.");
                print = false;
            }
            if (print)
            {
                RecordSkip(path, SkipReason.Print);
                return null;
            }
        }

        // 5. Settle.
        return await SettleAsync(path, token).ConfigureAwait(false);
    }

    private bool Accepted(string path, AppConfig settings)
    {
        string? root;
        lock (StateLock) root = _state.PictureFolder;
        if (root is null || !PhotoFilter.Accepts(root, path, settings.StrictFileNames))
        {
            WriteLog($"Ignored {path} (not a photo to upload).");
            return false;
        }
        return true;
    }

    /// <summary>True when ready; false when missing or re-queued.</summary>
    private async Task<bool> WaitReadyAsync(string path, CancellationToken token)
    {
        switch (await _readiness.WaitUntilReadyAsync(path, token).ConfigureAwait(false))
        {
            case ReadinessResult.Ready:
                return true;
            case ReadinessResult.Missing:
                WriteLog($"{path} disappeared before it could be uploaded.");
                return false;
            default:
                Requeue(path, "still being written");
                return false;
        }
    }

    /// <summary>Waits for VRCX; returns the (possibly renamed) path, or null when the file is gone or no longer accepted.</summary>
    private async Task<string?> SettleAsync(string path, CancellationToken token)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(0, _settings().VrcxWaitSeconds));
        var waiting = Settle.WaitAsync(path, timeout, token);
        UpdateStatus();
        SettleResult result;
        try
        {
            result = await waiting.ConfigureAwait(false);
        }
        finally
        {
            UpdateStatus();
        }

        WriteLog($"Settled {result.Path}: {result.Reason} after {result.Waited.TotalSeconds:0.0} s" +
                 (result.Renamed ? $" (renamed from {Path.GetFileName(path)})" : "") + ".");
        if (result.Reason == SettleReason.Missing)
            return null;
        if (result.Renamed && !Accepted(result.Path, _settings()))
            return null;
        return result.Path;
    }

    private void RecordSkip(string path, SkipReason reason)
    {
        var stat = _probe.TryStat(path);
        if (stat is not { } s)
            return;
        string relative;
        bool already;
        lock (StateLock)
        {
            relative = _state.ToKey(path);
            already = _state.IsUnchanged(path, s.Length, s.LastWriteUtc);
            if (!already)
            {
                _state.Set(path, new StateEntry
                {
                    Size = s.Length,
                    MtimeUtc = s.LastWriteUtc,
                    Skipped = reason == SkipReason.Print ? StateEntry.SkippedPrint : StateEntry.SkippedLayer,
                });
                if (reason == SkipReason.Print)
                    _state.Session.PrintsSkipped++;
                else
                    _state.Session.LayersSkipped++;
                SaveStateLocked();
            }
        }
        var actual = already ? SkipReason.Unchanged : reason;
        WriteLog($"Skipped {relative} ({actual.ToString().ToLowerInvariant()}).");
        Raise(Skipped, new UploadEvent(UploadOutcome.Skipped, path, relative, Skip: actual));
    }

    // ---- Stage 2 (one at a time, in order): unchanged check, snapshot, hash, upload, album, state. ----

    private async Task UploadSafeAsync(string path, CancellationToken token)
    {
        try
        {
            await UploadAsync(path, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Fail(path, $"Could not upload {path}: {ex.Message}");
        }
        finally
        {
            SetActivity(WorkerState.Idle);
        }
    }

    private async Task UploadAsync(string path, CancellationToken token)
    {
        var snapshotAttempts = 0;
        var transientFailures = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();

            // 6. Skip if unchanged in state.
            var current = _probe.TryStat(path);
            if (current is not { } stat)
            {
                WriteLog($"{path} disappeared before it could be uploaded.");
                return;
            }
            string relative;
            bool unchanged;
            lock (StateLock)
            {
                relative = _state.ToKey(path);
                unchanged = _state.IsUnchanged(path, stat.Length, stat.LastWriteUtc);
            }
            if (unchanged)
            {
                WriteLog($"Skipped {relative} (unchanged).");
                Raise(Skipped, new UploadEvent(UploadOutcome.Skipped, path, relative, Skip: SkipReason.Unchanged));
                return;
            }

            // 7. Snapshot.
            var snapshot = TakeSnapshot(path, stat);
            if (snapshot is null)
            {
                if (!_probe.Exists(path))
                {
                    WriteLog($"{path} disappeared before it could be uploaded.");
                    return;
                }
                if (++snapshotAttempts >= MaxSnapshotAttempts)
                {
                    Requeue(path, "it keeps changing");
                    return;
                }
                WriteLog($"{relative} changed while it was being read; waiting for it to settle again.");
                if (!await WaitReadyAsync(path, token).ConfigureAwait(false))
                    return;
                var settled = await SettleAsync(path, token).ConfigureAwait(false);
                if (settled is null)
                    return;
                path = settled;
                continue;
            }
            var (bytes, after) = snapshot.Value;

            var client = _client();
            if (client is null)
            {
                await PauseAsync(WorkerState.PausedNotSetUp, path, token).ConfigureAwait(false);
                continue;
            }

            // 8. SHA-1 of the snapshot.
            var sha1 = Convert.ToHexStringLower(SHA1.HashData(bytes));

            // 9. Upload from the snapshot.
            SetActivity(WorkerState.Uploading, path);
            var settings = _settings();
            var createdAt = VrcFileName.GetCaptureTime(path, _ => (after.CreationUtc, after.LastWriteUtc));
            var modifiedAt = new DateTimeOffset(UploadState.ToUtc(after.LastWriteUtc));
            UploadResult result;
            try
            {
                result = await client.UploadAsync(bytes, Path.GetFileName(path), sha1, createdAt, modifiedAt, settings.Visibility, token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                result = UploadResult.Transient(null, ex.Message);
            }

            switch (result.Status)
            {
                case UploadStatus.Created:
                case UploadStatus.Duplicate:
                    // 10. Album. 11. State. 12. Events.
                    await AddToAlbumAsync(client, settings, result.AssetId!, relative, token).ConfigureAwait(false);
                    RecordUpload(path, relative, after, sha1, result);
                    return;

                case UploadStatus.Transient:
                    transientFailures++;
                    var delay = RetryDelay(transientFailures);
                    WriteLog($"Upload of {relative} failed ({Describe(result)}); retry {transientFailures} in {delay.TotalSeconds:0} s.");
                    SetActivity(WorkerState.Retrying, path, delay);
                    await WaitOrWakeAsync(delay, token).ConfigureAwait(false);
                    continue;

                case UploadStatus.AuthError:
                    WriteLog($"Upload of {relative} was refused ({Describe(result)}); the API key was rejected. Uploads are paused.");
                    await PauseAsync(WorkerState.PausedAuth, path, token).ConfigureAwait(false);
                    transientFailures = 0;
                    continue;

                default:
                    Fail(path, $"The server rejected {relative} ({Describe(result)}).");
                    return;
            }
        }
    }

    /// <summary>Reads the whole file; null when it changed during the read (or couldn't be read).</summary>
    private (byte[] Bytes, FileStat Stat)? TakeSnapshot(string path, FileStat before)
    {
        byte[] bytes;
        try
        {
            bytes = _readFile(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteLog($"Could not read {path} ({ex.Message}).");
            return null;
        }
        var after = _probe.TryStat(path);
        if (after is not { } a || !a.SameContentAs(before) || a.Length != bytes.LongLength)
            return null;
        return (bytes, a);
    }

    private async Task AddToAlbumAsync(IUploadClient client, AppConfig settings, string assetId, string relative, CancellationToken token)
    {
        if (!settings.HasAlbum)
            return;
        var name = settings.AlbumName!.Trim();
        try
        {
            var album = await client.GetOrCreateAlbumAsync(name, token).ConfigureAwait(false);
            if (!album.Succeeded)
            {
                WriteLog($"Could not find or create album \"{name}\" ({album.Status}, {album.HttpStatus}: {album.Message}); {relative} is uploaded without it.");
                return;
            }
            var added = await client.AddToAlbumAsync(album.Value!, [assetId], token).ConfigureAwait(false);
            if (!added.Succeeded || !added.Value!.AllSucceeded)
                WriteLog($"Could not add {relative} to album \"{name}\" ({added.Status}, {added.HttpStatus}: {added.Message ?? added.Value?.Failed.FirstOrDefault()?.Error}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            WriteLog($"Could not add {relative} to album \"{name}\" ({ex.Message}).");
        }
    }

    private void RecordUpload(string path, string relative, FileStat stat, string sha1, UploadResult result)
    {
        var created = result.Status == UploadStatus.Created;
        lock (StateLock)
        {
            _state.Set(path, new StateEntry { Size = stat.Length, MtimeUtc = stat.LastWriteUtc, Sha1 = sha1, AssetId = result.AssetId });
            if (created)
                _state.Session.Uploaded++;
            else
                _state.Session.Duplicates++;
            _state.Session.LastFile = relative;
            _state.Heartbeat(_clock.UtcNow);
            SaveStateLocked();
        }
        WriteLog($"{(created ? "Uploaded" : "Duplicate")} {relative} → asset {result.AssetId}.");
        var evt = new UploadEvent(created ? UploadOutcome.Uploaded : UploadOutcome.Duplicate, path, relative, result.AssetId);
        Raise(created ? Uploaded : Duplicate, evt);
    }

    // ---- Pausing and waiting ----

    private async Task PauseAsync(WorkerState reason, string path, CancellationToken token)
    {
        Task wake;
        lock (_statusLock) wake = _wake.Task;
        SetActivity(reason, path);
        await wake.WaitAsync(token).ConfigureAwait(false);
        WriteLog("Uploads resumed.");
        SetActivity(WorkerState.Idle);
    }

    private async Task WaitOrWakeAsync(TimeSpan delay, CancellationToken token)
    {
        Task wake;
        lock (_statusLock) wake = _wake.Task;
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var wait = _clock.Delay(delay, delayCts.Token);
        await Task.WhenAny(wait, wake).ConfigureAwait(false);
        await delayCts.CancelAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---- Helpers ----

    private void Requeue(string path, string why)
    {
        if (_queue.Enqueue(path))
            WriteLog($"Re-queued {path} ({why}).");
        else
            WriteLog($"Gave up on {path} for now ({why}); the next scan will find it.");
    }

    private void Fail(string path, string message)
    {
        string relative;
        lock (StateLock)
        {
            relative = SafeKey(path);
            _state.Session.Failed++;
            SaveStateLocked();
        }
        WriteLog(message);
        Raise(Failed, new UploadEvent(UploadOutcome.Failed, path, relative, Detail: message));
    }

    private string SafeKey(string path)
    {
        try { return _state.ToKey(path); }
        catch (ArgumentException) { return path; }
    }

    private void SaveStateLocked()
    {
        try
        {
            _saveState(_state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteLog($"Could not save the state file ({ex.Message}).");
        }
    }

    private static string Describe(UploadResult result) =>
        result.HttpStatus is { } code ? $"HTTP {code}: {result.Message}" : result.Message ?? result.Status.ToString();

    private void SetActivity(WorkerState activity, string? file = null, TimeSpan? retryIn = null)
    {
        lock (_statusLock)
        {
            _activity = activity;
            _activityFile = file;
            _retryIn = retryIn;
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        WorkerStatus status;
        lock (_statusLock)
        {
            var waiting = Settle.ActiveCount;
            var name = _activityFile is null ? null : Path.GetFileName(_activityFile);
            status = _activity switch
            {
                WorkerState.PausedAuth => new(WorkerState.PausedAuth, "Key rejected — uploads paused", _activityFile, null, waiting),
                WorkerState.PausedNotSetUp => new(WorkerState.PausedNotSetUp, "Not set up", _activityFile, null, waiting),
                WorkerState.Retrying => new(WorkerState.Retrying, $"Retrying in {_retryIn!.Value.TotalSeconds:0}s", _activityFile, _retryIn, waiting),
                WorkerState.Uploading => new(WorkerState.Uploading, $"Uploading {name}", _activityFile, null, waiting),
                _ when waiting > 0 => new(WorkerState.WaitingForVrcx, $"Waiting for VRCX ({waiting})", null, null, waiting),
                _ => WorkerStatus.Idle,
            };
            if (status == _status)
                return;
            _status = status;
        }
        try { StatusChanged?.Invoke(status); }
        catch { /* a UI handler must never break the worker */ }
    }

    private static void Raise(Action<UploadEvent>? handler, UploadEvent evt)
    {
        try { handler?.Invoke(evt); }
        catch { /* a UI handler must never break the worker */ }
    }

    private void WriteLog(string message)
    {
        try { Log?.Invoke(message); }
        catch { /* logging must never break the worker */ }
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = PngChunks.OpenShared(path);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
