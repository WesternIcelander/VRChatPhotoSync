using System.Threading.Channels;

namespace VRChatPhotoSync;

/// <summary>
/// The files waiting to be processed, in arrival order: a <see cref="Channel{T}"/> of full paths,
/// de-duplicated by path (ignoring case). A path that is already waiting isn't added again; once the
/// worker has taken it, it can be queued again (for example when the file changes later).
/// Thread-safe; any number of writers, one reader (<see cref="UploadWorker"/>).
/// </summary>
public sealed class UploadQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>Number of files waiting (not counting the ones the worker is already processing).</summary>
    public int Count
    {
        get { lock (_lock) return _pending.Count; }
    }

    /// <summary>Raised (on the caller's thread) when <see cref="Count"/> changes.</summary>
    public event Action<int>? CountChanged;

    /// <summary>
    /// Queues <paramref name="path"/>. Returns false when the same path is already waiting, or the
    /// queue has been completed.
    /// </summary>
    public bool Enqueue(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        int count;
        lock (_lock)
        {
            if (!_pending.Add(path))
                return false;
            if (!_channel.Writer.TryWrite(path))
            {
                _pending.Remove(path);
                return false;
            }
            count = _pending.Count;
        }
        RaiseCountChanged(count);
        return true;
    }

    /// <summary>True when <paramref name="path"/> is waiting in the queue.</summary>
    public bool Contains(string path)
    {
        lock (_lock) return _pending.Contains(path);
    }

    /// <summary>Takes the next path, waiting for one. Throws <see cref="ChannelClosedException"/> once completed and empty.</summary>
    public async ValueTask<string> DequeueAsync(CancellationToken cancellationToken = default)
    {
        var path = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        Taken(path);
        return path;
    }

    /// <summary>Takes the next path if one is waiting.</summary>
    public bool TryDequeue(out string path)
    {
        if (!_channel.Reader.TryRead(out path!))
            return false;
        Taken(path);
        return true;
    }

    /// <summary>All paths as they arrive, until <see cref="Complete"/> is called and the queue is empty.</summary>
    public async IAsyncEnumerable<string> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (TryDequeue(out var path))
                yield return path;
        }
    }

    /// <summary>Stops accepting new paths (on shutdown). Paths already queued can still be read.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    private void Taken(string path)
    {
        int count;
        lock (_lock)
        {
            _pending.Remove(path);
            count = _pending.Count;
        }
        RaiseCountChanged(count);
    }

    private void RaiseCountChanged(int count)
    {
        try { CountChanged?.Invoke(count); }
        catch { /* a UI handler must never break the queue */ }
    }
}
