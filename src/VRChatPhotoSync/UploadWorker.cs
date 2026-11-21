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
    /// <summary>
    /// A file that was already uploaded changed (VRCX added its metadata late) and its new version was uploaded.
    /// Not counted as a new upload.
    /// </summary>
    Updated,
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
/// <param name="Detail">The error message for <see cref="UploadOutcome.Failed"/>, or the text to show for <see cref="UploadOutcome.Updated"/>.</param>
/// <param name="PreviousAssetId">For <see cref="UploadOutcome.Updated"/>: the asset of the version uploaded before.</param>
/// <param name="Replaced">For <see cref="UploadOutcome.Updated"/>: true when the old asset was copied to the new one and trashed.</param>
public sealed record UploadEvent(
    UploadOutcome Outcome, string Path, string RelativePath, string? AssetId = null, SkipReason? Skip = null, string? Detail = null,
    string? PreviousAssetId = null, bool Replaced = false);

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
/// Renames (<see cref="NotifyRenamed"/>) follow a file wherever it is: queued, in the pipeline, or in the state. A file in
/// the state that changed is uploaded again; when that creates a new asset, the old one is replaced (copy, then trash)
/// if the key allows it, and the file is reported as <see cref="UploadOutcome.Updated"/>, not as a new upload.
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

    /// <summary>A file in the pipeline. <see cref="Path"/> follows renames; guarded by <see cref="StateLock"/>.</summary>
    private sealed class Tracked(string path)
    {
        public string Path = path;
    }

    private readonly List<Tracked> _inFlight = []; // guarded by StateLock

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

    /// <summary>An already uploaded file changed and its new version was uploaded (<see cref="UploadOutcome.Updated"/>).</summary>
    public event Action<UploadEvent>? Updated;

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
    /// Handles <see cref="FolderWatcher.FileRenamed"/>. A file waiting in the queue, being checked or settled, or
    /// waiting to upload continues under <paramref name="newPath"/>, so the short-lived original name of a VRCX rename
    /// is never uploaded. A file already in the state keeps its entry under the new path (<see cref="UploadState.MoveEntry"/>),
    /// so it isn't treated as a new photo. Returns true when anything was moved.
    /// </summary>
    public bool NotifyRenamed(string oldPath, string newPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(oldPath);
        ArgumentException.ThrowIfNullOrEmpty(newPath);
        if (PathEquals(oldPath, newPath))
            return false;

        var tracked = false;
        var moved = false;
        lock (StateLock)
        {
            foreach (var t in _inFlight)
            {
                if (PathEquals(t.Path, oldPath))
                {
                    t.Path = newPath;
                    tracked = true;
                }
            }
            try
            {
                moved = _state.MoveEntry(oldPath, newPath);
            }
            catch (ArgumentException)
            {
                moved = false;
            }
            if (moved)
                SaveStateLocked();
        }
        var queued = _queue.Rename(oldPath, newPath);
        var settling = Settle.NotifyRenamed(oldPath, newPath);

        if (queued || tracked || settling || moved)
        {
            var what = string.Join(", ", new[]
            {
                queued ? "queued" : null,
                tracked || settling ? "in progress" : null,
                moved ? "state entry moved" : null,
            }.Where(s => s is not null));
            WriteLog($"Renamed {oldPath} → {Path.GetFileName(newPath)} ({what}).");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Processes files until the queue is completed and empty, or <paramref name="cancellationToken"/> is cancelled
    /// (then returns without throwing).
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = cts.Token;
        var pipeline = Channel.CreateBounded<(Tracked File, Task<bool> Prepare)>(new BoundedChannelOptions(Math.Max(1, MaxInFlight - 1))
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
                    var file = Track(path);
                    var prepare = Task.Run(() => PrepareSafeAsync(file, token), token);
                    await pipeline.Writer.WriteAsync((file, prepare), token).ConfigureAwait(false);
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
            await foreach (var (file, prepare) in pipeline.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    if (await prepare.ConfigureAwait(false))
                        await UploadSafeAsync(file, token).ConfigureAwait(false);
                }
                finally
                {
                    Untrack(file);
                }
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
            lock (StateLock) _inFlight.Clear();
            SetActivity(WorkerState.Idle);
        }
    }

    private Tracked Track(string path)
    {
        var file = new Tracked(path);
        lock (StateLock) _inFlight.Add(file);
        return file;
    }

    private void Untrack(Tracked file)
    {
        lock (StateLock) _inFlight.Remove(file);
    }

    /// <summary>The file's current path (after any renames).</summary>
    private string Current(Tracked file)
    {
        lock (StateLock) return file.Path;
    }

    // ---- Stage 1 (concurrent): filter, readiness, layer/print skip, settle. Returns true when the file should be uploaded. ----

    private async Task<bool> PrepareSafeAsync(Tracked file, CancellationToken token)
    {
        try
        {
            return await PrepareAsync(file, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            var path = Current(file);
            Fail(path, $"Could not process {path}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> PrepareAsync(Tracked file, CancellationToken token)
    {
        var settings = _settings();
        var path = Current(file);

        // 1. Filter.
        if (!Accepted(path, settings))
            return false;
        WriteLog($"Queued {path}");

        // 2. Readiness (following a rename).
        if (await WaitReadyAsync(file, token).ConfigureAwait(false) is not { } ready)
            return false;
        path = ready;

        // 3. Layer skip.
        settings = _settings();
        if (VrcFileName.IsLayer(path) && !settings.UploadLayers)
        {
            RecordSkip(path, SkipReason.Layer);
            return false;
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
                return false;
            }
        }

        // 5. Settle.
        return await SettleAsync(file, token).ConfigureAwait(false) is not null;
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

    /// <summary>
    /// Waits until the file is fully written, following renames. Returns its current path, or null when it is gone,
    /// renamed to a name that isn't uploaded, or re-queued.
    /// </summary>
    private async Task<string?> WaitReadyAsync(Tracked file, CancellationToken token)
    {
        var path = Current(file);
        while (true)
        {
            var result = await _readiness.WaitUntilReadyAsync(path, token).ConfigureAwait(false);
            var current = Current(file);
            if (!PathEquals(current, path))
            {
                // Renamed while waiting: check the new name, then wait for it.
                if (!Accepted(current, _settings()))
                    return null;
                path = current;
                continue;
            }
            switch (result)
            {
                case ReadinessResult.Ready:
                    return path;
                case ReadinessResult.Missing:
                    WriteLog($"{path} disappeared before it could be uploaded.");
                    return null;
                default:
                    Requeue(path, "still being written");
                    return null;
            }
        }
    }

    /// <summary>
    /// Waits for VRCX, following renames; returns the (possibly renamed) path, or null when the file is gone or no longer
    /// accepted.
    /// </summary>
    private async Task<string?> SettleAsync(Tracked file, CancellationToken token)
    {
        var path = Current(file);
        var original = path;
        while (true)
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

            // A rename reported just before the wait started isn't seen by it: the file looks missing. Wait on the new name.
            var current = Current(file);
            if (result.Reason == SettleReason.Missing && !PathEquals(current, path))
            {
                if (!Accepted(current, _settings()))
                    return null;
                path = current;
                continue;
            }

            var renamed = !PathEquals(result.Path, original);
            WriteLog($"Settled {result.Path}: {result.Reason} after {result.Waited.TotalSeconds:0.0} s" +
                     (renamed ? $" (renamed from {Path.GetFileName(original)})" : "") + ".");
            if (result.Reason == SettleReason.Missing)
                return null;
            // NotifyRenamed moves both the settle wait and the tracked path; the tracked one is the latest.
            lock (StateLock)
            {
                if (result.Renamed && PathEquals(file.Path, path))
                    file.Path = result.Path;
                current = file.Path;
            }
            if (!PathEquals(current, original) && !Accepted(current, _settings()))
                return null;
            return current;
        }
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

    private async Task UploadSafeAsync(Tracked file, CancellationToken token)
    {
        try
        {
            await UploadAsync(file, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var path = Current(file);
            Fail(path, $"Could not upload {path}: {ex.Message}");
        }
        finally
        {
            SetActivity(WorkerState.Idle);
        }
    }

    private async Task UploadAsync(Tracked file, CancellationToken token)
    {
        var snapshotAttempts = 0;
        var transientFailures = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var path = Current(file);

            // 6. Skip if unchanged in state.
            var current = _probe.TryStat(path);
            if (current is not { } stat)
            {
                if (!PathEquals(Current(file), path))
                    continue; // renamed just now
                WriteLog($"{path} disappeared before it could be uploaded.");
                return;
            }
            string relative;
            bool unchanged;
            StateEntry? previous;
            lock (StateLock)
            {
                relative = _state.ToKey(path);
                unchanged = _state.IsUnchanged(path, stat.Length, stat.LastWriteUtc);
                previous = _state.Get(path);
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
                    if (!PathEquals(Current(file), path))
                        continue; // renamed while it was being read
                    WriteLog($"{path} disappeared before it could be uploaded.");
                    return;
                }
                if (++snapshotAttempts >= MaxSnapshotAttempts)
                {
                    Requeue(Current(file), "it keeps changing");
                    return;
                }
                WriteLog($"{relative} changed while it was being read; waiting for it to settle again.");
                if (await WaitReadyAsync(file, token).ConfigureAwait(false) is null)
                    return;
                if (await SettleAsync(file, token).ConfigureAwait(false) is null)
                    return;
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
                    // A new version of a file uploaded before (VRCX added its metadata late): replace the old asset.
                    var previousId = previous is { Skipped: null, AssetId: { Length: > 0 } id } ? id : null;
                    var replaced = false;
                    if (previousId is not null && result.Status == UploadStatus.Created
                        && !string.Equals(previousId, result.AssetId, StringComparison.Ordinal))
                    {
                        replaced = await ReplaceAsync(client, previousId, result.AssetId!, relative, token).ConfigureAwait(false);
                    }
                    RecordUpload(file, after, sha1, result, previousId, replaced);
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

    /// <summary>
    /// Copies albums, favourite etc. from <paramref name="oldId"/> to <paramref name="newId"/> and trashes the old asset,
    /// when the key has <c>asset.copy</c> and <c>asset.delete</c>; otherwise keeps both. True when replaced.
    /// </summary>
    private async Task<bool> ReplaceAsync(IUploadClient client, string oldId, string newId, string relative, CancellationToken token)
    {
        try
        {
            var key = await client.GetMyApiKeyAsync(cancellationToken: token).ConfigureAwait(false);
            if (key.Status != ApiKeyCheckStatus.Ok || key.Key is null)
            {
                WriteLog($"Could not check the API key's permissions ({key.Status}, {key.HttpStatus}); kept both versions of {relative} (old asset {oldId}, new asset {newId}).");
                return false;
            }
            if (!key.Key.CanReplace)
            {
                WriteLog($"Kept both versions of {relative} (old asset {oldId}, new asset {newId}): the API key lacks " +
                         $"'{ApiKeyInfo.PermissionAssetCopy}' and '{ApiKeyInfo.PermissionAssetDelete}', which are needed to replace the old one.");
                return false;
            }

            var copy = await client.CopyAssetAsync(oldId, newId, token).ConfigureAwait(false);
            if (!copy.Succeeded)
            {
                WriteLog($"Could not copy albums and favourite from asset {oldId} to {newId} ({copy.Status}, {copy.HttpStatus}: {copy.Message}); kept both versions of {relative}.");
                return false;
            }
            var trash = await client.TrashAssetsAsync([oldId], token).ConfigureAwait(false);
            if (!trash.Succeeded)
            {
                WriteLog($"Could not move the old asset {oldId} of {relative} to the trash ({trash.Status}, {trash.HttpStatus}: {trash.Message}); kept both versions.");
                return false;
            }
            WriteLog($"Replaced asset {oldId} of {relative} with {newId} (the old one is in the trash).");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            WriteLog($"Could not replace asset {oldId} of {relative} ({ex.Message}); kept both versions.");
            return false;
        }
    }

    private void RecordUpload(Tracked file, FileStat stat, string sha1, UploadResult result, string? previousId, bool replaced)
    {
        var created = result.Status == UploadStatus.Created;
        var update = previousId is not null;
        string path;
        string relative;
        lock (StateLock)
        {
            // Under the lock, so a rename during the upload either already moved the entry or will move this one.
            path = file.Path;
            relative = _state.ToKey(path);
            _state.Set(path, new StateEntry { Size = stat.Length, MtimeUtc = stat.LastWriteUtc, Sha1 = sha1, AssetId = result.AssetId });
            if (!update)
            {
                if (created)
                    _state.Session.Uploaded++;
                else
                    _state.Session.Duplicates++;
                _state.Session.LastFile = relative;
            }
            _state.Heartbeat(_clock.UtcNow);
            SaveStateLocked();
        }

        if (update)
        {
            var text = $"Updated {Path.GetFileName(path)} (VRCX metadata)";
            WriteLog($"{text}: {relative} → asset {result.AssetId} ({(created ? "new asset" : "duplicate")}, was {previousId}).");
            Raise(Updated, new UploadEvent(UploadOutcome.Updated, path, relative, result.AssetId, Detail: text,
                PreviousAssetId: previousId, Replaced: replaced));
            return;
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

    private static bool PathEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

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
