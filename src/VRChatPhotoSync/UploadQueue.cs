using System.Threading.Channels;

namespace VRChatPhotoSync;

/// <summary>
/// The files waiting to be processed, in arrival order: a <see cref="Channel{T}"/> of full paths,
/// de-duplicated by path (ignoring case). A path that is already waiting isn't added again; once the
/// worker has taken it, it can be queued again (for example when the file changes later).
/// A waiting path can be renamed in place (<see cref="Rename"/>), keeping its position.
/// Thread-safe; any number of writers, one reader (<see cref="UploadWorker"/>).
/// </summary>
public sealed class UploadQueue
{
    /// <summary>One waiting file. <see cref="Path"/> changes on a rename; null means dropped.</summary>
    private sealed class Item(string path)
    {
        public string? Path = path;
    }

    private readonly Channel<Item> _channel = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, Item> _pending = new(StringComparer.OrdinalIgnoreCase);
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
            if (_pending.ContainsKey(path))
                return false;
            var item = new Item(path);
            if (!_channel.Writer.TryWrite(item))
                return false;
            _pending[path] = item;
            count = _pending.Count;
        }
        RaiseCountChanged(count);
        return true;
    }

    /// <summary>True when <paramref name="path"/> is waiting in the queue.</summary>
    public bool Contains(string path)
    {
        lock (_lock) return _pending.ContainsKey(path);
    }

    /// <summary>
    /// A waiting file was renamed: it keeps its place in the queue under <paramref name="newPath"/>, so the
    /// old name is never handed out. If <paramref name="newPath"/> is already waiting, the old entry is dropped.
    /// Returns false when <paramref name="oldPath"/> isn't waiting.
    /// </summary>
    public bool Rename(string oldPath, string newPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(newPath);
        int count;
        lock (_lock)
        {
            if (!_pending.Remove(oldPath, out var item))
                return false;
            if (_pending.ContainsKey(newPath))
            {
                item.Path = null;
            }
            else
            {
                item.Path = newPath;
                _pending[newPath] = item;
            }
            count = _pending.Count;
        }
        RaiseCountChanged(count);
        return true;
    }

    /// <summary>Takes the next path, waiting for one. Throws <see cref="ChannelClosedException"/> once completed and empty.</summary>
    public async ValueTask<string> DequeueAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var item = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (Taken(item) is { } path)
                return path;
        }
    }

    /// <summary>Takes the next path if one is waiting.</summary>
    public bool TryDequeue(out string path)
    {
        while (_channel.Reader.TryRead(out var item))
        {
            if (Taken(item) is { } p)
            {
                path = p;
                return true;
            }
        }
        path = null!;
        return false;
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

    /// <summary>The item's current path (null when it was dropped by a rename).</summary>
    private string? Taken(Item item)
    {
        string? path;
        int count;
        lock (_lock)
        {
            path = item.Path;
            if (path is null)
                return null;
            if (_pending.TryGetValue(path, out var current) && ReferenceEquals(current, item))
                _pending.Remove(path);
            item.Path = null;
            count = _pending.Count;
        }
        RaiseCountChanged(count);
        return path;
    }

    private void RaiseCountChanged(int count)
    {
        try { CountChanged?.Invoke(count); }
        catch { /* a UI handler must never break the queue */ }
    }
}
