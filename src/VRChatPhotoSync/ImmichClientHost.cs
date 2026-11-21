namespace VRChatPhotoSync;

/// <summary>
/// Holds the session's <see cref="ImmichClient"/> and replaces it when the server settings change (Save in the Immich
/// server window). The worker and scanner read <see cref="Current"/> for every file, so the new client applies to the
/// next request. A replaced client is disposed only after a grace period, so an upload still using it can finish.
/// Thread-safe.
/// </summary>
public sealed class ImmichClientHost : IDisposable
{
    /// <summary>How long a replaced client stays usable: one full request timeout plus a margin.</summary>
    public static readonly TimeSpan RetireDelay = ImmichClient.DefaultTimeout + TimeSpan.FromMinutes(1);

    private readonly object _lock = new();
    private readonly Func<string, string, IUploadClient> _create;
    private readonly Func<string?, string?> _unprotect;
    private readonly Action<IDisposable> _retire;
    private IUploadClient? _current;
    private bool _disposed;

    /// <param name="create">Creates a client for (server URL, API key); defaults to a new <see cref="ImmichClient"/>.</param>
    /// <param name="unprotect">Decrypts the saved key; defaults to <see cref="SecretProtector.Unprotect"/>.</param>
    /// <param name="retire">Disposes a replaced client later; defaults to after <see cref="RetireDelay"/>.</param>
    public ImmichClientHost(
        Func<string, string, IUploadClient>? create = null,
        Func<string?, string?>? unprotect = null,
        Action<IDisposable>? retire = null)
    {
        _create = create ?? ((url, key) => new ImmichClient(url, key));
        _unprotect = unprotect ?? SecretProtector.Unprotect;
        _retire = retire ?? (d => _ = Task.Delay(RetireDelay).ContinueWith(_ => d.Dispose(), TaskScheduler.Default));
    }

    /// <summary>The client in use, or null when the server or key isn't set up (or the key can't be decrypted).</summary>
    public IUploadClient? Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>
    /// Replaces the client with one for <paramref name="config"/>'s server and key, or with none when either is missing
    /// or the key can't be decrypted. Returns true when a client is now available.
    /// </summary>
    public bool Restart(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        IUploadClient? next = null;
        if (ConfigStore.IsValidServerUrl(config.ServerUrl) && _unprotect(config.ApiKeyProtected) is { Length: > 0 } key)
        {
            try { next = _create(config.ServerUrl!, key); }
            catch (ArgumentException) { next = null; }
        }

        IUploadClient? old;
        lock (_lock)
        {
            if (_disposed)
            {
                (next as IDisposable)?.Dispose();
                throw new ObjectDisposedException(nameof(ImmichClientHost));
            }
            old = _current;
            _current = next;
        }
        if (old is IDisposable disposable)
            _retire(disposable);
        return next is not null;
    }

    public void Dispose()
    {
        IUploadClient? old;
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            old = _current;
            _current = null;
        }
        (old as IDisposable)?.Dispose();
    }
}
