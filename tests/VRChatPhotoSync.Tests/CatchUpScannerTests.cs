using System.Security.Cryptography;

namespace VRChatPhotoSync.Tests;

public sealed class CatchUpScannerTests : IDisposable
{
    private readonly string _root;
    private readonly FakeClock _clock = new(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc));
    private readonly UploadQueue _queue = new();
    private readonly UploadState _state = new();
    private readonly object _stateLock = new();
    private readonly FakeUploadClient _client = new();
    private readonly List<string> _log = [];
    private readonly HashSet<string> _prints = new(StringComparer.OrdinalIgnoreCase);
    private AppConfig _config = new();
    private IUploadClient? _currentClient;
    private int _saves;

    public CatchUpScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "PhotoSyncCatchUp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _currentClient = _client;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private DateTime Now => _clock.UtcNow;

    private CatchUpScanner CreateScanner()
    {
        var scanner = new CatchUpScanner(
            _queue, _state, _stateLock,
            _ => Interlocked.Increment(ref _saves),
            () => _config,
            () => _currentClient,
            clock: _clock,
            isPrint: path => _prints.Contains(path));
        scanner.Log += l => { lock (_log) _log.Add(l); };
        return scanner;
    }

    /// <summary>Writes a file with random content; <paramref name="written"/> and <paramref name="created"/> are ages before now.</summary>
    private string AddFile(string relative, TimeSpan written, TimeSpan? created = null, int size = 500)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(size));
        File.SetCreationTimeUtc(path, Now - (created ?? written));
        File.SetLastWriteTimeUtc(path, Now - written);
        return path;
    }

    private static string Sha1(string path) => Convert.ToHexStringLower(SHA1.HashData(File.ReadAllBytes(path)));

    private void BindState(TimeSpan lastScanAgo)
    {
        _state.BindToFolder(_root, Now);
        _state.LastScanUtc = Now - lastScanAgo;
    }

    private List<string> Drain()
    {
        var list = new List<string>();
        while (_queue.TryDequeue(out var p))
            list.Add(p);
        return list;
    }

    private void RecordUploaded(string path, string assetId = "asset-old")
    {
        var info = new FileInfo(path);
        _state.Set(path, new StateEntry { Size = info.Length, MtimeUtc = info.LastWriteTimeUtc, Sha1 = Sha1(path), AssetId = assetId });
    }

    private const string Name1 = "VRChat_2026-11-21_21-34-55.168_3840x2160.png";
    private const string Name2 = "VRChat_2026-11-21_21-36-10.001_3840x2160.png";
    private const string Name3 = "VRChat_2026-11-21_21-37-20.002_3840x2160.png";
    private const string Name4 = "VRChat_2026-11-21_21-38-30.003_3840x2160.png";

    // ---- Catch-up ----

    [Fact]
    public void First_Run_Sets_LastScan_To_Now_And_Uploads_Nothing_Old()
    {
        AddFile(Name1, TimeSpan.FromDays(10));
        AddFile(@"2026-11\" + Name2, TimeSpan.FromHours(2));

        var result = CreateScanner().Startup(_root);

        Assert.True(result.FirstRun);
        Assert.Equal(2, result.Scanned);
        Assert.Equal(0, result.Enqueued);
        Assert.Empty(Drain());
        Assert.Equal(Now, _state.LastScanUtc);
        Assert.Equal(Path.GetFullPath(_root), _state.PictureFolder);
        Assert.True(_saves >= 1);
    }

    [Fact]
    public void Finds_New_Files_In_Root_And_Month_Folders_Oldest_First()
    {
        BindState(TimeSpan.FromHours(1));
        AddFile(@"2026-11\" + Name1, TimeSpan.FromHours(3));         // before the last scan
        var month = AddFile(@"2026-11\" + Name2, TimeSpan.FromMinutes(10));
        var root = AddFile(Name3, TimeSpan.FromMinutes(30));          // older than the month one
        var olderMonth = AddFile(@"2025-08\" + Name4, TimeSpan.FromMinutes(20));
        var lastScanBefore = _state.LastScanUtc;
        _clock.Delay(TimeSpan.FromSeconds(1), default);

        var result = CreateScanner().Startup(_root);

        Assert.False(result.FirstRun);
        Assert.Equal(4, result.Scanned);
        Assert.Equal(3, result.Enqueued);
        Assert.Equal([root, olderMonth, month], Drain());
        Assert.NotEqual(lastScanBefore, _state.LastScanUtc);
        Assert.Equal(Now, _state.LastScanUtc);
    }

    [Fact]
    public void Copied_In_File_With_Old_Write_Time_Is_Found()
    {
        BindState(TimeSpan.FromHours(1));
        var copied = AddFile(@"2026-11\" + Name1, written: TimeSpan.FromDays(365), created: TimeSpan.FromMinutes(5));
        AddFile(@"2026-11\" + Name2, written: TimeSpan.FromDays(365));

        CreateScanner().CatchUp();

        Assert.Equal([copied], Drain());
    }

    [Fact]
    public void Looks_One_Minute_Before_The_Last_Scan()
    {
        BindState(TimeSpan.FromMinutes(10));
        var inOverlap = AddFile(Name1, TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(30));
        AddFile(Name2, TimeSpan.FromMinutes(12));

        CreateScanner().CatchUp();

        Assert.Equal([inOverlap], Drain());
    }

    [Fact]
    public void Excluded_Folders_And_Other_Files_Are_Skipped()
    {
        BindState(TimeSpan.FromHours(1));
        var photo = AddFile(@"2026-11\" + Name1, TimeSpan.FromMinutes(5));
        AddFile(@"Prints\2026-11\" + Name2, TimeSpan.FromMinutes(5));
        AddFile(@"Stickers\2026-11\" + Name3, TimeSpan.FromMinutes(5));
        AddFile(@"emoji\" + Name4, TimeSpan.FromMinutes(5));
        AddFile(@"2026-11\VRChat_2026-11-21_21-34-55.168_3840x2160 - Copy.png", TimeSpan.FromMinutes(5));
        AddFile("PrtScn Screenshots.lnk", TimeSpan.FromMinutes(5));
        AddFile(@"2026-11\VRChat_2026-11-21_21-34-55.168_3840x2160.jpg", TimeSpan.FromMinutes(5));

        var result = CreateScanner().CatchUp();

        Assert.Equal(1, result.Scanned);
        Assert.Equal([photo], Drain());
    }

    [Fact]
    public void Files_Already_In_State_Are_Skipped_Unless_Changed()
    {
        BindState(TimeSpan.FromHours(1));
        var uploaded = AddFile(Name1, TimeSpan.FromMinutes(5));
        var changed = AddFile(Name2, TimeSpan.FromMinutes(5));
        var skippedLayer = AddFile("VRChat_2026-11-21_21-35-02.420_3840x2160_Environment.png", TimeSpan.FromMinutes(5));
        RecordUploaded(uploaded);
        RecordUploaded(changed);
        var info = new FileInfo(skippedLayer);
        _state.Set(skippedLayer, new StateEntry { Size = info.Length, MtimeUtc = info.LastWriteTimeUtc, Skipped = StateEntry.SkippedLayer });
        File.AppendAllText(changed, "VRCX");
        File.SetLastWriteTimeUtc(changed, Now - TimeSpan.FromMinutes(4));

        CreateScanner().CatchUp();

        Assert.Equal([changed], Drain());
    }

    [Fact]
    public void Picture_Folder_Change_Does_Not_Mass_Upload()
    {
        var oldRoot = Path.Combine(_root, "old");
        var newRoot = Path.Combine(_root, "new");
        Directory.CreateDirectory(oldRoot);
        _state.BindToFolder(oldRoot, Now - TimeSpan.FromHours(1));
        _state.Set(Path.Combine(oldRoot, Name1), new StateEntry { Size = 1, MtimeUtc = Now, AssetId = "a" });
        AddFile(@"new\" + Name1, TimeSpan.FromMinutes(10));
        AddFile(@"new\2026-11\" + Name2, TimeSpan.FromMinutes(30));
        _clock.Delay(TimeSpan.FromSeconds(5), default);

        var result = CreateScanner().PictureFolderChanged(newRoot);

        Assert.True(result.FirstRun);
        Assert.Equal(2, result.Scanned);
        Assert.Empty(Drain());
        Assert.Equal(Path.GetFullPath(newRoot), _state.PictureFolder);
        Assert.Equal(Now, _state.LastScanUtc);
        Assert.Empty(_state.Entries);
    }

    [Fact]
    public void Startup_With_The_Same_Folder_Catches_Up()
    {
        BindState(TimeSpan.FromHours(1));
        var photo = AddFile(Name1, TimeSpan.FromMinutes(10));

        var result = CreateScanner().Startup(_root + Path.DirectorySeparatorChar);

        Assert.False(result.FirstRun);
        Assert.Equal([photo], Drain());
    }

    [Fact]
    public void Missing_Folder_Leaves_LastScan_Alone()
    {
        var missing = Path.Combine(_root, "missing");
        _state.BindToFolder(missing, Now);
        var lastScan = Now - TimeSpan.FromHours(1);
        _state.LastScanUtc = lastScan;
        _clock.Delay(TimeSpan.FromMinutes(1), default);

        var result = CreateScanner().CatchUp();

        Assert.True(result.FolderMissing);
        Assert.Equal(lastScan, _state.LastScanUtc);
        Assert.False(Directory.Exists(missing));
    }

    [Fact]
    public void Already_Queued_File_Is_Not_Queued_Twice()
    {
        BindState(TimeSpan.FromHours(1));
        var photo = AddFile(Name1, TimeSpan.FromMinutes(10));
        Assert.True(_queue.Enqueue(photo));

        var result = CreateScanner().CatchUp();

        Assert.Equal(0, result.Enqueued);
        Assert.Equal([photo], Drain());
    }

    [Fact]
    public async Task Background_Catch_Up_Runs()
    {
        BindState(TimeSpan.FromHours(1));
        var photo = AddFile(Name1, TimeSpan.FromMinutes(10));

        var result = await CreateScanner().CatchUpInBackground().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, result!.Enqueued);
        Assert.Equal([photo], Drain());
    }

    // ---- Backfill ----

    private sealed class SyncProgress : IProgress<BackfillProgress>
    {
        public List<BackfillProgress> Reports { get; } = [];
        public void Report(BackfillProgress value) { lock (Reports) Reports.Add(value); }
    }

    [Fact]
    public async Task Backfill_Uses_The_Bulk_Check()
    {
        BindState(TimeSpan.Zero);
        var onServer = AddFile(Name1, TimeSpan.FromDays(30));
        var newPhoto = AddFile(@"2026-10\" + Name2, TimeSpan.FromDays(20));
        var print = AddFile(@"2026-10\VRChat_2026-10-04_01-48-06.885_2048x1440.png", TimeSpan.FromDays(20));
        var layer = AddFile(@"2026-11\VRChat_2026-11-21_21-35-02.420_3840x2160_Player.png", TimeSpan.FromDays(1));
        var uploaded = AddFile(@"2026-11\" + Name3, TimeSpan.FromDays(1));
        AddFile(@"Prints\2026-11\" + Name4, TimeSpan.FromDays(1));
        _prints.Add(print);
        RecordUploaded(uploaded);
        _client.Existing[Sha1(onServer)] = "asset-existing";
        var progress = new SyncProgress();

        var result = await CreateScanner().BackfillAsync(progress).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(BackfillStatus.Ok, result.Status);
        Assert.Equal(2, result.Candidates);
        Assert.Equal(1, result.Enqueued);
        Assert.Equal(1, result.AlreadyPresent);
        Assert.Equal(0, result.Rejected);

        var check = Assert.Single(_client.BulkChecks);
        Assert.Equal([@"2026-10\" + Name2, Name1], check.Select(i => i.Id).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(Sha1(onServer), check.Single(i => i.Id == Name1).Sha1);
        Assert.Empty(_client.Uploads);

        Assert.Equal([newPhoto], Drain());
        var entry = _state.Get(onServer)!;
        Assert.Equal("asset-existing", entry.AssetId);
        Assert.Equal(Sha1(onServer), entry.Sha1);
        Assert.Equal(new FileInfo(onServer).Length, entry.Size);
        Assert.Null(_state.Get(newPhoto));
        Assert.Null(_state.Get(layer));
        Assert.True(_saves >= 1);

        Assert.Contains(progress.Reports, p => p.Phase == BackfillPhase.Hashing && p.Done == 1 && p.Total == 2);
        Assert.Contains(progress.Reports, p => p.Phase == BackfillPhase.Hashing && p.Done == 2 && p.Total == 2 && p.Text == "Checking existing photos 2/2");
        Assert.Equal(BackfillPhase.Done, progress.Reports[^1].Phase);
    }

    [Fact]
    public async Task Backfill_Follows_The_Print_And_Layer_Settings()
    {
        BindState(TimeSpan.Zero);
        var print = AddFile(@"2026-10\VRChat_2026-10-04_01-48-06.885_2048x1440.png", TimeSpan.FromDays(20));
        var layer = AddFile(@"2026-11\VRChat_2026-11-21_21-35-02.420_3840x2160_Player.png", TimeSpan.FromDays(1));
        _prints.Add(print);
        // Skipped as a print earlier, when the setting was on.
        var info = new FileInfo(print);
        _state.Set(print, new StateEntry { Size = info.Length, MtimeUtc = info.LastWriteTimeUtc, Skipped = StateEntry.SkippedPrint });
        _config = _config with { ExcludePrints = false, UploadLayers = true };

        var result = await CreateScanner().BackfillAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, result.Enqueued);
        Assert.Equal([print, layer], Drain());
        Assert.Null(_state.Get(print)); // the skip entry is gone, so the worker uploads it
    }

    [Fact]
    public async Task Backfill_Check_Failure_Queues_Nothing()
    {
        BindState(TimeSpan.Zero);
        AddFile(Name1, TimeSpan.FromDays(30));
        _client.BulkCheckFailure = ApiStatus.Transient;

        var result = await CreateScanner().BackfillAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(BackfillStatus.CheckFailed, result.Status);
        Assert.NotNull(result.Message);
        Assert.Empty(Drain());
        Assert.Empty(_state.Entries);
    }

    [Fact]
    public async Task Backfill_Without_A_Client_Reports_Not_Set_Up()
    {
        BindState(TimeSpan.Zero);
        AddFile(Name1, TimeSpan.FromDays(30));
        _currentClient = null;

        var result = await CreateScanner().BackfillAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(BackfillStatus.NotSetUp, result.Status);
        Assert.Empty(Drain());
    }

    [Fact]
    public async Task Count_Existing_Applies_The_Same_Rules()
    {
        BindState(TimeSpan.Zero);
        AddFile(Name1, TimeSpan.FromDays(30));
        AddFile(@"2026-10\" + Name2, TimeSpan.FromDays(20));
        var print = AddFile(@"2026-10\VRChat_2026-10-04_01-48-06.885_2048x1440.png", TimeSpan.FromDays(20));
        AddFile(@"2026-11\VRChat_2026-11-21_21-35-02.420_3840x2160_UI.png", TimeSpan.FromDays(1));
        var uploaded = AddFile(@"2026-11\" + Name3, TimeSpan.FromDays(1));
        AddFile(@"Stickers\2026-11\" + Name4, TimeSpan.FromDays(1));
        AddFile(@"2026-11\VRChat_2026-11-21_21-34-55.168_3840x2160 - Copy.png", TimeSpan.FromDays(1));
        _prints.Add(print);
        RecordUploaded(uploaded);
        var scanner = CreateScanner();

        Assert.Equal(2, await scanner.CountExistingAsync());

        _config = _config with { ExcludePrints = false, UploadLayers = true, StrictFileNames = false };
        Assert.Equal(5, await scanner.CountExistingAsync());
        Assert.Empty(_client.BulkChecks);
    }
}
