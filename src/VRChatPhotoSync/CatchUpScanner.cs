using System.Security.Cryptography;

namespace VRChatPhotoSync;

/// <summary>What a catch-up scan did.</summary>
/// <param name="FirstRun">True when the state was (re)bound to the folder: the first run, or a different picture folder. Nothing old is uploaded then.</param>
/// <param name="Scanned">Candidate files looked at.</param>
/// <param name="Enqueued">Files put in the upload queue.</param>
/// <param name="FolderMissing">True when the picture folder doesn't exist; <see cref="UploadState.LastScanUtc"/> is then left as it was.</param>
public sealed record CatchUpResult(bool FirstRun, int Scanned, int Enqueued, bool FolderMissing);

/// <summary>The stage of a backfill, for the progress text.</summary>
public enum BackfillPhase
{
    /// <summary>Listing the camera roll and checking for prints and layers.</summary>
    Scanning,
    /// <summary>Computing SHA-1s (<see cref="BackfillProgress.Done"/> of <see cref="BackfillProgress.Total"/>).</summary>
    Hashing,
    /// <summary>Asking the server which files it already has.</summary>
    Checking,
    /// <summary>Finished.</summary>
    Done,
}

/// <summary>Backfill progress (<c>n/N</c>).</summary>
public sealed record BackfillProgress(BackfillPhase Phase, int Done, int Total)
{
    public string Text => Phase switch
    {
        BackfillPhase.Scanning => "Looking for existing photos…",
        BackfillPhase.Hashing => $"Checking existing photos {Done}/{Total}",
        BackfillPhase.Checking => $"Asking the server about {Total} photos…",
        _ => $"Existing photos checked {Done}/{Total}",
    };
}

/// <summary>How a backfill ended.</summary>
public enum BackfillStatus
{
    /// <summary>Finished; the new files are queued.</summary>
    Ok,
    /// <summary>No server or key is set up.</summary>
    NotSetUp,
    /// <summary>The bulk check failed (see <see cref="BackfillResult.Message"/>); nothing was queued.</summary>
    CheckFailed,
}

/// <summary>What a backfill did.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Candidates">Photos that may be uploaded under the current settings (not yet uploaded).</param>
/// <param name="Enqueued">Files the server doesn't have, now queued for upload.</param>
/// <param name="AlreadyPresent">Files the server already has, recorded in the state.</param>
/// <param name="Rejected">Files the server refused for another reason (for example <c>unsupported-format</c>).</param>
/// <param name="Message">The error for <see cref="BackfillStatus.CheckFailed"/>.</param>
public sealed record BackfillResult(BackfillStatus Status, int Candidates, int Enqueued, int AlreadyPresent, int Rejected, string? Message = null);

/// <summary>
/// Finds photos the watcher didn't see.
/// <list type="bullet">
/// <item><b>Catch-up</b> (<see cref="Startup"/>, <see cref="CatchUp"/>): at startup, after the watcher lost events
/// (<see cref="FolderWatcher.ResyncNeeded"/>) and after the picture folder changed
/// (<see cref="VRChatConfigWatcher.PictureFolderChanged"/>), queues every candidate whose
/// <c>max(creation, last write)</c> is later than <see cref="UploadState.LastScanUtc"/> minus <see cref="Overlap"/> and that
/// isn't in the state unchanged, oldest first; then <see cref="UploadState.LastScanUtc"/> becomes the scan's start time.
/// The creation time catches files copied in with an old write time. On the first run, or with a different picture folder,
/// <see cref="UploadState.LastScanUtc"/> starts at now, so the old library isn't mass-uploaded.</item>
/// <item><b>Backfill</b> (<see cref="BackfillAsync"/>, "Upload existing photos"): every candidate minus prints and layers
/// per the settings, hashed and checked with the server's bulk upload check; files it lacks are queued, and files it has
/// are recorded in the state as already present.</item>
/// <item><see cref="CountExistingAsync"/>: how many photos a backfill would look at (for the first-setup checkbox).</item>
/// </list>
/// The <see cref="UploadState"/> is shared with the <see cref="UploadWorker"/>; every access holds the same lock.
/// </summary>
public sealed class CatchUpScanner
{
    /// <summary>How far before <see cref="UploadState.LastScanUtc"/> a catch-up looks, for clock and timestamp slack.</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(1);

    private readonly UploadQueue _queue;
    private readonly UploadState _state;
    private readonly object _stateLock;
    private readonly Action<UploadState> _saveState;
    private readonly Func<AppConfig> _settings;
    private readonly Func<IUploadClient?> _client;
    private readonly IFileProbe _probe;
    private readonly IClock _clock;
    private readonly Func<string, bool> _isPrint;
    private readonly Func<string, string> _hash;
    private readonly object _scanLock = new();
    private readonly SemaphoreSlim _backfillLock = new(1, 1);

    /// <param name="queue">Where found files go.</param>
    /// <param name="state">The shared state.</param>
    /// <param name="stateLock">The lock guarding <paramref name="state"/> (the worker's <see cref="UploadWorker.StateLock"/>).</param>
    /// <param name="saveState">Saves the state; called under <paramref name="stateLock"/>.</param>
    /// <param name="settings">The current settings.</param>
    /// <param name="client">The current Immich client, or null when not set up.</param>
    /// <param name="probe">File sizes and times; defaults to the real file system.</param>
    /// <param name="clock">The time; defaults to the system clock.</param>
    /// <param name="isPrint">Print detection; defaults to <see cref="PrintDetector.IsPrint(string)"/>.</param>
    /// <param name="hash">SHA-1 (hex) of a file; defaults to a shared streaming read.</param>
    public CatchUpScanner(
        UploadQueue queue,
        UploadState state,
        object stateLock,
        Action<UploadState> saveState,
        Func<AppConfig> settings,
        Func<IUploadClient?> client,
        IFileProbe? probe = null,
        IClock? clock = null,
        Func<string, bool>? isPrint = null,
        Func<string, string>? hash = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _stateLock = stateLock ?? throw new ArgumentNullException(nameof(stateLock));
        _saveState = saveState ?? throw new ArgumentNullException(nameof(saveState));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _probe = probe ?? FileProbe.Instance;
        _clock = clock ?? SystemClock.Instance;
        _isPrint = isPrint ?? PrintDetector.IsPrint;
        _hash = hash ?? Sha1OfFile;
    }

    /// <summary>A line for the log.</summary>
    public event Action<string>? Log;

    /// <summary>
    /// The startup catch-up: ties the state to <paramref name="pictureFolder"/> (a first run or a different folder sets
    /// <see cref="UploadState.LastScanUtc"/> to now, see <see cref="UploadState.BindToFolder"/>), then runs
    /// <see cref="CatchUp"/>.
    /// </summary>
    public CatchUpResult Startup(string pictureFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pictureFolder);
        lock (_scanLock)
        {
            var firstRun = Bind(pictureFolder);
            return CatchUpLocked(firstRun);
        }
    }

    /// <summary>
    /// VRChat's picture folder changed: the state moves to <paramref name="newFolder"/> as on a first run (no mass upload
    /// of what is already there), then a catch-up runs.
    /// </summary>
    public CatchUpResult PictureFolderChanged(string newFolder) => Startup(newFolder);

    /// <summary>
    /// Queues every candidate changed since <see cref="UploadState.LastScanUtc"/> − <see cref="Overlap"/> that isn't in the
    /// state unchanged, oldest first, then sets <see cref="UploadState.LastScanUtc"/> to the time the scan started.
    /// Used after <see cref="FolderWatcher.ResyncNeeded"/>. Scans never overlap.
    /// </summary>
    public CatchUpResult CatchUp()
    {
        lock (_scanLock)
            return CatchUpLocked(firstRun: false);
    }

    /// <summary>Runs <see cref="CatchUp"/> in the background (for event handlers); errors are logged.</summary>
    public Task<CatchUpResult?> CatchUpInBackground() => Task.Run<CatchUpResult?>(() =>
    {
        try
        {
            return CatchUp();
        }
        catch (Exception ex)
        {
            WriteLog($"Catch-up scan failed: {ex.Message}");
            return null;
        }
    });

    /// <summary>Runs a catch-up whenever the watcher lost events.</summary>
    public void Attach(FolderWatcher watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        watcher.ResyncNeeded += (_, _) =>
        {
            WriteLog("The folder watcher missed events; scanning for new photos.");
            _ = CatchUpInBackground();
        };
    }

    /// <summary>Follows VRChat's picture folder: moves the state to the new folder and runs a catch-up there.</summary>
    public void Attach(VRChatConfigWatcher watcher)
    {
        ArgumentNullException.ThrowIfNull(watcher);
        watcher.PictureFolderChanged += (_, e) => _ = Task.Run(() =>
        {
            try
            {
                WriteLog($"VRChat's picture folder changed to {e.NewSettings.PictureFolder}; scanning there.");
                PictureFolderChanged(e.NewSettings.PictureFolder);
            }
            catch (Exception ex)
            {
                WriteLog($"Catch-up scan after the folder change failed: {ex.Message}");
            }
        });
    }

    private bool Bind(string pictureFolder)
    {
        lock (_stateLock)
        {
            var rebound = _state.BindToFolder(pictureFolder, _clock.UtcNow);
            if (rebound)
            {
                WriteLog($"Picture folder {_state.PictureFolder} is new to the state; only photos from now on are uploaded.");
                Save();
            }
            return rebound;
        }
    }

    private CatchUpResult CatchUpLocked(bool firstRun)
    {
        var start = _clock.UtcNow;
        string? root;
        DateTime? lastScan;
        lock (_stateLock)
        {
            root = _state.PictureFolder;
            lastScan = _state.LastScanUtc;
        }
        if (root is null || lastScan is null)
        {
            WriteLog("Catch-up skipped: no picture folder yet.");
            return new CatchUpResult(firstRun, 0, 0, FolderMissing: true);
        }
        if (!Directory.Exists(root))
        {
            WriteLog($"Catch-up skipped: {root} doesn't exist.");
            return new CatchUpResult(firstRun, 0, 0, FolderMissing: true);
        }

        var since = UploadState.ToUtc(lastScan.Value) - Overlap;
        var strict = _settings().StrictFileNames;
        var found = new List<(string Path, DateTime Newest)>();
        var scanned = 0;
        foreach (var path in PhotoFilter.EnumerateCandidates(root, strict))
        {
            scanned++;
            if (_probe.TryStat(path) is not { } stat || stat.NewestUtc <= since)
                continue;
            bool unchanged;
            lock (_stateLock) unchanged = _state.IsUnchanged(path, stat.Length, stat.LastWriteUtc);
            if (!unchanged)
                found.Add((path, stat.NewestUtc));
        }

        var enqueued = 0;
        foreach (var (path, _) in found.OrderBy(f => f.Newest).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (_queue.Enqueue(path))
                enqueued++;
        }

        lock (_stateLock)
        {
            // Only move forward, and only if the folder is still the one that was scanned.
            if (string.Equals(_state.PictureFolder, root, StringComparison.OrdinalIgnoreCase)
                && (_state.LastScanUtc is null || UploadState.ToUtc(_state.LastScanUtc.Value) <= start))
            {
                _state.LastScanUtc = start;
                Save();
            }
        }
        WriteLog($"Catch-up scan of {root}: {scanned} photos, {enqueued} queued (changed since {since:u}).");
        return new CatchUpResult(firstRun, scanned, enqueued, FolderMissing: false);
    }

    // ---- Backfill ----

    /// <summary>
    /// How many existing photos a backfill would look at under the current settings: the candidates, minus prints and
    /// layers when those are skipped, minus files already uploaded. Doesn't contact the server. Runs in the background.
    /// </summary>
    public Task<int> CountExistingAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => FindExisting(null, cancellationToken).Count, cancellationToken);

    /// <summary>
    /// "Upload existing photos": every candidate minus prints and layers per the settings (and minus files already
    /// uploaded) is hashed, then checked with the server's bulk upload check. Files the server lacks are queued; files it
    /// already has are recorded in the state with their asset id. Reports progress as <c>n/N</c>. Only one backfill runs
    /// at a time (a second call waits).
    /// </summary>
    public async Task<BackfillResult> BackfillAsync(IProgress<BackfillProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        await _backfillLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => BackfillCoreAsync(progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _backfillLock.Release();
        }
    }

    private async Task<BackfillResult> BackfillCoreAsync(IProgress<BackfillProgress>? progress, CancellationToken token)
    {
        var client = _client();
        if (client is null)
        {
            WriteLog("Upload existing photos: not set up (no server or key).");
            return new BackfillResult(BackfillStatus.NotSetUp, 0, 0, 0, 0, "No server or API key is set up.");
        }

        Report(progress, BackfillPhase.Scanning, 0, 0);
        var candidates = FindExisting(progress, token);
        var total = candidates.Count;
        WriteLog($"Upload existing photos: {total} photos to check.");

        // Hash each file; a file that changes while it is hashed is simply queued (the worker handles it).
        var hashed = new List<(string Path, string Key, FileStat Stat, string Sha1)>(total);
        var toQueue = new List<string>();
        var done = 0;
        Report(progress, BackfillPhase.Hashing, 0, total);
        foreach (var path in candidates)
        {
            token.ThrowIfCancellationRequested();
            var before = _probe.TryStat(path);
            if (before is { } b)
            {
                string? sha1 = null;
                try
                {
                    sha1 = _hash(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    WriteLog($"Could not read {path} ({ex.Message}); queuing it instead.");
                }
                var after = _probe.TryStat(path);
                if (sha1 is not null && after is { } a && a.SameContentAs(b))
                {
                    string key;
                    lock (_stateLock) key = _state.ToKey(path);
                    hashed.Add((path, key, a, sha1));
                }
                else if (after is not null)
                {
                    toQueue.Add(path);
                }
            }
            Report(progress, BackfillPhase.Hashing, ++done, total);
        }

        Report(progress, BackfillPhase.Checking, 0, hashed.Count);
        IReadOnlyList<BulkCheckResult> results = [];
        if (hashed.Count > 0)
        {
            var items = hashed.Select(h => new BulkCheckItem(h.Key, h.Sha1)).ToList();
            ApiResult<IReadOnlyList<BulkCheckResult>> check;
            try
            {
                check = await client.BulkUploadCheckAsync(items, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                check = ApiResult<IReadOnlyList<BulkCheckResult>>.Fail(ApiStatus.Transient, null, ex.Message);
            }
            if (!check.Succeeded)
            {
                var message = $"The server's bulk upload check failed ({check.Status}{(check.HttpStatus is { } code ? $", HTTP {code}" : "")}: {check.Message}).";
                WriteLog($"Upload existing photos: {message} Nothing was queued.");
                return new BackfillResult(BackfillStatus.CheckFailed, total, 0, 0, 0, message);
            }
            results = check.Value!;
        }

        var byKey = hashed.ToDictionary(h => h.Key, StringComparer.OrdinalIgnoreCase);
        var present = 0;
        var rejected = 0;
        lock (_stateLock)
        {
            foreach (var r in results)
            {
                if (!byKey.Remove(r.Id, out var h))
                    continue;
                if (r.Accepted)
                {
                    toQueue.Add(h.Path);
                }
                else if (r.IsDuplicate && !string.IsNullOrEmpty(r.AssetId))
                {
                    _state.Set(h.Path, new StateEntry { Size = h.Stat.Length, MtimeUtc = h.Stat.LastWriteUtc, Sha1 = h.Sha1, AssetId = r.AssetId });
                    present++;
                }
                else
                {
                    rejected++;
                    WriteLog($"Upload existing photos: the server won't take {h.Key} ({r.Reason ?? "rejected"}).");
                }
            }
            // Anything the server didn't answer for is queued; the upload's own checksum check handles it.
            toQueue.AddRange(byKey.Values.Select(h => h.Path));

            // A file skipped earlier as a print or layer is wanted now (the settings changed): drop the skip entry so the
            // worker doesn't treat it as unchanged.
            foreach (var path in toQueue)
            {
                if (_state.Get(path) is { Skipped: not null })
                    _state.Remove(path);
            }
            if (present > 0 || toQueue.Count > 0)
                Save();
        }

        var enqueued = 0;
        foreach (var path in toQueue)
        {
            if (_queue.Enqueue(path) || _queue.Contains(path))
                enqueued++;
        }
        Report(progress, BackfillPhase.Done, total, total);
        WriteLog($"Upload existing photos: {enqueued} queued, {present} already on the server, {rejected} refused.");
        return new BackfillResult(BackfillStatus.Ok, total, enqueued, present, rejected);
    }

    /// <summary>
    /// The candidates a backfill looks at: accepted by the filter, not a layer (unless layers are uploaded), not a print
    /// (unless prints are uploaded), and not already uploaded unchanged. Sorted by path.
    /// </summary>
    private List<string> FindExisting(IProgress<BackfillProgress>? progress, CancellationToken token)
    {
        var settings = _settings();
        string? root;
        lock (_stateLock) root = _state.PictureFolder;
        var list = new List<string>();
        if (root is null)
            return list;

        foreach (var path in PhotoFilter.EnumerateCandidates(root, settings.StrictFileNames))
        {
            token.ThrowIfCancellationRequested();
            if (!settings.UploadLayers && VrcFileName.IsLayer(path))
                continue;
            if (_probe.TryStat(path) is not { } stat)
                continue;
            bool uploaded;
            lock (_stateLock)
                uploaded = _state.Get(path) is { Skipped: null } && _state.IsUnchanged(path, stat.Length, stat.LastWriteUtc);
            if (uploaded)
                continue;
            if (settings.ExcludePrints && IsPrintSafe(path))
                continue;
            list.Add(path);
            if (list.Count % 100 == 0)
                Report(progress, BackfillPhase.Scanning, list.Count, 0);
        }
        return list;
    }

    private bool IsPrintSafe(string path)
    {
        try
        {
            return _isPrint(path);
        }
        catch (Exception ex)
        {
            WriteLog($"Print check failed for {path} ({ex.Message}); treating it as a photo.");
            return false;
        }
    }

    // ---- Helpers ----

    private void Save()
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

    private static void Report(IProgress<BackfillProgress>? progress, BackfillPhase phase, int done, int total)
    {
        try { progress?.Report(new BackfillProgress(phase, done, total)); }
        catch { /* a UI handler must never break the scan */ }
    }

    private void WriteLog(string message)
    {
        try { Log?.Invoke(message); }
        catch { /* logging must never break the scan */ }
    }

    private static string Sha1OfFile(string path)
    {
        using var stream = PngChunks.OpenShared(path);
        return Convert.ToHexStringLower(SHA1.HashData(stream));
    }
}
