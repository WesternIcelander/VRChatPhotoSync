using System.Diagnostics;

namespace VRChatPhotoSync.Tests;

public sealed class VrcxSettleTests : IDisposable
{
    private const string Folder = @"D:\Photos\VRChat\2026-11";
    private const string Base = Folder + @"\VRChat_2026-11-21_21-35-02.420_3840x2160";
    private const string Photo = Base + ".png";
    private const string Environment = Base + "_Environment.png";
    private const string Player = Base + "_Player.png";
    private const string Ui = Base + "_UI.png";
    private const string WorldRenamed = Base + "_wrld_12345678-90ab-4cde-8f01-234567890abc.png";
    private const string BareRenamed = Base + "_.png";

    private static readonly TimeSpan Ten = TimeSpan.FromSeconds(10);

    private readonly FakeClock _clock = new();
    private readonly FakeProbe _probe;
    private readonly VrcxSettle _settle;
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "PhotoSyncTests", Guid.NewGuid().ToString("N"));

    public VrcxSettleTests()
    {
        _probe = new FakeProbe(_clock);
        _settle = new VrcxSettle(_probe, _clock);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private Task<SettleResult> Wait(string path, TimeSpan? timeout = null)
    {
        _clock.MarkStart();
        return _settle.WaitAsync(path, timeout ?? Ten);
    }

    private TimeSpan Elapsed => _clock.UtcNow - _clock.Start;

    [Fact]
    public void Defaults_MatchThePlan()
    {
        var settle = new VrcxSettle();
        Assert.Equal(TimeSpan.FromSeconds(1), settle.PollInterval);
        Assert.Equal(TimeSpan.FromSeconds(2), settle.QuietPeriod);
        Assert.Equal(TimeSpan.FromMinutes(2), settle.MaxAge);
        Assert.True(settle.SiblingPollInterval < TimeSpan.FromMilliseconds(150));
    }

    // Immediate returns.

    [Theory]
    [InlineData(Environment)]
    [InlineData(Player)]
    [InlineData(Ui)]
    public async Task LayerFile_ReturnsImmediately(string layer)
    {
        _probe.Add(layer);
        var result = await Wait(layer);
        Assert.Equal(new SettleResult(layer, SettleReason.LayerFile, TimeSpan.Zero), result);
        Assert.Equal(TimeSpan.Zero, Elapsed);
    }

    [Theory]
    [InlineData(Environment)]
    [InlineData(Player)]
    [InlineData(Ui)]
    public async Task ExistingLayerSibling_ReturnsImmediately(string layer)
    {
        _probe.Add(Photo);
        _probe.Add(layer);
        var result = await Wait(Photo);
        Assert.Equal(SettleReason.SiblingExists, result.Reason);
        Assert.Equal(TimeSpan.Zero, result.Waited);
        Assert.Equal(TimeSpan.Zero, Elapsed);
    }

    [Fact]
    public async Task MetadataAlreadyPresent_ReturnsImmediately()
    {
        _probe.Add(Photo);
        _probe.AddChunk(Photo);
        var result = await Wait(Photo);
        Assert.Equal(SettleReason.MetadataPresent, result.Reason);
        Assert.Equal(TimeSpan.Zero, Elapsed);
    }

    [Fact]
    public async Task TimeoutZero_ReturnsImmediately()
    {
        _probe.Add(Photo);
        var result = await Wait(Photo, TimeSpan.Zero);
        Assert.Equal(SettleReason.Disabled, result.Reason);
        Assert.Equal(TimeSpan.Zero, Elapsed);
    }

    [Fact]
    public async Task FileOlderThanTwoMinutes_ReturnsImmediately()
    {
        _probe.Add(Photo, age: TimeSpan.FromMinutes(3));
        var result = await Wait(Photo);
        Assert.Equal(SettleReason.OldFile, result.Reason);
        Assert.Equal(TimeSpan.Zero, Elapsed);
    }

    [Fact]
    public async Task CopiedInFile_WithOldWriteTimeButNewCreationTime_IsNotOld()
    {
        var file = _probe.Add(Photo, age: TimeSpan.FromDays(30));
        file.CreationUtc = _clock.UtcNow;
        var result = await Wait(Photo, TimeSpan.FromSeconds(3));
        Assert.Equal(SettleReason.TimedOut, result.Reason);
    }

    [Fact]
    public async Task FileOneMinuteOld_StillWaits()
    {
        _probe.Add(Photo, age: TimeSpan.FromMinutes(1));
        var result = await Wait(Photo);
        Assert.Equal(SettleReason.TimedOut, result.Reason);
        Assert.Equal(Ten, result.Waited);
    }

    // Exits after waiting.

    [Fact]
    public async Task ChunkAtOneSecond_ReturnsAtOneSecond()
    {
        _probe.Add(Photo);
        _clock.MarkStart();
        _clock.At(TimeSpan.FromSeconds(1), () => _probe.AddChunk(Photo));

        var result = await _settle.WaitAsync(Photo, Ten);

        Assert.Equal(SettleReason.MetadataAppeared, result.Reason);
        Assert.Equal(Photo, result.Path);
        Assert.False(result.Renamed);
        Assert.Equal(TimeSpan.FromSeconds(1), result.Waited);
    }

    [Fact]
    public async Task ChunkAt2_3s_IsSeenAtTheNextOneSecondPoll()
    {
        _probe.Add(Photo);
        _clock.MarkStart();
        _clock.At(Ms(2300), () => _probe.AddChunk(Photo));

        var result = await _settle.WaitAsync(Photo, Ten);

        Assert.Equal(SettleReason.MetadataAppeared, result.Reason);
        Assert.Equal(TimeSpan.FromSeconds(3), result.Waited);
    }

    [Fact]
    public async Task LayerSiblingAt150ms_ReturnsWellUnderOneSecond()
    {
        _probe.Add(Photo);
        _clock.MarkStart();
        _clock.At(Ms(150), () => _probe.Add(Environment));
        _clock.At(Ms(350), () => _probe.Add(Player));

        var result = await _settle.WaitAsync(Photo, Ten);

        Assert.Equal(SettleReason.SiblingAppeared, result.Reason);
        Assert.Equal(Ms(200), result.Waited);
        Assert.Equal(0, _probe.MetadataChecks - 1); // only the initial check; no 1 s poll happened
    }

    [Theory]
    [InlineData("_Player")]
    [InlineData("_UI")]
    public async Task AnyLayerSibling_EndsTheWait(string suffix)
    {
        _probe.Add(Photo);
        _clock.MarkStart();
        _clock.At(Ms(400), () => _probe.Add(Base + suffix + ".png"));

        var result = await _settle.WaitAsync(Photo, Ten);

        Assert.Equal(SettleReason.SiblingAppeared, result.Reason);
        Assert.True(result.Waited < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ChangedWithoutChunk_ThenStableFor2s_Returns()
    {
        _probe.Add(Photo, length: 1000);
        _clock.MarkStart();
        _clock.At(Ms(1500), () => _probe.Write(Photo, 1300));

        var result = await _settle.WaitAsync(Photo, Ten);

        // The change is seen at the 2 s poll and still unchanged at 4 s.
        Assert.Equal(SettleReason.ChangedAndStable, result.Reason);
        Assert.Equal(TimeSpan.FromSeconds(4), result.Waited);
    }

    [Fact]
    public async Task RepeatedChanges_RestartTheQuietPeriod()
    {
        _probe.Add(Photo, length: 1000);
        _clock.MarkStart();
        _clock.At(Ms(500), () => _probe.Write(Photo, 1100));
        _clock.At(Ms(2500), () => _probe.Write(Photo, 1200));

        var result = await _settle.WaitAsync(Photo, Ten);

        // Seen at 1 s, again at 3 s; stable from 3 s to 5 s.
        Assert.Equal(SettleReason.ChangedAndStable, result.Reason);
        Assert.Equal(TimeSpan.FromSeconds(5), result.Waited);
    }

    [Fact]
    public async Task NoChange_TimesOut()
    {
        _probe.Add(Photo);
        var result = await Wait(Photo);
        Assert.Equal(SettleReason.TimedOut, result.Reason);
        Assert.Equal(Ten, result.Waited);
        Assert.Equal(Ten, Elapsed);
    }

    [Fact]
    public async Task KeepsChanging_TimesOut()
    {
        _probe.Add(Photo, length: 1);
        _clock.MarkStart();
        for (var i = 1; i < 20; i++)
        {
            var length = 1 + i;
            _clock.At(Ms(900 * i), () => _probe.Write(Photo, length));
        }

        var result = await _settle.WaitAsync(Photo, Ten);

        Assert.Equal(SettleReason.TimedOut, result.Reason);
        Assert.Equal(Ten, result.Waited);
    }

    [Fact]
    public async Task FractionalTimeout_StillChecksAtTheDeadline()
    {
        _probe.Add(Photo);
        _clock.MarkStart();
        _clock.At(Ms(2400), () => _probe.AddChunk(Photo));

        var result = await _settle.WaitAsync(Photo, Ms(2500));

        Assert.Equal(SettleReason.MetadataAppeared, result.Reason);
        Assert.Equal(Ms(2500), result.Waited);
    }

    [Fact]
    public async Task UnreadableChunkListMidRewrite_IsNotTreatedAsPresent()
    {
        var file = _probe.Add(Photo);
        _clock.MarkStart();
        _clock.At(Ms(900), () => file.Metadata = VrcxMetadataStatus.NotReady);
        _clock.At(Ms(1900), () => _probe.AddChunk(Photo));

        var result = await _settle.WaitAsync(Photo, Ten);

        Assert.Equal(SettleReason.MetadataAppeared, result.Reason);
        Assert.Equal(TimeSpan.FromSeconds(2), result.Waited);
    }

    // Renames.

    [Fact]
    public async Task RenameToWorldName_WithChunkAlreadyAdded_FollowsTheNewPath()
    {
        _probe.Add(Photo);
        _clock.MarkStart();
        _clock.At(Ms(350), () =>
        {
            _probe.Rename(Photo, WorldRenamed);
            _probe.AddChunk(WorldRenamed);
            Assert.True(_settle.NotifyRenamed(Photo, WorldRenamed));
            Assert.True(_settle.IsSettling(WorldRenamed));
            Assert.False(_settle.IsSettling(Photo));
        });

        var result = await _settle.WaitAsync(Photo, Ten);

        Assert.Equal(SettleReason.MetadataAppeared, result.Reason);
        Assert.Equal(WorldRenamed, result.Path);
        Assert.True(result.Renamed);
        Assert.True(result.Waited < TimeSpan.FromSeconds(1), $"Waited {result.Waited}.");
    }

    [Fact]
    public async Task RenameToBareUnderscore_ThenChunkLater_WaitsOnTheNewPath()
    {
        _probe.Add(Photo);
        _clock.MarkStart();
        _clock.At(Ms(200), () =>
        {
            _probe.Rename(Photo, BareRenamed);
            _settle.NotifyRenamed(Photo, BareRenamed);
        });
        _clock.At(Ms(1500), () => _probe.AddChunk(BareRenamed));

        var result = await _settle.WaitAsync(Photo, Ten);

        Assert.Equal(SettleReason.MetadataAppeared, result.Reason);
        Assert.Equal(BareRenamed, result.Path);
        Assert.True(result.Renamed);
        Assert.Equal(TimeSpan.FromSeconds(2), result.Waited);
    }

    [Fact]
    public async Task Renamed_WithoutChunk_TimesOutOnTheNewPath()
    {
        _probe.Add(Photo);
        _clock.MarkStart();
        _clock.At(Ms(300), () =>
        {
            _probe.Rename(Photo, BareRenamed);
            _settle.NotifyRenamed(Photo, BareRenamed);
        });

        var result = await _settle.WaitAsync(Photo, Ten);

        // The rename itself is not a change of content.
        Assert.Equal(SettleReason.TimedOut, result.Reason);
        Assert.Equal(BareRenamed, result.Path);
        Assert.True(result.Renamed);
    }

    [Fact]
    public void NotifyRenamed_ForAPathNotSettling_ReturnsFalse()
    {
        Assert.False(_settle.NotifyRenamed(Photo, WorldRenamed));
    }

    [Fact]
    public async Task DeletedWithoutRename_ReturnsMissing()
    {
        _probe.Add(Photo);
        _clock.MarkStart();
        _clock.At(Ms(500), () => _probe.Delete(Photo));

        var result = await _settle.WaitAsync(Photo, Ten);

        Assert.Equal(SettleReason.Missing, result.Reason);
        Assert.Equal(TimeSpan.FromSeconds(3), result.Waited); // missing at 1 s, gave up 2 s later
    }

    [Fact]
    public async Task Cancellation_Throws_AndUnregisters()
    {
        _probe.Add(Photo);
        using var cts = new CancellationTokenSource();
        _clock.MarkStart();
        _clock.At(Ms(1500), cts.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _settle.WaitAsync(Photo, Ten, cts.Token));
        Assert.Equal(0, _settle.ActiveCount);
    }

    [Fact]
    public async Task ActiveCount_CountsWaitsInProgress()
    {
        _probe.Add(Photo);
        var during = -1;
        _clock.MarkStart();
        _clock.At(Ms(500), () => during = _settle.ActiveCount);

        await _settle.WaitAsync(Photo, TimeSpan.FromSeconds(1));

        Assert.Equal(1, during);
        Assert.Equal(0, _settle.ActiveCount);
    }

    // Real files and real time.

    [Fact]
    public async Task ManyFilesSettleAtOnce_OnTheRealClock()
    {
        Directory.CreateDirectory(_temp);
        var settle = new VrcxSettle { PollInterval = Ms(100), SiblingPollInterval = Ms(50) };
        var paths = Enumerable.Range(0, 5)
            .Select(i => Path.Combine(_temp, $"VRChat_2026-11-21_21-34-5{i}.168_32x32.png"))
            .ToArray();
        for (var i = 0; i < paths.Length; i++)
            PngTestFiles.Write(paths[i], 32, 32, seed: i);

        var stopwatch = Stopwatch.StartNew();
        var waits = paths.Select(p => settle.WaitAsync(p, TimeSpan.FromSeconds(1.5))).ToArray();

        // All five wait at the same time.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (settle.ActiveCount < paths.Length && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Equal(paths.Length, settle.ActiveCount);

        // VRCX adds its chunk to the first, a layer appears for the second; the rest time out.
        await Task.Delay(300);
        PngTestFiles.InsertDescriptionChunk(paths[0]);
        PngTestFiles.Write(Path.Combine(_temp, Path.GetFileNameWithoutExtension(paths[1]) + "_Environment.png"), 32, 32);

        var results = await Task.WhenAll(waits).WaitAsync(TimeSpan.FromSeconds(10));
        stopwatch.Stop();

        Assert.Equal(SettleReason.MetadataAppeared, results[0].Reason);
        Assert.Equal(SettleReason.SiblingAppeared, results[1].Reason);
        Assert.All(results.Skip(2), r => Assert.Equal(SettleReason.TimedOut, r.Reason));
        Assert.True(results[0].Waited < TimeSpan.FromSeconds(1.4), $"Chunk wait took {results[0].Waited}.");
        // Run in parallel: well under 5 × 1.5 s.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"Took {stopwatch.Elapsed}.");
        Assert.Equal(0, settle.ActiveCount);
    }

    [Fact]
    public async Task RealFile_ChunkInsertedInPlace_IsDetected()
    {
        Directory.CreateDirectory(_temp);
        var path = Path.Combine(_temp, "VRChat_2026-11-21_21-34-55.168_64x64.png");
        PngTestFiles.Write(path, 64, 64);
        var settle = new VrcxSettle { PollInterval = Ms(100) };

        var wait = settle.WaitAsync(path, TimeSpan.FromSeconds(10));
        await Task.Delay(250);
        PngTestFiles.InsertDescriptionChunk(path);

        var result = await wait.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(SettleReason.MetadataAppeared, result.Reason);
        Assert.True(result.Waited < TimeSpan.FromSeconds(5));
    }
}
