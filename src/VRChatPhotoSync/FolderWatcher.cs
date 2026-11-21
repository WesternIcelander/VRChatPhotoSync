using Timer = System.Threading.Timer;

namespace VRChatPhotoSync;

/// <summary>What a <see cref="FolderWatcher"/> is doing right now.</summary>
public enum FolderWatchState
{
    Stopped,
    /// <summary>The folder doesn't exist yet; it is checked again every poll interval.</summary>
    WaitingForFolder,
    Watching,
}

/// <summary>
/// Watches the camera roll root (and every subfolder, so both month folders and the root are
/// covered) and reports files that appear or change, after a short quiet period.
/// <list type="bullet">
/// <item><see cref="FileAppeared"/>: a file was created or written, then stayed quiet for the debounce delay.</item>
/// <item><see cref="FileRenamed"/>: raised straight away. A pending <see cref="FileAppeared"/> for the old
/// name is dropped, and <see cref="FileAppeared"/> follows for the new name, so the short-lived original
/// name of a VRCX rename is never reported on its own.</item>
/// <item><see cref="ResyncNeeded"/>: events may have been lost (watcher error, or the folder appeared
/// after waiting for it); the caller should run a catch-up scan.</item>
/// </list>
/// A missing folder is never created: the watcher reports <see cref="FolderWatchState.WaitingForFolder"/>
/// and checks again every poll interval. Events are raised on thread-pool threads.
/// Filtering (which files are photos) is the caller's job.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(30);

    /// <summary>The watcher's internal buffer: 64 KB, the most Windows allows for network paths.</summary>
    public const int BufferSize = 64 * 1024;

    public const NotifyFilters WatchedChanges = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size;

    private readonly object _lock = new();
    private readonly TimeSpan _pollInterval;
    private readonly Action<string>? _log;
    private readonly Debouncer<string> _debouncer;
    private FileSystemWatcher? _watcher;
    private Timer? _pollTimer;
    private FolderWatchState _state = FolderWatchState.Stopped;
    private bool _disposed;

    /// <param name="root">The camera roll root. Never created by this class.</param>
    /// <param name="debounce">Quiet period before <see cref="FileAppeared"/>; default 500 ms.</param>
    /// <param name="pollInterval">How often a missing folder is looked for (and a watched one checked); default 30 s.</param>
    /// <param name="log">Receives log lines.</param>
    public FolderWatcher(string root, TimeSpan? debounce = null, TimeSpan? pollInterval = null, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _pollInterval = pollInterval is { } p && p > TimeSpan.Zero ? p : DefaultPollInterval;
        _log = log;
        _debouncer = new Debouncer<string>(debounce ?? DefaultDebounce, RaiseAppeared, StringComparer.OrdinalIgnoreCase, log);
    }

    /// <summary>The folder being watched (full path).</summary>
    public string Root { get; }

    public FolderWatchState State
    {
        get { lock (_lock) return _state; }
    }

    /// <summary>A short status line for the UI, e.g. <c>Waiting for D:\Photos\VRChat to exist…</c>.</summary>
    public string StatusText => State switch
    {
        FolderWatchState.WaitingForFolder => $"Waiting for {Root} to exist…",
        FolderWatchState.Watching => $"Watching {Root}",
        _ => "Stopped",
    };

    /// <summary>A file was created or changed and has been quiet for the debounce delay. Full path.</summary>
    public event Action<string>? FileAppeared;

    /// <summary>A file was renamed (old full path, new full path).</summary>
    public event Action<string, string>? FileRenamed;

    /// <summary>Events may have been missed; run a catch-up scan.</summary>
    public event EventHandler? ResyncNeeded;

    /// <summary><see cref="State"/> changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary>For tests: the live watcher, or null when not watching.</summary>
    internal FileSystemWatcher? Watcher
    {
        get { lock (_lock) return _watcher; }
    }

    /// <summary>Starts watching, or waiting for the folder if it doesn't exist yet. Does nothing if already started.</summary>
    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != FolderWatchState.Stopped)
                return;
            _pollTimer = new Timer(_ => Poll(), null, _pollInterval, _pollInterval);
        }
        // A folder present at start needs no resync: the startup catch-up scan covers it.
        TryStartWatching(raiseResync: false);
    }

    /// <summary>Stops watching and drops any pending events.</summary>
    public void Stop()
    {
        bool changed;
        lock (_lock)
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
            DisposeWatcher();
            changed = _state != FolderWatchState.Stopped;
            _state = FolderWatchState.Stopped;
        }
        _debouncer.CancelAll();
        if (changed)
            Raise(StateChanged);
    }

    /// <summary>
    /// Creates the watcher if the folder exists, otherwise switches to waiting. Returns true when watching.
    /// </summary>
    private bool TryStartWatching(bool raiseResync)
    {
        FolderWatchState newState;
        bool changed;
        lock (_lock)
        {
            if (_disposed || _state == FolderWatchState.Stopped && _pollTimer is null)
                return false;

            DisposeWatcher();
            FileSystemWatcher? watcher = null;
            if (Directory.Exists(Root))
            {
                try
                {
                    watcher = CreateWatcher();
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    Log($"Could not watch {Root}: {ex.Message}");
                    watcher = null;
                }
            }

            _watcher = watcher;
            newState = watcher is null ? FolderWatchState.WaitingForFolder : FolderWatchState.Watching;
            changed = newState != _state;
            _state = newState;
        }

        if (changed)
        {
            Log(newState == FolderWatchState.Watching ? $"Watching {Root}" : $"Waiting for {Root} to exist…");
            Raise(StateChanged);
        }
        if (newState == FolderWatchState.Watching && raiseResync)
            Raise(ResyncNeeded);
        return newState == FolderWatchState.Watching;
    }

    private FileSystemWatcher CreateWatcher()
    {
        var watcher = new FileSystemWatcher(Root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = WatchedChanges,
            InternalBufferSize = BufferSize,
        };
        watcher.Created += OnCreatedOrChanged;
        watcher.Changed += OnCreatedOrChanged;
        watcher.Renamed += OnRenamed;
        watcher.Error += OnError;
        try
        {
            watcher.EnableRaisingEvents = true;
        }
        catch
        {
            watcher.Dispose();
            throw;
        }
        return watcher;
    }

    private void DisposeWatcher()
    {
        var watcher = _watcher;
        _watcher = null;
        if (watcher is null)
            return;
        watcher.EnableRaisingEvents = false;
        watcher.Created -= OnCreatedOrChanged;
        watcher.Changed -= OnCreatedOrChanged;
        watcher.Renamed -= OnRenamed;
        watcher.Error -= OnError;
        watcher.Dispose();
    }

    /// <summary>Every poll interval: start watching a folder that has appeared, or notice one that vanished.</summary>
    private void Poll()
    {
        FolderWatchState state;
        lock (_lock)
            state = _state;

        if (state == FolderWatchState.WaitingForFolder)
        {
            // Files may have been saved before the watcher existed.
            TryStartWatching(raiseResync: true);
        }
        else if (state == FolderWatchState.Watching && !Directory.Exists(Root))
        {
            Log($"{Root} no longer exists.");
            TryStartWatching(raiseResync: false);
        }
    }

    private void OnCreatedOrChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsCurrent(sender))
            return;
        _debouncer.Trigger(e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsCurrent(sender))
            return;
        _debouncer.Cancel(e.OldFullPath);
        Raise(FileRenamed, e.OldFullPath, e.FullPath);
        _debouncer.Trigger(e.FullPath);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        if (!IsCurrent(sender))
            return;
        HandleError(e.GetException());
    }

    /// <summary>Recreates the watcher after an error (e.g. buffer overflow) and asks for a resync.</summary>
    internal void HandleError(Exception? ex)
    {
        Log($"Folder watcher error ({ex?.GetType().Name}: {ex?.Message}); recreating the watcher.");
        TryStartWatching(raiseResync: true);
    }

    private bool IsCurrent(object sender)
    {
        lock (_lock)
            return !_disposed && ReferenceEquals(sender, _watcher);
    }

    private void RaiseAppeared(string path)
    {
        // Changed also fires for folders (their contents changed), and a file may be gone by now.
        if (!File.Exists(path))
            return;
        Raise(FileAppeared, path);
    }

    private void Raise(EventHandler? handler)
    {
        try { handler?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log($"Folder watcher event handler failed: {ex.Message}"); }
    }

    private void Raise(Action<string>? handler, string path)
    {
        try { handler?.Invoke(path); }
        catch (Exception ex) { Log($"Folder watcher event handler failed for {path}: {ex.Message}"); }
    }

    private void Raise(Action<string, string>? handler, string oldPath, string newPath)
    {
        try { handler?.Invoke(oldPath, newPath); }
        catch (Exception ex) { Log($"Folder watcher event handler failed for {newPath}: {ex.Message}"); }
    }

    private void Log(string message)
    {
        try { _log?.Invoke(message); } catch { /* logging must never break watching */ }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _pollTimer?.Dispose();
            _pollTimer = null;
            DisposeWatcher();
            _state = FolderWatchState.Stopped;
            _disposed = true;
        }
        _debouncer.Dispose();
    }
}
