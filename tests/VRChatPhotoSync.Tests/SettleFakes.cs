namespace VRChatPhotoSync.Tests;

/// <summary>
/// A clock that never really waits: <see cref="Delay"/> moves time forward at once and runs any actions scheduled
/// up to the new time, so a test can script "the chunk appears at 1 s" and run in microseconds.
/// </summary>
public sealed class FakeClock : IClock
{
    private readonly object _lock = new();
    private readonly List<(DateTime At, Action Action)> _scheduled = [];
    private DateTime _now;

    public FakeClock(DateTime? start = null) => _now = start ?? new DateTime(2026, 11, 21, 20, 0, 0, DateTimeKind.Utc);

    public DateTime Start { get; private set; }

    public DateTime UtcNow
    {
        get { lock (_lock) return _now; }
    }

    /// <summary>Marks the current time as the start that <see cref="At"/> offsets count from.</summary>
    public FakeClock MarkStart()
    {
        lock (_lock) Start = _now;
        return this;
    }

    /// <summary>Runs <paramref name="action"/> once time reaches <see cref="Start"/> + <paramref name="offset"/>.</summary>
    public void At(TimeSpan offset, Action action)
    {
        lock (_lock) _scheduled.Add((Start + offset, action));
    }

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<Action> due;
        lock (_lock)
        {
            _now += delay;
            due = _scheduled.Where(s => s.At <= _now).OrderBy(s => s.At).Select(s => s.Action).ToList();
            _scheduled.RemoveAll(s => s.At <= _now);
        }
        foreach (var action in due)
            action();
        return Task.CompletedTask;
    }
}

/// <summary>An in-memory file system for <see cref="IFileProbe"/>, with scriptable files.</summary>
public sealed class FakeProbe : IFileProbe
{
    public sealed class FakeFile
    {
        public long Length = 1000;
        public DateTime LastWriteUtc;
        public DateTime CreationUtc;
        public bool Locked;
        public VrcxMetadataStatus Metadata = VrcxMetadataStatus.Absent;
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, FakeFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly IClock _clock;

    public FakeProbe(IClock clock) => _clock = clock;

    public int MetadataChecks;

    /// <summary>Creates (or replaces) a file written "now", or <paramref name="age"/> ago.</summary>
    public FakeFile Add(string path, long length = 1000, TimeSpan? age = null)
    {
        var time = _clock.UtcNow - (age ?? TimeSpan.Zero);
        var file = new FakeFile { Length = length, LastWriteUtc = time, CreationUtc = time };
        lock (_lock) _files[path] = file;
        return file;
    }

    /// <summary>Changes a file's content: new length and a new write time.</summary>
    public void Write(string path, long length)
    {
        lock (_lock)
        {
            var file = _files[path];
            file.Length = length;
            file.LastWriteUtc = _clock.UtcNow;
        }
    }

    /// <summary>Adds VRCX's chunk (the file grows by 201 bytes, as measured).</summary>
    public void AddChunk(string path)
    {
        lock (_lock)
        {
            var file = _files[path];
            file.Length += 201;
            file.LastWriteUtc = _clock.UtcNow;
            file.Metadata = VrcxMetadataStatus.Present;
        }
    }

    public void Rename(string oldPath, string newPath)
    {
        lock (_lock)
        {
            _files.Remove(oldPath, out var file);
            _files[newPath] = file!;
        }
    }

    public void Delete(string path)
    {
        lock (_lock) _files.Remove(path);
    }

    public void SetLocked(string path, bool locked)
    {
        lock (_lock) _files[path].Locked = locked;
    }

    public FileStat? TryStat(string path)
    {
        lock (_lock)
            return _files.TryGetValue(path, out var f) ? new FileStat(f.Length, f.LastWriteUtc, f.CreationUtc) : null;
    }

    public bool Exists(string path)
    {
        lock (_lock) return _files.ContainsKey(path);
    }

    public bool CanOpen(string path)
    {
        lock (_lock) return _files.TryGetValue(path, out var f) && !f.Locked;
    }

    public VrcxMetadataStatus CheckVrcxMetadata(string path)
    {
        Interlocked.Increment(ref MetadataChecks);
        lock (_lock)
            return _files.TryGetValue(path, out var f) ? (f.Locked ? VrcxMetadataStatus.NotReady : f.Metadata) : VrcxMetadataStatus.NotReady;
    }
}
