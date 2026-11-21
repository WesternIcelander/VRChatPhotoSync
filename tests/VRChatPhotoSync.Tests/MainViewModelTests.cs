namespace VRChatPhotoSync.Tests;

public class MainViewModelTests
{
    private static readonly string Root = Path.GetFullPath(@"D:\Photos\VRChat");
    private DateTime _now = new(2026, 11, 21, 20, 0, 0, DateTimeKind.Utc);

    private static AppConfig SetUpConfig => new()
    {
        ServerUrl = "https://immich.example.com/api",
        ApiKeyProtected = "protected-key",
    };

    private MainViewModel Create(AppConfig? config = null) => new(config ?? SetUpConfig, Root, () => _now);

    private static string Photo(string name = "VRChat_2026-11-21_21-03-08.102_3840x2160.png") =>
        Path.Combine(Root, "2026-11", name);

    private static UploadEvent Event(UploadOutcome outcome, string? path = null, SkipReason? skip = null, string? detail = null)
    {
        path ??= Photo();
        return new UploadEvent(outcome, path, Path.GetRelativePath(Root, path), Skip: skip, Detail: detail);
    }

    [Fact]
    public void Fresh_ShowsWatchingZeroCountersAndNoLastFile()
    {
        var view = Create().Snapshot();
        Assert.Equal("PhotoSync — 0 uploaded", view.Title);
        Assert.Equal(MainViewModel.WatchingText, view.StatusText);
        Assert.Equal(Root, view.StatusToolTip);
        Assert.Equal("Uploaded this session: 0", view.CountersText);
        Assert.Equal("Last: —", view.LastText);
        Assert.Null(view.LastFilePath);
        Assert.Null(view.QueueText);
        Assert.Equal("Skip prints (0 skipped)", view.SkipPrintsText);
        Assert.Equal("Upload photo layers", view.UploadLayersText);
        Assert.True(view.ExcludePrints);
        Assert.False(view.UploadLayers);
    }

    [Theory]
    [InlineData(null, "key")]
    [InlineData("https://immich.example.com", null)]
    [InlineData("not a url", "key")]
    public void MissingServerOrKey_ShowsNotSetUp(string? url, string? key)
    {
        var vm = Create(new AppConfig { ServerUrl = url, ApiKeyProtected = key });
        Assert.Equal(MainViewModel.NotSetUpText, vm.Snapshot().StatusText);
    }

    [Fact]
    public void ApplyConfig_AfterSetup_LeavesNotSetUpAndUpdatesCheckboxes()
    {
        var vm = Create(new AppConfig());
        var changed = 0;
        vm.Changed += () => changed++;

        vm.ApplyConfig(SetUpConfig with { ExcludePrints = false, UploadLayers = true });

        var view = vm.Snapshot();
        Assert.Equal(MainViewModel.WatchingText, view.StatusText);
        Assert.False(view.ExcludePrints);
        Assert.True(view.UploadLayers);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void WorkerPausedNotSetUp_ShowsNotSetUp()
    {
        var vm = Create();
        vm.SetWorkerStatus(new WorkerStatus(WorkerState.PausedNotSetUp, "Not set up"));
        Assert.Equal(MainViewModel.NotSetUpText, vm.Snapshot().StatusText);
    }

    [Fact]
    public void KeyRejected_ShowsPaused()
    {
        var vm = Create();
        vm.SetWorkerStatus(new WorkerStatus(WorkerState.PausedAuth, "x", Photo()));
        Assert.Equal(MainViewModel.KeyRejectedText, vm.Snapshot().StatusText);
    }

    [Fact]
    public void WaitingForFolder_ShowsFolderNameAndFullPathInToolTip()
    {
        var vm = Create();
        vm.SetFolderState(FolderWatchState.WaitingForFolder);
        var view = vm.Snapshot();
        Assert.Equal("Waiting for folder VRChat…", view.StatusText);
        Assert.Contains(Root, view.StatusToolTip);

        vm.SetFolderState(FolderWatchState.Watching);
        Assert.Equal(MainViewModel.WatchingText, vm.Snapshot().StatusText);
    }

    [Fact]
    public void WaitingForVrcx_ShowsCount()
    {
        var vm = Create();
        vm.SetWorkerStatus(new WorkerStatus(WorkerState.WaitingForVrcx, "x", Waiting: 3));
        Assert.Equal("Waiting for VRCX (3)", vm.Snapshot().StatusText);
    }

    [Fact]
    public void Uploading_ShowsFileName()
    {
        var vm = Create();
        vm.SetWorkerStatus(new WorkerStatus(WorkerState.Uploading, "x", Photo()));
        var view = vm.Snapshot();
        Assert.Equal("Uploading VRChat_2026-11-21_21-03-08.102_3840x2160.png", view.StatusText);
        Assert.Equal(Photo(), view.StatusToolTip);
    }

    [Fact]
    public void Retrying_CountsDown()
    {
        var vm = Create();
        Assert.False(vm.NeedsTick);
        vm.SetWorkerStatus(new WorkerStatus(WorkerState.Retrying, "x", Photo(), TimeSpan.FromSeconds(15)));
        Assert.True(vm.NeedsTick);
        Assert.Equal("Retrying in 15s", vm.Snapshot().StatusText);

        _now = _now.AddSeconds(4.5);
        var changed = 0;
        vm.Changed += () => changed++;
        vm.Tick();
        Assert.Equal(1, changed);
        Assert.Equal("Retrying in 11s", vm.Snapshot().StatusText);

        _now = _now.AddSeconds(20);
        Assert.Equal("Retrying…", vm.Snapshot().StatusText);

        vm.SetWorkerStatus(WorkerStatus.Idle);
        Assert.False(vm.NeedsTick);
        changed = 0;
        vm.Tick();
        Assert.Equal(0, changed);
        Assert.Equal(MainViewModel.WatchingText, vm.Snapshot().StatusText);
    }

    [Fact]
    public void Uploading_TakesPriorityOverWaitingForVrcxAndFolder()
    {
        var vm = Create();
        vm.SetFolderState(FolderWatchState.WaitingForFolder);
        vm.SetWorkerStatus(new WorkerStatus(WorkerState.Uploading, "x", Photo(), Waiting: 2));
        Assert.StartsWith("Uploading ", vm.Snapshot().StatusText);
    }

    [Fact]
    public void Notice_ReplacesStatusUntilCleared()
    {
        var vm = Create();
        vm.SetNotice("Error: something broke");
        Assert.Equal("Error: something broke", vm.Snapshot().StatusText);
        vm.SetNotice(null);
        Assert.Equal(MainViewModel.WatchingText, vm.Snapshot().StatusText);
    }

    [Fact]
    public void Backfill_ShowsProgressWhileIdle()
    {
        var vm = Create();
        vm.SetBackfill(new BackfillProgress(BackfillPhase.Hashing, 3, 10));
        Assert.Equal("Checking existing photos 3/10", vm.Snapshot().StatusText);
        vm.SetBackfill(new BackfillProgress(BackfillPhase.Done, 10, 10));
        Assert.Equal(MainViewModel.WatchingText, vm.Snapshot().StatusText);
    }

    [Fact]
    public void Uploaded_CountsAndSetsLastAndTitle()
    {
        var vm = Create();
        vm.Record(Event(UploadOutcome.Uploaded));
        vm.Record(Event(UploadOutcome.Uploaded, Photo("VRChat_2026-11-21_21-05-58.427_3840x2160.png")));

        var view = vm.Snapshot();
        Assert.Equal("PhotoSync — 2 uploaded", view.Title);
        Assert.Equal("Uploaded this session: 2", view.CountersText);
        Assert.Equal("Last: VRChat_2026-11-21_21-05-58.427_3840x2160.png", view.LastText);
        Assert.Equal(Photo("VRChat_2026-11-21_21-05-58.427_3840x2160.png"), view.LastFilePath);
    }

    [Fact]
    public void DuplicatesAndFailures_AppearOnlyWhenPresent()
    {
        var vm = Create();
        vm.Record(Event(UploadOutcome.Uploaded));
        vm.Record(Event(UploadOutcome.Duplicate));
        Assert.Equal("Uploaded this session: 1 (1 duplicate)", vm.Snapshot().CountersText);

        vm.Record(Event(UploadOutcome.Duplicate));
        vm.Record(Event(UploadOutcome.Failed, detail: "bad"));
        var view = vm.Snapshot();
        Assert.Equal("Uploaded this session: 1 (2 duplicates, 1 failed)", view.CountersText);
        Assert.Equal("PhotoSync — 1 uploaded", view.Title);
    }

    [Fact]
    public void OnlyFailures_ShowFailedCount()
    {
        var vm = Create();
        vm.Record(Event(UploadOutcome.Failed));
        Assert.Equal("Uploaded this session: 0 (1 failed)", vm.Snapshot().CountersText);
        Assert.Equal("Last: —", vm.Snapshot().LastText);
    }

    [Fact]
    public void Skips_CountInCheckboxTexts()
    {
        var vm = Create();
        vm.Record(Event(UploadOutcome.Skipped, skip: SkipReason.Print));
        vm.Record(Event(UploadOutcome.Skipped, skip: SkipReason.Print));
        vm.Record(Event(UploadOutcome.Skipped, skip: SkipReason.Layer));
        vm.Record(Event(UploadOutcome.Skipped, skip: SkipReason.Unchanged));

        var view = vm.Snapshot();
        Assert.Equal("Skip prints (2 skipped)", view.SkipPrintsText);
        Assert.Equal("Upload photo layers (1 skipped)", view.UploadLayersText);
        Assert.Equal("Uploaded this session: 0", view.CountersText);
    }

    [Fact]
    public void UnchangedSkip_DoesNotRaiseChanged()
    {
        var vm = Create();
        var changed = 0;
        vm.Changed += () => changed++;
        vm.Record(Event(UploadOutcome.Skipped, skip: SkipReason.Unchanged));
        Assert.Equal(0, changed);
    }

    [Fact]
    public void Updated_IsNotCountedAndShowsTheUpdateText()
    {
        var vm = Create();
        vm.Record(Event(UploadOutcome.Updated, detail: "Updated VRChat_2026-11-21_21-03-08.102_3840x2160.png (VRCX metadata)"));
        var view = vm.Snapshot();
        Assert.Equal("Uploaded this session: 0", view.CountersText);
        Assert.Equal("Last: Updated VRChat_2026-11-21_21-03-08.102_3840x2160.png (VRCX metadata)", view.LastText);
        Assert.Equal(Photo(), view.LastFilePath);

        vm.Record(Event(UploadOutcome.Uploaded, Photo("VRChat_2026-11-21_21-35-02.420_3840x2160.png")));
        Assert.Equal("Last: VRChat_2026-11-21_21-35-02.420_3840x2160.png", vm.Snapshot().LastText);
    }

    [Fact]
    public void LoadSession_ContinuesCountersAndResolvesLastFile()
    {
        var vm = Create();
        vm.LoadSession(new SessionInfo
        {
            Uploaded = 7, Duplicates = 1, Failed = 2, PrintsSkipped = 3, LayersSkipped = 4,
            LastFile = Path.Combine("2026-11", "VRChat_2026-11-21_21-03-08.102_3840x2160.png"),
        });

        var view = vm.Snapshot();
        Assert.Equal("PhotoSync — 7 uploaded", view.Title);
        Assert.Equal("Uploaded this session: 7 (1 duplicate, 2 failed)", view.CountersText);
        Assert.Equal("Skip prints (3 skipped)", view.SkipPrintsText);
        Assert.Equal("Upload photo layers (4 skipped)", view.UploadLayersText);
        Assert.Equal(Photo(), view.LastFilePath);
        Assert.Equal("Last: VRChat_2026-11-21_21-03-08.102_3840x2160.png", view.LastText);

        vm.Record(Event(UploadOutcome.Uploaded));
        Assert.Equal("PhotoSync — 8 uploaded", vm.Snapshot().Title);
    }

    [Fact]
    public void QueueLength_ShownOnlyWhenPositive()
    {
        var vm = Create();
        var changed = 0;
        vm.Changed += () => changed++;
        vm.SetQueueLength(4);
        Assert.Equal("Queued: 4", vm.Snapshot().QueueText);
        vm.SetQueueLength(4);
        Assert.Equal(1, changed);
        vm.SetQueueLength(0);
        Assert.Null(vm.Snapshot().QueueText);
    }

    [Fact]
    public void Rename_OfLastFile_FollowsIt()
    {
        var vm = Create();
        var original = Photo("VRChat_2026-11-21_21-39-01.718_3840x2160.png");
        var renamed = Photo("VRChat_2026-11-21_21-39-01.718_3840x2160_wrld_12345678-90ab-4cde-8f01-234567890abc.png");
        vm.Record(Event(UploadOutcome.Uploaded, original));

        vm.NotifyRenamed(Photo("other.png"), Photo("other2.png"));
        Assert.Equal(original, vm.Snapshot().LastFilePath);

        vm.NotifyRenamed(original.ToUpperInvariant(), renamed);
        Assert.Equal(renamed, vm.Snapshot().LastFilePath);
        Assert.EndsWith("_wrld_12345678-90ab-4cde-8f01-234567890abc.png", vm.Snapshot().LastText);
    }

    [Fact]
    public void ConcurrentRecords_AreAllCounted()
    {
        var vm = Create();
        Parallel.For(0, 1000, _ => vm.Record(Event(UploadOutcome.Uploaded)));
        Assert.Equal("PhotoSync — 1000 uploaded", vm.Snapshot().Title);
    }

    [Theory]
    [InlineData("https://immich.example.com/api", "https://immich.example.com/")]
    [InlineData("https://immich.example.com", "https://immich.example.com/")]
    [InlineData("https://example.com/immich/api/", "https://example.com/immich/")]
    [InlineData(null, null)]
    [InlineData("not a url", null)]
    public void ServerRoot_DropsApiSuffix(string? url, string? expected)
    {
        Assert.Equal(expected, MainViewModel.ServerRoot(url));
    }
}
