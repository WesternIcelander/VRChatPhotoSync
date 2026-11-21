namespace VRChatPhotoSync;

/// <summary>Everything the main window shows, worked out by <see cref="MainViewModel"/>. Immutable.</summary>
/// <param name="Title">The window title, <c>PhotoSync — N uploaded</c>.</param>
/// <param name="StatusText">The status line.</param>
/// <param name="StatusToolTip">More detail for the status line (the folder, or the file being uploaded), or null.</param>
/// <param name="CountersText"><c>Uploaded this session: N</c>, plus duplicates and failures if any.</param>
/// <param name="LastText"><c>Last: &lt;file&gt;</c>, or <c>Last: —</c>.</param>
/// <param name="LastFilePath">The full path of the last file (for the tooltip and "select in Explorer"), or null.</param>
/// <param name="QueueText"><c>Queued: N</c>, or null when the queue is empty.</param>
/// <param name="SkipPrintsText">The Skip prints checkbox text, with the session's count.</param>
/// <param name="UploadLayersText">The Upload photo layers checkbox text, with the count when there is one.</param>
/// <param name="ExcludePrints">The Skip prints checkbox state.</param>
/// <param name="UploadLayers">The Upload photo layers checkbox state.</param>
public sealed record MainView(
    string Title,
    string StatusText,
    string? StatusToolTip,
    string CountersText,
    string LastText,
    string? LastFilePath,
    string? QueueText,
    string SkipPrintsText,
    string UploadLayersText,
    bool ExcludePrints,
    bool UploadLayers);

/// <summary>
/// The main window's presentation logic: the status line, the session counters, the last file, the queue length and the
/// checkbox texts. The window only feeds it events (from the worker, queue, watcher and settings) and shows
/// <see cref="Snapshot"/> when <see cref="Changed"/> is raised.
/// <para>
/// Thread-safe: inputs may come from any thread. <see cref="Changed"/> is raised on the caller's thread, so the window
/// must marshal it (<c>BeginInvoke</c>).
/// </para>
/// </summary>
public sealed class MainViewModel
{
    public const string AppTitle = "PhotoSync";
    public const string NotSetUpText = "Not set up — click Immich server…";
    public const string KeyRejectedText = "Key rejected — uploads paused";
    public const string WatchingText = "Watching for new photos";

    private readonly object _lock = new();
    private readonly Func<DateTime> _utcNow;

    private string? _pictureFolder;
    private bool _setUp;
    private FolderWatchState? _folderState;
    private WorkerStatus _worker = WorkerStatus.Idle;
    private DateTime? _retryUntilUtc;
    private BackfillProgress? _backfill;
    private string? _notice;

    private int _uploaded;
    private int _duplicates;
    private int _failed;
    private int _printsSkipped;
    private int _layersSkipped;
    private string? _lastFile;
    private string? _lastDetail;
    private int _queue;
    private bool _excludePrints;
    private bool _uploadLayers;

    /// <param name="config">The settings (for the checkboxes and whether the server is set up).</param>
    /// <param name="pictureFolder">The camera roll root, or null if unknown.</param>
    /// <param name="utcNow">The clock (for the retry countdown); <see cref="DateTime.UtcNow"/> if null.</param>
    public MainViewModel(AppConfig config, string? pictureFolder = null, Func<DateTime>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _pictureFolder = pictureFolder;
        ApplyConfig(config);
    }

    /// <summary>Raised after anything shown changed, on the thread that made the change.</summary>
    public event Action? Changed;

    /// <summary>True while a retry countdown is running, so the window should call <see cref="Tick"/> every second.</summary>
    public bool NeedsTick
    {
        get { lock (_lock) return _retryUntilUtc is not null; }
    }

    /// <summary>True when a server URL and an API key are set.</summary>
    public static bool IsSetUp(AppConfig config) =>
        ConfigStore.IsValidServerUrl(config.ServerUrl) && !string.IsNullOrWhiteSpace(config.ApiKeyProtected);

    /// <summary>
    /// The Immich web address for a server URL: the API base without its <c>/api</c> (for "Open Immich"), or null when
    /// the URL isn't valid.
    /// </summary>
    public static string? ServerRoot(string? serverUrl)
    {
        var normalized = ConfigStore.NormalizeServerUrl(serverUrl);
        if (!ConfigStore.IsValidServerUrl(normalized))
            return null;
        return normalized!.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? normalized[..^4] + "/" : normalized + "/";
    }

    // ---- Inputs ----

    /// <summary>The settings changed: checkboxes and the set-up state follow.</summary>
    public void ApplyConfig(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_lock)
        {
            _setUp = IsSetUp(config);
            _excludePrints = config.ExcludePrints;
            _uploadLayers = config.UploadLayers;
        }
        OnChanged();
    }

    /// <summary>
    /// Starts from the counters of a continued session (see <see cref="UploadState.ResolveSession"/>).
    /// <see cref="SessionInfo.LastFile"/> is relative to <see cref="PictureFolder"/>.
    /// </summary>
    public void LoadSession(SessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_lock)
        {
            _uploaded = session.Uploaded;
            _duplicates = session.Duplicates;
            _failed = session.Failed;
            _printsSkipped = session.PrintsSkipped;
            _layersSkipped = session.LayersSkipped;
            _lastDetail = null;
            _lastFile = string.IsNullOrEmpty(session.LastFile)
                ? null
                : _pictureFolder is null || Path.IsPathRooted(session.LastFile)
                    ? session.LastFile
                    : Path.GetFullPath(Path.Combine(_pictureFolder, session.LastFile));
        }
        OnChanged();
    }

    /// <summary>The camera roll root (for the status tooltip and relative session paths).</summary>
    public string? PictureFolder
    {
        get { lock (_lock) return _pictureFolder; }
        set
        {
            lock (_lock) _pictureFolder = value;
            OnChanged();
        }
    }

    /// <summary>The folder watcher's state; <see cref="FolderWatchState.WaitingForFolder"/> shows in the status line.</summary>
    public void SetFolderState(FolderWatchState state, string? folder = null)
    {
        lock (_lock)
        {
            _folderState = state;
            if (folder is not null)
                _pictureFolder = folder;
        }
        OnChanged();
    }

    /// <summary>The worker's status (<see cref="UploadWorker.StatusChanged"/>).</summary>
    public void SetWorkerStatus(WorkerStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_lock)
        {
            _worker = status;
            _retryUntilUtc = status.State == WorkerState.Retrying && status.RetryIn is { } wait ? _utcNow() + wait : null;
        }
        OnChanged();
    }

    /// <summary>The upload queue's length (<see cref="UploadQueue.CountChanged"/>).</summary>
    public void SetQueueLength(int count)
    {
        lock (_lock)
        {
            if (_queue == Math.Max(0, count))
                return;
            _queue = Math.Max(0, count);
        }
        OnChanged();
    }

    /// <summary>"Upload existing photos" progress, or null when it is not running.</summary>
    public void SetBackfill(BackfillProgress? progress)
    {
        lock (_lock)
            _backfill = progress is { Phase: BackfillPhase.Done } ? null : progress;
        OnChanged();
    }

    /// <summary>A message that replaces the normal status line (for example an error), or null to clear it.</summary>
    public void SetNotice(string? notice)
    {
        lock (_lock)
            _notice = string.IsNullOrWhiteSpace(notice) ? null : notice;
        OnChanged();
    }

    /// <summary>One file's outcome from the worker: updates the counters and the last file.</summary>
    public void Record(UploadEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        lock (_lock)
        {
            switch (evt.Outcome)
            {
                case UploadOutcome.Uploaded:
                    _uploaded++;
                    SetLast(evt.Path, null);
                    break;
                case UploadOutcome.Duplicate:
                    _duplicates++;
                    SetLast(evt.Path, null);
                    break;
                case UploadOutcome.Failed:
                    _failed++;
                    break;
                case UploadOutcome.Skipped when evt.Skip == SkipReason.Print:
                    _printsSkipped++;
                    break;
                case UploadOutcome.Skipped when evt.Skip == SkipReason.Layer:
                    _layersSkipped++;
                    break;
                case UploadOutcome.Updated:
                    // Not a new upload: show what happened instead.
                    SetLast(evt.Path, evt.Detail ?? $"Updated {Path.GetFileName(evt.Path)} (VRCX metadata)");
                    break;
                default:
                    return;
            }
        }
        OnChanged();
    }

    /// <summary>A file was renamed: the last file follows it.</summary>
    public void NotifyRenamed(string oldPath, string newPath)
    {
        lock (_lock)
        {
            if (_lastFile is null || !string.Equals(_lastFile, oldPath, StringComparison.OrdinalIgnoreCase))
                return;
            _lastFile = newPath;
        }
        OnChanged();
    }

    /// <summary>Called every second by the window; refreshes the retry countdown.</summary>
    public void Tick()
    {
        if (NeedsTick)
            OnChanged();
    }

    // ---- Output ----

    /// <summary>What to show now.</summary>
    public MainView Snapshot()
    {
        lock (_lock)
        {
            var (status, tip) = Status();
            return new MainView(
                Title: $"{AppTitle} — {_uploaded} uploaded",
                StatusText: status,
                StatusToolTip: tip,
                CountersText: Counters(),
                LastText: "Last: " + (_lastDetail ?? (_lastFile is null ? "—" : Path.GetFileName(_lastFile))),
                LastFilePath: _lastFile,
                QueueText: _queue > 0 ? $"Queued: {_queue}" : null,
                SkipPrintsText: $"Skip prints ({_printsSkipped} skipped)",
                UploadLayersText: _layersSkipped > 0 ? $"Upload photo layers ({_layersSkipped} skipped)" : "Upload photo layers",
                ExcludePrints: _excludePrints,
                UploadLayers: _uploadLayers);
        }
    }

    private (string Text, string? ToolTip) Status()
    {
        if (_notice is not null)
            return (_notice, null);
        if (!_setUp || _worker.State == WorkerState.PausedNotSetUp)
            return (NotSetUpText, null);
        if (_worker.State == WorkerState.PausedAuth)
            return (KeyRejectedText, "Open Immich server… to enter a new key.");
        switch (_worker.State)
        {
            case WorkerState.Retrying:
                var left = _retryUntilUtc is { } until ? Math.Ceiling((until - _utcNow()).TotalSeconds) : 0;
                return (left >= 1 ? $"Retrying in {left:0}s" : "Retrying…", _worker.File);
            case WorkerState.Uploading:
                return ($"Uploading {Path.GetFileName(_worker.File) ?? "…"}", _worker.File);
        }
        if (_backfill is not null)
            return (_backfill.Text, null);
        if (_worker.State == WorkerState.WaitingForVrcx || _worker.Waiting > 0)
            return ($"Waiting for VRCX ({Math.Max(1, _worker.Waiting)})", null);
        if (_folderState == FolderWatchState.WaitingForFolder)
            return ($"Waiting for folder {FolderName()}…", $"{_pictureFolder} doesn't exist yet. It is checked every 30 s.");
        if (_folderState == FolderWatchState.Stopped)
            return ("Stopped", _pictureFolder);
        return (WatchingText, _pictureFolder);
    }

    private string FolderName()
    {
        if (string.IsNullOrEmpty(_pictureFolder))
            return "";
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(_pictureFolder));
        return string.IsNullOrEmpty(name) ? _pictureFolder : name;
    }

    private string Counters()
    {
        var text = $"Uploaded this session: {_uploaded}";
        var extras = new List<string>(2);
        if (_duplicates > 0)
            extras.Add(_duplicates == 1 ? "1 duplicate" : $"{_duplicates} duplicates");
        if (_failed > 0)
            extras.Add($"{_failed} failed");
        return extras.Count > 0 ? $"{text} ({string.Join(", ", extras)})" : text;
    }

    private void SetLast(string path, string? detail)
    {
        _lastFile = path;
        _lastDetail = detail;
    }

    private void OnChanged() => Changed?.Invoke();
}
