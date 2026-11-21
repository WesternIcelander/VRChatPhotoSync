namespace VRChatPhotoSync.Tests;

public sealed class UploadStateTests : IDisposable
{
    private const string Folder = @"D:\Photos\VRChat";
    private static readonly DateTime Now = new(2026, 11, 21, 21, 3, 8, 102, DateTimeKind.Utc);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PhotoSyncTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static UploadState SampleState()
    {
        var state = new UploadState { PictureFolder = Folder, LastScanUtc = Now.AddHours(-1), HeartbeatUtc = Now };
        state.Set(@"2026-11\VRChat_2026-11-21_21-03-08.102_3840x2160.png", new StateEntry
        {
            Size = 12_345_678,
            MtimeUtc = new DateTime(2026, 11, 21, 21, 3, 9, DateTimeKind.Utc).AddTicks(1234567),
            Sha1 = "0123456789abcdef0123456789abcdef01234567",
            AssetId = "11111111-2222-3333-4444-555555555555",
        });
        state.Set(@"2026-11\VRChat_2026-11-21_21-03-08.102_3840x2160_Player.png", new StateEntry
        {
            Size = 42,
            MtimeUtc = Now,
            Skipped = StateEntry.SkippedLayer,
        });
        state.Set(@"2026-11\VRChat_2026-11-04_01-48-06.885_2048x1440.png", new StateEntry
        {
            Size = 43,
            MtimeUtc = Now,
            Skipped = StateEntry.SkippedPrint,
        });
        state.Session = new SessionInfo
        {
            StartUtc = Now.AddMinutes(-30),
            Uploaded = 5,
            Duplicates = 1,
            Failed = 2,
            PrintsSkipped = 3,
            LayersSkipped = 4,
            LastFile = @"2026-11\VRChat_2026-11-21_21-03-08.102_3840x2160.png",
        };
        return state;
    }

    private static void AssertSameState(UploadState expected, UploadState actual)
    {
        Assert.Equal(expected.PictureFolder, actual.PictureFolder);
        Assert.Equal(expected.LastScanUtc, actual.LastScanUtc);
        Assert.Equal(expected.HeartbeatUtc, actual.HeartbeatUtc);
        Assert.Equal(expected.Session, actual.Session);
        Assert.Equal(expected.Entries.Count, actual.Entries.Count);
        foreach (var (key, entry) in expected.Entries)
            Assert.Equal(entry, actual.Entries[key]);
    }

    // ---- load / save ----

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new UploadStateStore(_dir);
        var state = SampleState();
        store.Save(state);

        var warnings = new List<string>();
        var loaded = store.Load(warnings.Add);
        Assert.Empty(warnings);
        AssertSameState(state, loaded);
        Assert.Equal(DateTimeKind.Utc, loaded.LastScanUtc!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, loaded.Entries.Values.First().MtimeUtc.Kind);
    }

    [Fact]
    public void Save_WritesCamelCaseJson_WithoutNulls()
    {
        var store = new UploadStateStore(_dir);
        store.Save(SampleState());
        var json = File.ReadAllText(store.StatePath);
        foreach (var name in new[] { "pictureFolder", "lastScanUtc", "entries", "size", "mtimeUtc", "sha1", "assetId",
                                     "skipped", "session", "startUtc", "uploaded", "duplicates", "failed",
                                     "printsSkipped", "layersSkipped", "lastFile", "heartbeatUtc" })
            Assert.Contains($"\"{name}\"", json);
        Assert.Contains("\"skipped\": \"print\"", json);
        Assert.Contains("\"skipped\": \"layer\"", json);
        Assert.DoesNotContain("null", json);
    }

    [Fact]
    public void Load_EntryKeys_AreCaseInsensitive()
    {
        var store = new UploadStateStore(_dir);
        store.Save(SampleState());
        var loaded = store.Load();
        Assert.True(loaded.IsUnchanged(@"2026-11\vrchat_2026-11-21_21-03-08.102_3840x2160_PLAYER.png", 42, Now));
    }

    [Fact]
    public void MissingFile_GivesFreshState_WithoutWarning()
    {
        var warnings = new List<string>();
        var state = new UploadStateStore(_dir).Load(warnings.Add);
        Assert.Empty(warnings);
        Assert.Null(state.PictureFolder);
        Assert.Null(state.LastScanUtc);
        Assert.Null(state.HeartbeatUtc);
        Assert.Empty(state.Entries);
        Assert.Equal(new SessionInfo(), state.Session);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"entries\": {\"a.png\": {\"size\": \"big\"}}}")]
    [InlineData("{\"lastScanUtc\": \"yesterday\"}")]
    public void CorruptFile_GivesFreshState_AndWarning(string content)
    {
        Directory.CreateDirectory(_dir);
        var store = new UploadStateStore(_dir);
        File.WriteAllText(store.StatePath, content);

        var warnings = new List<string>();
        var state = store.Load(warnings.Add);
        Assert.Single(warnings);
        Assert.Contains(store.StatePath, warnings[0]);
        Assert.Null(state.PictureFolder);
        Assert.Empty(state.Entries);
        Assert.NotNull(state.Session);
    }

    [Fact]
    public void CorruptFile_ThrowingLogger_StillGivesFreshState()
    {
        Directory.CreateDirectory(_dir);
        var store = new UploadStateStore(_dir);
        File.WriteAllText(store.StatePath, "{ not json");
        var state = store.Load(_ => throw new InvalidOperationException("log broken"));
        Assert.Empty(state.Entries);
    }

    [Fact]
    public void Load_NullSessionOrEntry_IsTolerated()
    {
        Directory.CreateDirectory(_dir);
        var store = new UploadStateStore(_dir);
        File.WriteAllText(store.StatePath, "{\"pictureFolder\":\"D:\\\\Photos\\\\VRChat\",\"session\":null,\"entries\":{\"a.png\":null}}");
        var state = store.Load();
        Assert.Equal(Folder, state.PictureFolder);
        Assert.NotNull(state.Session);
        Assert.Empty(state.Entries);
    }

    [Fact]
    public void Save_IsAtomic_ReplacesExistingFile_AndLeavesNoTemp()
    {
        var store = new UploadStateStore(_dir);
        var state = SampleState();
        store.Save(state);
        state.Session.Uploaded = 99;
        store.Save(state);

        Assert.Equal(99, store.Load().Session.Uploaded);
        Assert.Equal([UploadStateStore.FileName], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Save_KilledMidWrite_LeavesPreviousFileIntact()
    {
        var store = new UploadStateStore(_dir);
        var state = SampleState();
        store.Save(state);

        // A kill during the next save leaves a half-written temp file; state.json is untouched.
        File.WriteAllText(store.StatePath + ".tmp", "{\"pictureFolder\": \"D:\\\\Pho");
        var warnings = new List<string>();
        AssertSameState(state, store.Load(warnings.Add));
        Assert.Empty(warnings);

        // The next save overwrites the stale temp file and cleans it up.
        state.Session.Uploaded = 6;
        store.Save(state);
        Assert.Equal(6, store.Load().Session.Uploaded);
        Assert.False(File.Exists(store.StatePath + ".tmp"));
    }

    [Fact]
    public void Save_WhileFileOpenForReading_StillSucceedsOrKeepsOldFile()
    {
        // Readers open state.json briefly; a save must never leave a broken file behind.
        var store = new UploadStateStore(_dir);
        var state = SampleState();
        store.Save(state);
        state.Session.Uploaded = 7;
        using (new FileStream(store.StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            try { store.Save(state); } catch (IOException) { }
        }
        var loaded = store.Load();
        Assert.Contains(loaded.Session.Uploaded, new[] { 5, 7 });
        Assert.Equal(Folder, loaded.PictureFolder);
    }

    [Fact]
    public void Save_CreatesDirectory()
    {
        var nested = Path.Combine(_dir, "a", "b");
        var store = new UploadStateStore(nested);
        store.Save(new UploadState());
        Assert.True(File.Exists(Path.Combine(nested, "state.json")));
    }

    // ---- helpers ----

    [Fact]
    public void IsUnchanged_ComparesSizeAndMtime()
    {
        var state = SampleState();
        const string path = @"2026-11\VRChat_2026-11-21_21-03-08.102_3840x2160_Player.png";
        Assert.True(state.IsUnchanged(path, 42, Now));
        Assert.True(state.IsUnchanged(path, 42, Now.ToLocalTime()));
        Assert.False(state.IsUnchanged(path, 43, Now));
        Assert.False(state.IsUnchanged(path, 42, Now.AddTicks(1)));
        Assert.False(state.IsUnchanged(@"2026-11\other.png", 42, Now));
    }

    [Fact]
    public void IsUnchanged_AcceptsFullPathUnderFolder()
    {
        var state = SampleState();
        Assert.True(state.IsUnchanged(
            Path.Combine(Folder, "2026-11", "VRChat_2026-11-21_21-03-08.102_3840x2160_Player.png"), 42, Now));
        Assert.True(state.IsUnchanged("2026-11/VRChat_2026-11-21_21-03-08.102_3840x2160_Player.png", 42, Now));
    }

    [Fact]
    public void MoveEntry_RenamesKey()
    {
        var state = SampleState();
        const string oldPath = @"2026-11\VRChat_2026-11-21_21-03-08.102_3840x2160.png";
        const string newPath = @"2026-11\VRChat_2026-11-21_21-03-08.102_3840x2160_wrld_12345678-90ab-4cde-8f01-234567890abc.png";
        var entry = state.Get(oldPath);

        Assert.True(state.MoveEntry(oldPath, Path.Combine(Folder, newPath)));
        Assert.Null(state.Get(oldPath));
        Assert.Equal(entry, state.Get(newPath));
        Assert.Equal(3, state.Entries.Count);
    }

    [Fact]
    public void MoveEntry_UnknownOldPath_ReturnsFalse()
    {
        var state = SampleState();
        Assert.False(state.MoveEntry(@"2026-11\missing.png", @"2026-11\new.png"));
        Assert.Null(state.Get(@"2026-11\new.png"));
        Assert.Equal(3, state.Entries.Count);
    }

    [Fact]
    public void ResolveSession_HeartbeatThirtySecondsOld_Continues()
    {
        var state = SampleState();
        var before = state.Session;
        state.HeartbeatUtc = Now;

        var continued = state.ResolveSession(Now.AddSeconds(30));
        Assert.True(continued);
        Assert.Equal(before, state.Session);
        Assert.Equal(5, state.Session.Uploaded);
        Assert.Equal(Now.AddSeconds(30), state.HeartbeatUtc);
    }

    [Fact]
    public void ResolveSession_HeartbeatTenMinutesOld_StartsNewSession()
    {
        var state = SampleState();
        state.HeartbeatUtc = Now;

        var later = Now.AddMinutes(10);
        Assert.False(state.ResolveSession(later));
        Assert.Equal(new SessionInfo { StartUtc = later }, state.Session);
        Assert.Equal(later, state.HeartbeatUtc);
        Assert.Equal(3, state.Entries.Count); // entries are kept
    }

    [Theory]
    [InlineData(119, true)]
    [InlineData(120, false)]
    [InlineData(-60, false)] // heartbeat in the future (clock changed) → new session
    public void ResolveSession_Boundaries(int secondsSinceHeartbeat, bool expectContinue)
    {
        var state = SampleState();
        state.HeartbeatUtc = Now;
        Assert.Equal(expectContinue, state.ResolveSession(Now.AddSeconds(secondsSinceHeartbeat)));
    }

    [Fact]
    public void ResolveSession_NoHeartbeat_StartsNewSession()
    {
        var state = new UploadState();
        Assert.False(state.ResolveSession(Now));
        Assert.Equal(Now, state.Session.StartUtc);
        Assert.Equal(0, state.Session.Uploaded);
    }

    [Fact]
    public void ResolveSession_SurvivesSaveAndReload()
    {
        // Simulates a kill: save with a heartbeat, reload 30 s later, continue the counts.
        var store = new UploadStateStore(_dir);
        var state = SampleState();
        state.Heartbeat(Now);
        store.Save(state);

        var reloaded = store.Load();
        Assert.True(reloaded.ResolveSession(Now.AddSeconds(30)));
        Assert.Equal(5, reloaded.Session.Uploaded);
    }

    [Fact]
    public void BindToFolder_FreshState_IsFirstRun()
    {
        var state = new UploadState();
        Assert.True(state.BindToFolder(Folder, Now));
        Assert.Equal(Folder, state.PictureFolder);
        Assert.Equal(Now, state.LastScanUtc);
    }

    [Theory]
    [InlineData(@"D:\Photos\VRChat")]
    [InlineData(@"D:\Photos\VRChat\")]
    [InlineData(@"d:\photos\vrchat")]
    public void BindToFolder_SameFolder_KeepsState(string folder)
    {
        var state = SampleState();
        var lastScan = state.LastScanUtc;
        Assert.False(state.BindToFolder(folder, Now));
        Assert.Equal(lastScan, state.LastScanUtc);
        Assert.Equal(3, state.Entries.Count);
    }

    [Fact]
    public void BindToFolder_DifferentFolder_IsTreatedAsFirstRun()
    {
        var state = SampleState();
        var session = state.Session;
        Assert.True(state.BindToFolder(@"E:\Other\VRChat", Now));
        Assert.Equal(@"E:\Other\VRChat", state.PictureFolder);
        Assert.Equal(Now, state.LastScanUtc);
        Assert.Empty(state.Entries);
        Assert.Equal(session, state.Session); // counters are not reset by a folder change
    }

    [Fact]
    public void BindToFolder_AfterReload_WithChangedFolder_IsFirstRun()
    {
        var store = new UploadStateStore(_dir);
        store.Save(SampleState());
        var reloaded = store.Load();
        Assert.True(reloaded.BindToFolder(@"E:\Other\VRChat", Now));
        Assert.Empty(reloaded.Entries);
    }
}
