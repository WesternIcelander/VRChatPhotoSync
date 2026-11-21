using Timer = System.Threading.Timer;

namespace VRChatPhotoSync;

/// <summary>Raised when VRChat's photo folder setting now points somewhere else.</summary>
public sealed class PictureFolderChangedEventArgs(VRChatSettings oldSettings, VRChatSettings newSettings) : EventArgs
{
    public VRChatSettings OldSettings { get; } = oldSettings;
    public VRChatSettings NewSettings { get; } = newSettings;
}

/// <summary>
/// Watches the folder holding VRChat's <c>config.json</c> and, after a short quiet period,
/// reloads the settings through the supplied loader (normally <see cref="VRChatSettings.Load"/>
/// with the same <c>--folder</c> override). Raises <see cref="PictureFolderChanged"/> when the
/// picture folder differs from the current one.
/// <para>
/// The file belongs to VRChat and is never written: this class only watches the folder, and
/// the loader reads the file read-only. If the folder doesn't exist yet (VRChat never run),
/// it is looked for every poll interval and never created.
/// </para>
/// </summary>
public sealed class VRChatConfigWatcher : IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(30);

    private const string ReloadKey = "config";

    private readonly object _lock = new();
    private readonly Func<VRChatSettings> _load;
    private readonly TimeSpan _pollInterval;
    private readonly Action<string>? _log;
    private readonly Debouncer<string> _debouncer;
    private readonly object _reloadLock = new();
    private FileSystemWatcher? _watcher;
    private Timer? _pollTimer;
    private VRChatSettings _current;
    private bool _started;
    private bool _disposed;

    /// <param name="configPath">VRChat's <c>config.json</c>.</param>
    /// <param name="current">The settings in use now (what a change is compared against).</param>
    /// <param name="load">Reloads the settings. Must not throw (<see cref="VRChatSettings.Load"/> never does).</param>
    /// <param name="debounce">Quiet period after the last change before reloading; default 1 s.</param>
    /// <param name="pollInterval">How often a missing config folder is looked for; default 30 s.</param>
    /// <param name="log">Receives log lines.</param>
    public VRChatConfigWatcher(
        string configPath,
        VRChatSettings current,
        Func<VRChatSettings> load,
        TimeSpan? debounce = null,
        TimeSpan? pollInterval = null,
        Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(load);
        ConfigPath = Path.GetFullPath(configPath);
        _current = current;
        _load = load;
        _pollInterval = pollInterval is { } p && p > TimeSpan.Zero ? p : DefaultPollInterval;
        _log = log;
        _debouncer = new Debouncer<string>(debounce ?? DefaultDebounce, _ => Reload(), log: log);
    }

    /// <summary>The watched config file.</summary>
    public string ConfigPath { get; }

    /// <summary>The most recently loaded settings.</summary>
    public VRChatSettings Current
    {
        get { lock (_reloadLock) return _current; }
    }

    /// <summary>True while the config folder is being watched (false while waiting for it to exist).</summary>
    public bool IsWatching
    {
        get { lock (_lock) return _watcher is not null; }
    }

    /// <summary>The picture folder changed. Raised on a thread-pool thread.</summary>
    public event EventHandler<PictureFolderChangedEventArgs>? PictureFolderChanged;

    /// <summary>The settings were reloaded (whether or not the folder changed).</summary>
    public event Action<VRChatSettings>? SettingsReloaded;

    /// <summary>Starts watching (or waiting for the config folder). Does nothing if already started.</summary>
    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started)
                return;
            _started = true;
            _pollTimer = new Timer(_ => Poll(), null, _pollInterval, _pollInterval);
        }
        TryStartWatching(reload: false);
    }

    /// <summary>Stops watching.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _started = false;
            _pollTimer?.Dispose();
            _pollTimer = null;
            DisposeWatcher();
        }
        _debouncer.CancelAll();
    }

    private void TryStartWatching(bool reload)
    {
        var directory = Path.GetDirectoryName(ConfigPath);
        bool startedNow;
        lock (_lock)
        {
            if (_disposed || !_started)
                return;
            DisposeWatcher();
            if (directory is not null && Directory.Exists(directory))
            {
                try
                {
                    _watcher = CreateWatcher(directory);
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    Log($"Could not watch VRChat's config folder {directory}: {ex.Message}");
                }
            }
            startedNow = _watcher is not null;
        }

        // The file may have changed while nothing was watching it.
        if (startedNow && reload)
            _debouncer.Trigger(ReloadKey);
    }

    private FileSystemWatcher CreateWatcher(string directory)
    {
        var watcher = new FileSystemWatcher(directory, Path.GetFileName(ConfigPath))
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        watcher.Created += OnChanged;
        watcher.Changed += OnChanged;
        watcher.Deleted += OnChanged;
        watcher.Renamed += OnChanged;
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
        watcher.Created -= OnChanged;
        watcher.Changed -= OnChanged;
        watcher.Deleted -= OnChanged;
        watcher.Renamed -= OnChanged;
        watcher.Error -= OnError;
        watcher.Dispose();
    }

    private void Poll()
    {
        bool waiting;
        lock (_lock)
            waiting = _started && !_disposed && _watcher is null;
        if (waiting)
            TryStartWatching(reload: true);
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (IsCurrent(sender))
            _debouncer.Trigger(ReloadKey);
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        if (!IsCurrent(sender))
            return;
        Log($"VRChat config watcher error ({e.GetException()?.Message}); recreating the watcher.");
        TryStartWatching(reload: true);
    }

    private bool IsCurrent(object sender)
    {
        lock (_lock)
            return !_disposed && ReferenceEquals(sender, _watcher);
    }

    /// <summary>
    /// Reloads the settings now and raises the events. Called after the debounce; internal so
    /// tests can call it directly.
    /// </summary>
    internal void Reload()
    {
        VRChatSettings oldSettings, newSettings;
        lock (_reloadLock)
        {
            try
            {
                newSettings = _load();
            }
            catch (Exception ex)
            {
                Log($"Reloading VRChat's settings failed: {ex.Message}");
                return;
            }
            oldSettings = _current;
            _current = newSettings;
        }

        try { SettingsReloaded?.Invoke(newSettings); }
        catch (Exception ex) { Log($"VRChat settings handler failed: {ex.Message}"); }

        if (string.Equals(
                Path.TrimEndingDirectorySeparator(oldSettings.PictureFolder),
                Path.TrimEndingDirectorySeparator(newSettings.PictureFolder),
                StringComparison.OrdinalIgnoreCase))
            return;

        Log($"VRChat's photo folder changed: {oldSettings.PictureFolder} -> {newSettings.Describe()}");
        try { PictureFolderChanged?.Invoke(this, new PictureFolderChangedEventArgs(oldSettings, newSettings)); }
        catch (Exception ex) { Log($"Photo folder change handler failed: {ex.Message}"); }
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
            _disposed = true;
            _started = false;
            _pollTimer?.Dispose();
            _pollTimer = null;
            DisposeWatcher();
        }
        _debouncer.Dispose();
    }
}
