namespace VRChatPhotoSync;

/// <summary>
/// The settings while the app runs. <see cref="Saved"/> is what <c>config.json</c> holds; <see cref="Current"/> is that
/// plus this run's command-line overrides, and is what the worker reads (live, for every file). A change made in the UI
/// (<see cref="Update"/>) is applied to both and saved straight away, so the overrides are never written to the file.
/// Thread-safe.
/// </summary>
public sealed class LiveConfig
{
    private readonly object _lock = new();
    private readonly Action<AppConfig> _save;
    private AppConfig _saved;
    private AppConfig _current;

    /// <param name="saved">The config as stored in the file.</param>
    /// <param name="current">The config for this run (<paramref name="saved"/> with the command-line overrides).</param>
    /// <param name="save">Writes the file (normally <see cref="ConfigStore.Save"/>).</param>
    public LiveConfig(AppConfig saved, AppConfig current, Action<AppConfig> save)
    {
        _saved = saved ?? throw new ArgumentNullException(nameof(saved));
        _current = current ?? throw new ArgumentNullException(nameof(current));
        _save = save ?? throw new ArgumentNullException(nameof(save));
    }

    /// <summary>The config as stored in <c>config.json</c>.</summary>
    public AppConfig Saved
    {
        get { lock (_lock) return _saved; }
    }

    /// <summary>The config this run uses (with the command-line overrides).</summary>
    public AppConfig Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>Raised after a change was applied (with the new <see cref="Current"/>), on the caller's thread.</summary>
    public event Action<AppConfig>? Changed;

    /// <summary>
    /// Applies <paramref name="change"/> to both the saved and the current config, then saves the file. If saving throws,
    /// the change still applies to this run and the exception is passed on so the caller can report it.
    /// </summary>
    public void Update(Func<AppConfig, AppConfig> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        AppConfig current;
        Exception? saveError = null;
        lock (_lock)
        {
            var saved = change(_saved);
            current = change(_current);
            _saved = saved;
            _current = current;
            // Saved under the lock so two quick changes can't reach the file in the wrong order.
            try { _save(saved); }
            catch (Exception ex) { saveError = ex; }
        }
        Changed?.Invoke(current);
        if (saveError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(saveError);
    }
}
