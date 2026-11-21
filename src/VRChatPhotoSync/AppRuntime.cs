using Timer = System.Threading.Timer;

namespace VRChatPhotoSync;

/// <summary>
/// The running pipeline: the state (with the session resolved), the queue, the upload worker, the catch-up scanner, the
/// folder watcher (re-created when VRChat's picture folder changes), and the heartbeat. <c>Program.Main</c> builds one,
/// starts it, and stops it gracefully when the app exits normally.
/// <para>
/// VRCX ends the app with a hard kill, so nothing here relies on <see cref="StopAsync"/> running: the state is saved
/// after every step, the heartbeat lets the next start continue the session, and the startup catch-up finds whatever
/// was still pending.
/// </para>
/// </summary>
public sealed class AppRuntime : IDisposable
{
    /// <summary>How often the heartbeat is saved (it is also refreshed after each upload).</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long a graceful stop gives the current upload.</summary>
    public static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);

    /// <summary>How long a stop waits for the worker after cancelling it.</summary>
    private static readonly TimeSpan CancelWait = TimeSpan.FromSeconds(2);

    private readonly Func<DateTime> _utcNow;
    private readonly Action<string> _log;
    private readonly Action<string> _warn;
    private readonly TimeSpan? _watcherDebounce;
    private readonly object _lifecycleLock = new();
    private readonly CancellationTokenSource _cts = new();
    private FolderWatcher? _watcher;
    private VRChatConfigWatcher? _configWatcher;
    private Timer? _heartbeatTimer;
    private Task _workerTask = Task.CompletedTask;
    private bool _started;
    private bool _stopping;
    private bool _disposed;

    /// <summary>
    /// Loads the state, resolves the session (continued when the last heartbeat is under 2 minutes old) and builds the
    /// pipeline. Nothing runs until <see cref="Start"/>.
    /// </summary>
    /// <param name="stateStore">Where <c>state.json</c> lives.</param>
    /// <param name="pictureFolder">The camera roll root.</param>
    /// <param name="settings">The current settings (read for every file).</param>
    /// <param name="client">The current Immich client, or null when not set up.</param>
    /// <param name="utcNow">The time for the session and heartbeat; defaults to the system clock.</param>
    /// <param name="log">Receives info lines.</param>
    /// <param name="warn">Receives warnings.</param>
    /// <param name="watcherDebounce">The folder watcher's debounce (tests shorten it).</param>
    public AppRuntime(
        UploadStateStore stateStore,
        string pictureFolder,
        Func<AppConfig> settings,
        Func<IUploadClient?> client,
        Func<DateTime>? utcNow = null,
        Action<string>? log = null,
        Action<string>? warn = null,
        TimeSpan? watcherDebounce = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(pictureFolder);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(client);
        StateStore = stateStore;
        PictureFolder = pictureFolder;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _log = log ?? (_ => { });
        _warn = warn ?? _log;
        _watcherDebounce = watcherDebounce;

        State = stateStore.Load(_warn);
        SessionContinued = State.ResolveSession(_utcNow());
        _log(SessionContinued
            ? $"Continuing the session started {State.Session.StartUtc:u} ({State.Session.Uploaded} uploaded so far)."
            : "Starting a new session.");
        SaveState(State); // records the heartbeat straight away, so a quick kill-and-relaunch continues too

        Queue = new UploadQueue();
        Worker = new UploadWorker(Queue, State, SaveState, settings, client);
        Scanner = new CatchUpScanner(Queue, State, Worker.StateLock, SaveState, settings, client);
    }

    public UploadStateStore StateStore { get; }

    /// <summary>The shared state; hold <see cref="UploadWorker.StateLock"/> when touching it.</summary>
    public UploadState State { get; }

    /// <summary>True when the previous session's counters carried over.</summary>
    public bool SessionContinued { get; }

    public UploadQueue Queue { get; }

    public UploadWorker Worker { get; }

    public CatchUpScanner Scanner { get; }

    /// <summary>The camera roll root being watched (follows VRChat's setting).</summary>
    public string PictureFolder { get; private set; }

    /// <summary>The current folder watcher (null before <see cref="Start"/>).</summary>
    public FolderWatcher? Watcher
    {
        get { lock (_lifecycleLock) return _watcher; }
    }

    /// <summary>The worker's task (completes after <see cref="StopAsync"/>).</summary>
    public Task WorkerTask
    {
        get { lock (_lifecycleLock) return _workerTask; }
    }

    /// <summary>A new folder watcher replaced the old one (VRChat's picture folder changed). Raised on a thread-pool thread.</summary>
    public event Action<FolderWatcher>? WatcherChanged;

    /// <summary>
    /// Starts the folder watcher, the startup catch-up followed by the worker (so the state is bound to the folder before
    /// the first file is checked), VRChat's config watcher if given, and the heartbeat.
    /// </summary>
    /// <param name="configWatcher">Follows VRChat's picture folder; null when <c>--folder</c> fixes it. Owned from here on.</param>
    public void Start(VRChatConfigWatcher? configWatcher = null)
    {
        FolderWatcher watcher;
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                throw new InvalidOperationException("Already started.");
            _started = true;
            watcher = _watcher = CreateWatcher(PictureFolder);
        }
        watcher.Start();

        var token = _cts.Token;
        var folder = PictureFolder;
        var task = Task.Run(async () =>
        {
            try
            {
                Scanner.Startup(folder);
            }
            catch (Exception ex)
            {
                _warn($"Startup catch-up failed: {ex.Message}");
            }
            await Worker.RunAsync(token).ConfigureAwait(false);
        }, CancellationToken.None);
        lock (_lifecycleLock)
            _workerTask = task;

        if (configWatcher is not null)
        {
            lock (_lifecycleLock)
                _configWatcher = configWatcher;
            Scanner.Attach(configWatcher);
            configWatcher.PictureFolderChanged += (_, e) => SwitchFolder(e.NewSettings.PictureFolder);
            configWatcher.Start();
        }

        lock (_lifecycleLock)
            _heartbeatTimer = new Timer(_ => Heartbeat(), null, HeartbeatInterval, HeartbeatInterval);
    }

    /// <summary>Refreshes the heartbeat and saves the state (every 30 s; the worker also does it after each upload).</summary>
    public void Heartbeat()
    {
        try
        {
            lock (Worker.StateLock)
            {
                State.Heartbeat(_utcNow());
                StateStore.Save(State);
            }
        }
        catch (Exception ex)
        {
            _warn($"Could not save the heartbeat: {ex.Message}");
        }
    }

    /// <summary>
    /// A normal exit (the user, or Windows shutting down): stops taking new files, drops what is still queued (the next
    /// start's catch-up finds it again), gives the file being uploaded up to <paramref name="grace"/> to finish, then
    /// cancels and saves the state. Safe to call more than once.
    /// </summary>
    public async Task StopAsync(TimeSpan? grace = null)
    {
        Task workerTask;
        FolderWatcher? watcher;
        VRChatConfigWatcher? configWatcher;
        Timer? timer;
        lock (_lifecycleLock)
        {
            if (_stopping || _disposed)
                return;
            _stopping = true;
            workerTask = _workerTask;
            watcher = _watcher;
            configWatcher = _configWatcher;
            timer = _heartbeatTimer;
            _heartbeatTimer = null;
        }

        // 1. Stop intake.
        timer?.Dispose();
        configWatcher?.Dispose();
        watcher?.Dispose();
        var dropped = 0;
        while (Queue.TryDequeue(out _))
            dropped++;
        Queue.Complete();
        if (dropped > 0)
            _log($"Stopping: {dropped} queued files left for the next start.");

        // 2. Let the current upload finish, up to the grace period.
        var limit = grace ?? StopGrace;
        if (!workerTask.IsCompleted
            && await Task.WhenAny(workerTask, Task.Delay(limit)).ConfigureAwait(false) != workerTask)
        {
            _log($"Stopping: the current upload didn't finish within {limit.TotalSeconds:0.#} s; cancelling it (it is redone at the next start).");
            await _cts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAny(workerTask, Task.Delay(CancelWait)).ConfigureAwait(false);
        }
        if (workerTask.IsFaulted)
            _warn($"The upload worker failed: {workerTask.Exception?.GetBaseException().Message}");

        // 3. Save the state.
        Heartbeat();
    }

    private FolderWatcher CreateWatcher(string folder)
    {
        var watcher = new FolderWatcher(folder, _watcherDebounce, log: _log);
        watcher.FileAppeared += path => Queue.Enqueue(path);
        watcher.FileRenamed += (oldPath, newPath) => Worker.NotifyRenamed(oldPath, newPath);
        Scanner.Attach(watcher);
        return watcher;
    }

    /// <summary>VRChat's picture folder changed: watch the new one (the scanner moves the state and scans there).</summary>
    internal void SwitchFolder(string newFolder)
    {
        if (string.IsNullOrWhiteSpace(newFolder))
            return;
        FolderWatcher? old;
        FolderWatcher next;
        lock (_lifecycleLock)
        {
            if (_stopping || _disposed || !_started)
                return;
            old = _watcher;
            next = CreateWatcher(newFolder);
            _watcher = next;
            PictureFolder = newFolder;
        }
        old?.Dispose();
        next.Start();
        _log($"Now watching {newFolder}.");
        WatcherChanged?.Invoke(next);
    }

    private void SaveState(UploadState state)
    {
        try
        {
            StateStore.Save(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _warn($"Could not save the state file ({ex.Message}).");
        }
    }

    /// <summary>Stops everything at once, without the grace period or a final save (use <see cref="StopAsync"/> first).</summary>
    public void Dispose()
    {
        FolderWatcher? watcher;
        VRChatConfigWatcher? configWatcher;
        Timer? timer;
        lock (_lifecycleLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            watcher = _watcher;
            configWatcher = _configWatcher;
            timer = _heartbeatTimer;
            _heartbeatTimer = null;
        }
        timer?.Dispose();
        configWatcher?.Dispose();
        watcher?.Dispose();
        Queue.Complete();
        _cts.Cancel();
    }
}
