namespace VRChatPhotoSync.Tests;

public sealed class FileReadinessTests : IDisposable
{
    private const string Photo = @"D:\Photos\VRChat\2026-11\VRChat_2026-11-21_21-34-55.168_3840x2160.png";

    private readonly FakeClock _clock = new();
    private readonly FakeProbe _probe;
    private readonly FileReadiness _readiness;
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "PhotoSyncTests", Guid.NewGuid().ToString("N"));

    public FileReadinessTests()
    {
        _probe = new FakeProbe(_clock);
        _readiness = new FileReadiness(_probe, _clock);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private TimeSpan Elapsed => _clock.UtcNow - _clock.Start;

    [Fact]
    public void Defaults_Are500msStableAnd60sGiveUp()
    {
        var readiness = new FileReadiness();
        Assert.Equal(TimeSpan.FromMilliseconds(500), readiness.StableFor);
        Assert.Equal(TimeSpan.FromSeconds(60), readiness.GiveUpAfter);
    }

    [Fact]
    public async Task StableFile_IsReadyAfterOneStableWindow()
    {
        _probe.Add(Photo);
        _clock.MarkStart();

        Assert.Equal(ReadinessResult.Ready, await _readiness.WaitUntilReadyAsync(Photo));
        Assert.Equal(TimeSpan.FromMilliseconds(500), Elapsed);
    }

    [Fact]
    public async Task GrowingFile_IsReadyOnlyOnceItStopsChanging()
    {
        _probe.Add(Photo, length: 100);
        _clock.MarkStart();
        for (var i = 1; i <= 7; i++)
        {
            var length = 100 + i * 100;
            _clock.At(TimeSpan.FromMilliseconds(250 * i), () => _probe.Write(Photo, length));
        }

        Assert.Equal(ReadinessResult.Ready, await _readiness.WaitUntilReadyAsync(Photo));
        // Last write at 1.75 s is seen at 2.0 s and confirmed unchanged at 2.5 s.
        Assert.Equal(TimeSpan.FromMilliseconds(2500), Elapsed);
    }

    [Fact]
    public async Task LockedFile_WaitsUntilItCanBeOpened()
    {
        _probe.Add(Photo);
        _probe.SetLocked(Photo, true);
        _clock.MarkStart();
        _clock.At(TimeSpan.FromMilliseconds(900), () => _probe.SetLocked(Photo, false));

        Assert.Equal(ReadinessResult.Ready, await _readiness.WaitUntilReadyAsync(Photo));
        // Openable first at 1.0 s, stable at 1.5 s.
        Assert.Equal(TimeSpan.FromMilliseconds(1500), Elapsed);
    }

    [Fact]
    public async Task MissingFile_ReturnsMissing()
    {
        _clock.MarkStart();
        Assert.Equal(ReadinessResult.Missing, await _readiness.WaitUntilReadyAsync(Photo));
        Assert.Equal(TimeSpan.Zero, Elapsed);
    }

    [Fact]
    public async Task FileDeletedWhileWaiting_ReturnsMissing()
    {
        _probe.Add(Photo);
        _probe.SetLocked(Photo, true);
        _clock.MarkStart();
        _clock.At(TimeSpan.FromSeconds(2), () => _probe.Delete(Photo));

        Assert.Equal(ReadinessResult.Missing, await _readiness.WaitUntilReadyAsync(Photo));
    }

    [Fact]
    public async Task FileThatNeverSettles_GivesUpAfterAbout60s()
    {
        _probe.Add(Photo, length: 1);
        _clock.MarkStart();
        for (var i = 1; i <= 200; i++)
        {
            var length = 1 + i;
            _clock.At(TimeSpan.FromMilliseconds(500 * i - 100), () => _probe.Write(Photo, length));
        }

        Assert.Equal(ReadinessResult.TimedOut, await _readiness.WaitUntilReadyAsync(Photo));
        Assert.InRange(Elapsed, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(61));
    }

    [Fact]
    public async Task EmptyFile_IsNeverReady()
    {
        _probe.Add(Photo, length: 0);
        _clock.MarkStart();

        Assert.Equal(ReadinessResult.TimedOut, await _readiness.WaitUntilReadyAsync(Photo));
    }

    [Fact]
    public async Task Cancellation_Throws()
    {
        _probe.Add(Photo);
        _probe.SetLocked(Photo, true);
        using var cts = new CancellationTokenSource();
        _clock.MarkStart();
        _clock.At(TimeSpan.FromSeconds(1), cts.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _readiness.WaitUntilReadyAsync(Photo, cts.Token));
    }

    [Fact]
    public async Task RealFile_HeldOpenForWriting_IsReadyOnlyAfterClose()
    {
        Directory.CreateDirectory(_temp);
        var path = Path.Combine(_temp, "VRChat_2026-11-21_21-34-55.168_64x64.png");
        var readiness = new FileReadiness(stableFor: TimeSpan.FromMilliseconds(50), giveUpAfter: TimeSpan.FromSeconds(20));

        var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        writer.Write(PngTestFiles.Generate(64, 64));
        writer.Flush();

        var wait = readiness.WaitUntilReadyAsync(path);
        await Task.Delay(400);
        Assert.False(wait.IsCompleted, "Ready while the writer still had the file open.");

        await writer.DisposeAsync();
        Assert.Equal(ReadinessResult.Ready, await wait.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task RealFile_OpenedByVrcxStyleSharedReader_DoesNotBlock()
    {
        Directory.CreateDirectory(_temp);
        var path = Path.Combine(_temp, "VRChat_2026-11-21_21-34-55.168_64x64.png");
        PngTestFiles.Write(path, 64, 64);
        var readiness = new FileReadiness(stableFor: TimeSpan.FromMilliseconds(50));

        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Assert.Equal(ReadinessResult.Ready, await readiness.WaitUntilReadyAsync(path).WaitAsync(TimeSpan.FromSeconds(10)));
    }
}
