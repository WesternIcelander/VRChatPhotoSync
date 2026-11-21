using Timer = System.Threading.Timer;

namespace VRChatPhotoSync;

/// <summary>
/// Collapses bursts of events per key: <see cref="Trigger"/> (re)starts a quiet-period timer
/// for the key, and the callback runs once the key has been quiet for the delay.
/// Callbacks run on the thread pool; an exception from one is reported through the log.
/// </summary>
internal sealed class Debouncer<TKey> : IDisposable where TKey : notnull
{
    private sealed class Pending
    {
        public Timer? Timer;
        public long Generation;
    }

    private readonly object _lock = new();
    private readonly Dictionary<TKey, Pending> _pending;
    private readonly TimeSpan _delay;
    private readonly Action<TKey> _callback;
    private readonly Action<string>? _log;
    private long _generation;
    private bool _disposed;

    public Debouncer(TimeSpan delay, Action<TKey> callback, IEqualityComparer<TKey>? comparer = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
        _callback = callback;
        _log = log;
        _pending = new Dictionary<TKey, Pending>(comparer);
    }

    /// <summary>Number of keys waiting for their quiet period to end.</summary>
    public int PendingCount
    {
        get { lock (_lock) return _pending.Count; }
    }

    /// <summary>True if <paramref name="key"/> is waiting for its quiet period to end.</summary>
    public bool IsPending(TKey key)
    {
        lock (_lock) return _pending.ContainsKey(key);
    }

    /// <summary>Starts or restarts the quiet period for <paramref name="key"/>.</summary>
    public void Trigger(TKey key)
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            if (!_pending.TryGetValue(key, out var pending))
            {
                pending = new Pending();
                _pending[key] = pending;
            }
            // A new generation (and timer) makes any callback already queued for an earlier
            // due time a no-op.
            pending.Generation = ++_generation;
            var generation = pending.Generation;
            pending.Timer?.Dispose();
            pending.Timer = new Timer(_ => Fire(key, generation), null, _delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Drops a pending key without running its callback. True if it was pending.</summary>
    public bool Cancel(TKey key)
    {
        lock (_lock)
        {
            if (!_pending.Remove(key, out var pending))
                return false;
            pending.Timer?.Dispose();
            return true;
        }
    }

    /// <summary>Drops every pending key.</summary>
    public void CancelAll()
    {
        lock (_lock)
        {
            foreach (var pending in _pending.Values)
                pending.Timer?.Dispose();
            _pending.Clear();
        }
    }

    private void Fire(TKey key, long generation)
    {
        lock (_lock)
        {
            if (_disposed || !_pending.TryGetValue(key, out var pending) || pending.Generation != generation)
                return;
            _pending.Remove(key);
            pending.Timer?.Dispose();
        }

        try
        {
            _callback(key);
        }
        catch (Exception ex)
        {
            try { _log?.Invoke($"Debounced handler for {key} failed: {ex.Message}"); } catch { }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var pending in _pending.Values)
                pending.Timer?.Dispose();
            _pending.Clear();
        }
    }
}
