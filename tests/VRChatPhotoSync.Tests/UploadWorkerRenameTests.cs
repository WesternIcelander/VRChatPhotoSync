using System.Security.Cryptography;

namespace VRChatPhotoSync.Tests;

/// <summary>Renames (VRCX's "modify filename") and late VRCX edits of files that were already uploaded.</summary>
public sealed partial class UploadWorkerTests
{
    private const string WorldName = Month + @"\VRChat_2026-11-21_21-34-55.168_3840x2160_wrld_12345678-90ab-4cde-8f01-234567890abc.png";
    private const string BareName = Month + @"\VRChat_2026-11-21_21-34-55.168_3840x2160_.png";
    private const string CopyName = Month + @"\VRChat_2026-11-21_21-34-55.168_3840x2160 - Copy.png";

    /// <summary>Queues the paths, completes the queue and runs an already created worker to the end.</summary>
    private async Task Run(UploadWorker worker, params string[] paths)
    {
        foreach (var p in paths)
            Assert.True(_queue.Enqueue(p));
        _queue.Complete();
        await worker.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Renames the fake file (keeping its content and times) and reports it like the folder watcher does.</summary>
    private void RenameFile(UploadWorker worker, string oldPath, string newPath)
    {
        _probe.Rename(oldPath, newPath);
        _contents.TryRemove(oldPath, out var bytes);
        _contents[newPath] = bytes!;
        worker.NotifyRenamed(oldPath, newPath);
    }

    /// <summary>VRCX adds its <c>Description</c> chunk (the file grows and gets new content).</summary>
    private void AddChunk(string path)
    {
        _probe.AddChunk(path);
        _contents[path] = RandomNumberGenerator.GetBytes((int)_probe.TryStat(path)!.Value.Length);
    }

    /// <summary>A file that was uploaded before as <paramref name="assetId"/>, and that VRCX has since changed.</summary>
    private void SeedUploadedThenChanged(string path, string assetId = "old")
    {
        AddFile(path);
        var stat = _probe.TryStat(path)!.Value;
        _state.Set(path, new StateEntry { Size = stat.Length, MtimeUtc = stat.LastWriteUtc, Sha1 = "before", AssetId = assetId });
        AddChunk(path);
    }

    // ---- Renames ----

    [Theory]
    [InlineData(WorldName)]
    [InlineData(BareName)]
    public async Task Rename_During_Settle_Uploads_Only_The_New_Name(string newPath)
    {
        AddFile(Photo1);
        _config = _config with { VrcxWaitSeconds = 10 };
        var worker = CreateWorker();
        // Readiness takes 0.5 s, then the settle starts. VRCX renames first, then writes the chunk (as observed).
        _clock.MarkStart();
        _clock.At(TimeSpan.FromSeconds(1.0), () => RenameFile(worker, Photo1, newPath));
        _clock.At(TimeSpan.FromSeconds(1.3), () => AddChunk(newPath));

        await Run(worker, Photo1);

        var upload = Assert.Single(_client.Uploads);
        Assert.Equal(Path.GetFileName(newPath), upload.FileName);
        Assert.Equal(_contents[newPath], upload.Content); // the version with VRCX's chunk
        Assert.Equal(1201, upload.Content.Length);
        Assert.Null(_state.Get(Photo1));
        Assert.Equal("asset-1", _state.Get(newPath)!.AssetId);
        var evt = Single(UploadOutcome.Uploaded);
        Assert.Equal(newPath, evt.Path);
        Assert.Equal(@"2026-11\" + Path.GetFileName(newPath), _state.Session.LastFile);
        Assert.Contains(_log, l => l.Contains("MetadataAppeared", StringComparison.Ordinal) && l.Contains("renamed from", StringComparison.Ordinal));
        Assert.True(_clock.UtcNow - _clock.Start < TimeSpan.FromSeconds(3), "followed the rename instead of timing out");
    }

    [Fact]
    public async Task Rename_During_Readiness_Is_Followed()
    {
        AddFile(Photo1);
        _config = _config with { VrcxWaitSeconds = 10 };
        var worker = CreateWorker();
        _clock.MarkStart();
        _clock.At(TimeSpan.FromSeconds(0.2), () => RenameFile(worker, Photo1, WorldName));
        _clock.At(TimeSpan.FromSeconds(1.2), () => AddChunk(WorldName));

        await Run(worker, Photo1);

        Assert.Equal([Path.GetFileName(WorldName)], _client.UploadedNames);
        Assert.Equal(1201, Assert.Single(_client.Uploads).Content.Length);
        Assert.NotNull(_state.Get(WorldName));
        Assert.Null(_state.Get(Photo1));
    }

    [Fact]
    public async Task Rename_While_Queued_Keeps_The_Place_Under_The_New_Name()
    {
        AddFile(Photo1);
        AddFile(Photo2);
        var worker = CreateWorker();
        Assert.True(_queue.Enqueue(Photo1));
        Assert.True(_queue.Enqueue(Photo2));

        RenameFile(worker, Photo1, BareName);
        // The watcher follows a rename with FileAppeared for the new name; it is already queued.
        Assert.False(_queue.Enqueue(BareName));
        Assert.False(_queue.Contains(Photo1));
        Assert.True(_queue.Contains(BareName));
        Assert.Equal(2, _queue.Count);

        await Run(worker);

        Assert.Equal([Path.GetFileName(BareName), Path.GetFileName(Photo2)], _client.UploadedNames);
        Assert.Null(_state.Get(Photo1));
    }

    [Fact]
    public async Task Rename_To_A_Name_That_Is_Not_Uploaded_Drops_The_File()
    {
        AddFile(Photo1);
        _config = _config with { VrcxWaitSeconds = 10 };
        var worker = CreateWorker();
        _clock.MarkStart();
        _clock.At(TimeSpan.FromSeconds(1.0), () => RenameFile(worker, Photo1, CopyName));

        await Run(worker, Photo1);

        Assert.Empty(_client.Uploads);
        Assert.Empty(_state.Entries);
    }

    [Fact]
    public async Task Rename_After_Upload_Moves_The_State_Entry()
    {
        AddFile(Photo1);
        var worker = CreateWorker();
        var run = worker.RunAsync();
        _queue.Enqueue(Photo1);
        await WaitFor(() => { lock (_events) return _events.Count == 1; });
        var saves = _saves;

        RenameFile(worker, Photo1, WorldName);
        Assert.Null(_state.Get(Photo1));
        Assert.Equal("asset-1", _state.Get(WorldName)!.AssetId);
        Assert.True(_saves > saves, "the moved entry is saved");

        // The watcher then reports the new name; it is the same photo, so nothing is uploaded.
        _queue.Enqueue(WorldName);
        await WaitFor(() => { lock (_events) return _events.Count == 2; });
        _queue.Complete();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Single(_client.Uploads);
        Assert.Equal(SkipReason.Unchanged, _events[1].Skip);
        Assert.Equal(WorldName, _events[1].Path);
        Assert.Equal(1, _state.Session.Uploaded);
    }

    [Fact]
    public void Rename_Of_An_Unknown_File_Changes_Nothing()
    {
        var worker = CreateWorker();
        Assert.False(worker.NotifyRenamed(Photo1, WorldName));
        Assert.Equal(0, _saves);
        Assert.Empty(_state.Entries);
    }

    // ---- Late VRCX edits ----

    [Theory]
    [InlineData("all")]
    [InlineData("asset.upload,asset.copy,asset.delete")]
    public async Task Changed_After_Upload_Replaces_The_Old_Asset_When_Permitted(string permissions)
    {
        SeedUploadedThenChanged(Photo1);
        _client.Permissions = permissions.Split(',');

        await RunAll(Photo1);

        var upload = Assert.Single(_client.Uploads);
        Assert.Equal(1201, upload.Content.Length);
        Assert.Equal(["key", "copy", "trash"], _client.ReplaceCalls);
        Assert.Equal(("old", "asset-1"), Assert.Single(_client.Copies));
        Assert.Equal(new[] { "old" }, Assert.Single(_client.Trashes));

        var evt = Single(UploadOutcome.Updated);
        Assert.Equal($"Updated {Path.GetFileName(Photo1)} (VRCX metadata)", evt.Detail);
        Assert.Equal("asset-1", evt.AssetId);
        Assert.Equal("old", evt.PreviousAssetId);
        Assert.True(evt.Replaced);
        Assert.DoesNotContain(_events, e => e.Outcome == UploadOutcome.Uploaded);

        var entry = _state.Get(Photo1)!;
        Assert.Equal("asset-1", entry.AssetId);
        Assert.Equal(1201, entry.Size);
        Assert.Equal(upload.Sha1, entry.Sha1);
        Assert.Equal(0, _state.Session.Uploaded); // not a new photo
        Assert.Equal(0, _state.Session.Duplicates);
        Assert.Contains(_log, l => l.Contains("Replaced asset old", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("asset.upload")]
    [InlineData("asset.upload,asset.copy")]
    [InlineData("asset.upload,asset.delete")]
    public async Task Changed_After_Upload_Keeps_Both_Versions_Without_Permissions(string permissions)
    {
        SeedUploadedThenChanged(Photo1);
        _client.Permissions = permissions.Split(',');

        await RunAll(Photo1);

        Assert.Single(_client.Uploads);
        Assert.Equal(["key"], _client.ReplaceCalls);
        var evt = Single(UploadOutcome.Updated);
        Assert.False(evt.Replaced);
        Assert.Equal("old", evt.PreviousAssetId);
        Assert.Equal("asset-1", _state.Get(Photo1)!.AssetId);
        Assert.Equal(0, _state.Session.Uploaded);
        Assert.Contains(_log, l => l.Contains("Kept both versions", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("old")]
    [InlineData("other")]
    public async Task Changed_After_Upload_With_Duplicate_Result_Does_Not_Copy_Or_Trash(string duplicateId)
    {
        SeedUploadedThenChanged(Photo1);
        _client.Permissions = [ApiKeyInfo.PermissionAll];
        _client.Then(UploadResult.Duplicate(duplicateId));

        await RunAll(Photo1);

        Assert.Single(_client.Uploads);
        Assert.Empty(_client.ReplaceCalls);
        var evt = Single(UploadOutcome.Updated);
        Assert.False(evt.Replaced);
        Assert.Equal(duplicateId, _state.Get(Photo1)!.AssetId);
        Assert.Equal(1201, _state.Get(Photo1)!.Size);
        Assert.Equal(0, _state.Session.Duplicates);
        Assert.Equal(0, _state.Session.Uploaded);
    }

    [Fact]
    public async Task Failed_Copy_Does_Not_Trash()
    {
        SeedUploadedThenChanged(Photo1);
        _client.Permissions = [ApiKeyInfo.PermissionAll];
        _client.CopyResult = new ApiResult(ApiStatus.Rejected, 400, "Not found");

        await RunAll(Photo1);

        Assert.Equal(["key", "copy"], _client.ReplaceCalls);
        Assert.False(Single(UploadOutcome.Updated).Replaced);
        Assert.Equal("asset-1", _state.Get(Photo1)!.AssetId);
        Assert.Contains(_log, l => l.Contains("kept both versions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failed_Key_Check_Keeps_Both_Versions()
    {
        SeedUploadedThenChanged(Photo1);
        _client.KeyStatus = ApiKeyCheckStatus.Unreachable;

        await RunAll(Photo1);

        Assert.Equal(["key"], _client.ReplaceCalls);
        Assert.False(Single(UploadOutcome.Updated).Replaced);
        Assert.Equal("asset-1", _state.Get(Photo1)!.AssetId);
    }

    [Fact]
    public async Task Changed_Skipped_File_Is_A_Normal_Upload()
    {
        AddFile(Print);
        _state.Set(Print, new StateEntry { Size = 999, MtimeUtc = _clock.UtcNow, Skipped = StateEntry.SkippedPrint });
        _config = _config with { ExcludePrints = false };

        await RunAll(Print);

        Assert.Empty(_client.ReplaceCalls);
        Assert.Equal(UploadOutcome.Uploaded, Assert.Single(_events).Outcome);
        Assert.Equal(1, _state.Session.Uploaded);
    }

    [Fact]
    public async Task Late_VRCX_Edit_While_Running_Uploads_Again_And_Replaces()
    {
        AddFile(Photo1);
        _client.Permissions = [ApiKeyInfo.PermissionAll];
        _config = _config with { AlbumName = "VRChat" };
        var worker = CreateWorker();
        var run = worker.RunAsync();
        _queue.Enqueue(Photo1);
        await WaitFor(() => { lock (_events) return _events.Count == 1; });
        Assert.Equal(UploadOutcome.Uploaded, _events[0].Outcome);

        // VRCX was later than the wait: the chunk arrives after the upload, and the watcher reports the change.
        AddChunk(Photo1);
        _queue.Enqueue(Photo1);
        await WaitFor(() => { lock (_events) return _events.Count == 2; });
        _queue.Complete();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        var evt = _events[1];
        Assert.Equal(UploadOutcome.Updated, evt.Outcome);
        Assert.Equal("asset-2", evt.AssetId);
        Assert.Equal("asset-1", evt.PreviousAssetId);
        Assert.True(evt.Replaced);
        Assert.Equal(("asset-1", "asset-2"), Assert.Single(_client.Copies));
        Assert.Equal(new[] { "asset-1" }, Assert.Single(_client.Trashes));
        Assert.Equal(2, _client.AlbumAdds.Count); // the new asset joins the album as well
        Assert.Equal("asset-2", _state.Get(Photo1)!.AssetId);
        Assert.Equal(1, _state.Session.Uploaded);
    }
}
