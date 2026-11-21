namespace VRChatPhotoSync;

/// <summary>What <see cref="FileReadiness.WaitUntilReadyAsync"/> found.</summary>
public enum ReadinessResult
{
    /// <summary>The file can be opened and its size and write time stayed the same across the stability window.</summary>
    Ready,

    /// <summary>The file doesn't exist (deleted, or renamed away; a rename is reported separately by the watcher).</summary>
    Missing,

    /// <summary>The file was still being written (or locked) when the time limit ran out. The caller re-queues it.</summary>
    TimedOut,
}

/// <summary>
/// Waits until a newly written file is complete: it can be opened without anyone else writing it, it isn't empty,
/// and its size and write time are unchanged across about 500 ms. Gives up after about 60 s.
/// </summary>
public sealed class FileReadiness
{
    public static readonly TimeSpan DefaultStableFor = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan DefaultGiveUpAfter = TimeSpan.FromSeconds(60);

    private readonly IFileProbe _probe;
    private readonly IClock _clock;

    public FileReadiness(IFileProbe? probe = null, IClock? clock = null, TimeSpan? stableFor = null, TimeSpan? giveUpAfter = null)
    {
        _probe = probe ?? FileProbe.Instance;
        _clock = clock ?? SystemClock.Instance;
        StableFor = stableFor ?? DefaultStableFor;
        GiveUpAfter = giveUpAfter ?? DefaultGiveUpAfter;
    }

    /// <summary>How long size and write time must stay the same.</summary>
    public TimeSpan StableFor { get; }

    /// <summary>How long to keep trying before returning <see cref="ReadinessResult.TimedOut"/>.</summary>
    public TimeSpan GiveUpAfter { get; }

    public async Task<ReadinessResult> WaitUntilReadyAsync(string path, CancellationToken cancellationToken = default)
    {
        var start = _clock.UtcNow;
        FileStat? previous = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stat = _probe.TryStat(path);
            if (stat is null)
                return ReadinessResult.Missing;

            var openable = stat.Value.Length > 0 && _probe.CanOpen(path);
            if (openable && previous is { } before && before.SameContentAs(stat.Value))
                return ReadinessResult.Ready;

            // Only an openable, non-empty observation starts the stability window.
            previous = openable ? stat : null;

            if (_clock.UtcNow - start >= GiveUpAfter)
                return ReadinessResult.TimedOut;
            await _clock.Delay(StableFor, cancellationToken).ConfigureAwait(false);
        }
    }
}
