namespace VRChatPhotoSync;

/// <summary>Why <see cref="VrcxSettle.WaitAsync"/> stopped waiting.</summary>
public enum SettleReason
{
    // Returned straight away, without waiting.

    /// <summary>The file is a layer of a multi-layer photo; VRCX never adds metadata to those.</summary>
    LayerFile,

    /// <summary>A layer file with the same base name already exists, so this is a multi-layer photo.</summary>
    SiblingExists,

    /// <summary>VRCX's <c>Description</c> chunk is already there.</summary>
    MetadataPresent,

    /// <summary>The wait is turned off (timeout 0).</summary>
    Disabled,

    /// <summary>The file is more than 2 minutes old (found by a catch-up scan or backfill).</summary>
    OldFile,

    // Returned after waiting.

    /// <summary>VRCX's <c>Description</c> chunk appeared.</summary>
    MetadataAppeared,

    /// <summary>A layer file with the same base name appeared (a multi-layer photo).</summary>
    SiblingAppeared,

    /// <summary>The file changed and then stayed the same for the quiet period (someone edited it, without a <c>Description</c> chunk).</summary>
    ChangedAndStable,

    /// <summary>The time limit ran out; upload whatever is there.</summary>
    TimedOut,

    /// <summary>The file disappeared and no rename was reported.</summary>
    Missing,
}

/// <summary>The outcome of a settle wait.</summary>
/// <param name="Path">The file's final path (different from the one asked for when it was renamed while waiting).</param>
/// <param name="Reason">Why the wait ended.</param>
/// <param name="Waited">How long it waited.</param>
public readonly record struct SettleResult(string Path, SettleReason Reason, TimeSpan Waited)
{
    /// <summary>True when the file was renamed during the wait.</summary>
    public bool Renamed { get; init; }
}

/// <summary>
/// Waits, for a fully written photo, until VRCX has added its metadata (or clearly won't), so the photo is uploaded once,
/// in its final form. Any number of files can be settled at once; <see cref="NotifyRenamed"/> moves a wait to a file's new path.
/// </summary>
public sealed class VrcxSettle
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan DefaultSiblingPollInterval = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan DefaultMissingGrace = TimeSpan.FromSeconds(2);

    private sealed class Waiter(string path)
    {
        public string Path = path;
        public bool Renamed;
    }

    private readonly IFileProbe _probe;
    private readonly IClock _clock;
    private readonly object _lock = new();
    private readonly List<Waiter> _waiters = [];

    public VrcxSettle(IFileProbe? probe = null, IClock? clock = null)
    {
        _probe = probe ?? FileProbe.Instance;
        _clock = clock ?? SystemClock.Instance;
    }

    /// <summary>How often the chunk list and file size/time are re-checked.</summary>
    public TimeSpan PollInterval { get; init; } = DefaultPollInterval;

    /// <summary>How often the cheap layer-sibling and rename checks run (layers follow the combined photo within ~150–400 ms).</summary>
    public TimeSpan SiblingPollInterval { get; init; } = DefaultSiblingPollInterval;

    /// <summary>After a change without a chunk, how long the file must stay the same.</summary>
    public TimeSpan QuietPeriod { get; init; } = DefaultQuietPeriod;

    /// <summary>Files older than this (by the later of creation and last-write time) don't wait.</summary>
    public TimeSpan MaxAge { get; init; } = DefaultMaxAge;

    /// <summary>How long a file may be missing (waiting for a rename to be reported) before giving up.</summary>
    public TimeSpan MissingGrace { get; init; } = DefaultMissingGrace;

    /// <summary>Number of files waiting right now (for the "waiting for VRCX (N)" status).</summary>
    public int ActiveCount
    {
        get { lock (_lock) return _waiters.Count; }
    }

    /// <summary>True while a wait for <paramref name="path"/> (its current path) is running.</summary>
    public bool IsSettling(string path)
    {
        lock (_lock) return _waiters.Exists(w => PathEquals(w.Path, path));
    }

    /// <summary>
    /// Reports a rename from the folder watcher. Any wait on <paramref name="oldPath"/> continues on <paramref name="newPath"/>.
    /// Returns true when a wait was moved.
    /// </summary>
    public bool NotifyRenamed(string oldPath, string newPath)
    {
        var moved = false;
        lock (_lock)
        {
            foreach (var waiter in _waiters)
            {
                if (PathEquals(waiter.Path, oldPath))
                {
                    waiter.Path = newPath;
                    waiter.Renamed = true;
                    moved = true;
                }
            }
        }
        return moved;
    }

    /// <summary>
    /// Waits until VRCX's metadata is in the file, or it is clear none is coming. Returns straight away for a layer file,
    /// a photo with an existing layer sibling, a file that already has the chunk, <paramref name="timeout"/> 0, or a file
    /// older than <see cref="MaxAge"/>. Otherwise polls until the chunk appears, a layer sibling appears, the file changes
    /// and then stays the same for <see cref="QuietPeriod"/>, or the timeout runs out. A rename (via <see cref="NotifyRenamed"/>)
    /// is followed. Never throws except for cancellation.
    /// </summary>
    public async Task<SettleResult> WaitAsync(string path, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var start = _clock.UtcNow;
        if (VrcFileName.IsLayer(path))
            return new SettleResult(path, SettleReason.LayerFile, TimeSpan.Zero);
        if (AnySiblingExists(path))
            return new SettleResult(path, SettleReason.SiblingExists, TimeSpan.Zero);
        if (_probe.CheckVrcxMetadata(path) == VrcxMetadataStatus.Present)
            return new SettleResult(path, SettleReason.MetadataPresent, TimeSpan.Zero);
        if (timeout <= TimeSpan.Zero)
            return new SettleResult(path, SettleReason.Disabled, TimeSpan.Zero);
        var initial = _probe.TryStat(path);
        if (initial is { } first && start - first.NewestUtc > MaxAge)
            return new SettleResult(path, SettleReason.OldFile, TimeSpan.Zero);

        var waiter = new Waiter(path);
        lock (_lock) _waiters.Add(waiter);
        try
        {
            return await PollAsync(waiter, initial, start, timeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_lock) _waiters.Remove(waiter);
        }
    }

    private async Task<SettleResult> PollAsync(Waiter waiter, FileStat? initial, DateTime start, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string currentPath;
        bool renamed;
        lock (_lock) (currentPath, renamed) = (waiter.Path, waiter.Renamed);

        var lastStat = initial;        // what the last full check saw
        DateTime? changedAt = null;    // when the last change (from the original) was seen
        DateTime? missingSince = initial is null ? start : null;
        var nextFullCheck = start + PollInterval;

        SettleResult Result(SettleReason reason) => new(currentPath, reason, _clock.UtcNow - start) { Renamed = renamed };

        while (true)
        {
            var now = _clock.UtcNow;
            var deadline = start + timeout;
            var step = Min(SiblingPollInterval, nextFullCheck - now, deadline - now);
            if (step > TimeSpan.Zero)
                await _clock.Delay(step, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            now = _clock.UtcNow;

            string path;
            lock (_lock) (path, renamed) = (waiter.Path, waiter.Renamed);
            var pathChanged = !PathEquals(path, currentPath);
            currentPath = path;

            if (AnySiblingExists(currentPath))
                return Result(SettleReason.SiblingAppeared);

            // A rename is checked straight away: VRCX renames first, then writes the chunk (often already there).
            // The last check runs at the deadline, so a chunk written just before it still counts.
            if (now >= nextFullCheck || pathChanged || now >= deadline)
            {
                while (nextFullCheck <= now)
                    nextFullCheck += PollInterval;

                if (_probe.CheckVrcxMetadata(currentPath) == VrcxMetadataStatus.Present)
                    return Result(SettleReason.MetadataAppeared);

                var stat = _probe.TryStat(currentPath);
                if (stat is null)
                {
                    missingSince ??= now;
                    if (now - missingSince.Value >= MissingGrace)
                        return Result(SettleReason.Missing);
                }
                else
                {
                    missingSince = null;
                    if (lastStat is not { } last || !last.SameContentAs(stat.Value))
                    {
                        // The first sighting after a rename (or after a missing spell) only sets the baseline.
                        if (lastStat is not null && !pathChanged)
                            changedAt = now;
                        lastStat = stat;
                    }
                    else if (changedAt is { } changed && now - changed >= QuietPeriod)
                    {
                        return Result(SettleReason.ChangedAndStable);
                    }
                }
            }

            if (now >= deadline)
                return Result(SettleReason.TimedOut);
        }
    }

    private bool AnySiblingExists(string path)
    {
        foreach (var sibling in VrcFileName.LayerSiblingPaths(path))
        {
            if (_probe.Exists(sibling))
                return true;
        }
        return false;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b, TimeSpan c) => a < b ? (a < c ? a : c) : (b < c ? b : c);

    private static bool PathEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
