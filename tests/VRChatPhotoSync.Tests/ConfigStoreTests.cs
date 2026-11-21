namespace VRChatPhotoSync.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoSyncTests", Guid.NewGuid().ToString("N"));
    private readonly string _exeDir;
    private readonly string _appDataDir;

    public ConfigStoreTests()
    {
        _exeDir = Path.Combine(_root, "exe");
        _appDataDir = Path.Combine(_root, "appdata", ConfigStore.AppDataFolderName);
        Directory.CreateDirectory(_exeDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void WritableExeDir_IsUsed()
    {
        var store = new ConfigStore(_exeDir, _appDataDir);
        Assert.False(store.UsesAppDataFallback);
        Assert.Equal(Path.Combine(_exeDir, "config.json"), store.ConfigPath);
        Assert.Empty(Directory.GetFiles(_exeDir)); // the write probe cleans up after itself
    }

    [Fact]
    public void UnwritableExeDir_FallsBackToAppData()
    {
        var missing = Path.Combine(_root, "does-not-exist");
        var store = new ConfigStore(missing, _appDataDir);
        Assert.True(store.UsesAppDataFallback);
        Assert.Equal(Path.Combine(_appDataDir, "config.json"), store.ConfigPath);

        store.Save(new AppConfig { AlbumName = "VRChat" });
        Assert.True(File.Exists(Path.Combine(_appDataDir, "config.json")));
        Assert.Equal("VRChat", store.Load().Config.AlbumName);
    }

    [Fact]
    public void MissingFile_GivesDefaults()
    {
        var result = new ConfigStore(_exeDir, _appDataDir).Load();
        Assert.False(result.Exists);
        Assert.Null(result.Error);
        Assert.Equal(new AppConfig(), result.Config);
        Assert.False(result.HasValidServerUrl);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new ConfigStore(_exeDir, _appDataDir);
        var config = new AppConfig
        {
            ServerUrl = "https://immich.example.com/api",
            ApiKeyProtected = "AQAAANCMnd8=",
            AlbumName = "VRChat",
            Visibility = AssetVisibility.Archive,
            ExcludePrints = false,
            UploadLayers = true,
            StrictFileNames = false,
            VrcxWaitSeconds = 0,
            CloseButtonAction = CloseButtonAction.HideToTray,
            MinimizeButtonAction = MinimizeButtonAction.Tray,
            StartMinimized = true,
        };
        store.Save(config);

        var result = store.Load();
        Assert.True(result.Exists);
        Assert.Null(result.Error);
        Assert.Equal(config, result.Config);
        Assert.True(result.HasValidServerUrl);
        Assert.False(File.Exists(store.ConfigPath + ".tmp"));
        Assert.False(Directory.Exists(_appDataDir));
    }

    [Fact]
    public void Save_OverwritesExistingFile()
    {
        var store = new ConfigStore(_exeDir, _appDataDir);
        store.Save(new AppConfig { AlbumName = "First" });
        store.Save(new AppConfig { AlbumName = "Second" });
        Assert.Equal("Second", store.Load().Config.AlbumName);
    }

    [Fact]
    public void Save_WritesReadableJson()
    {
        var store = new ConfigStore(_exeDir, _appDataDir);
        store.Save(new AppConfig { ServerUrl = "https://immich.example.com/api" });
        var json = File.ReadAllText(store.ConfigPath);
        Assert.Contains("\"ServerUrl\": \"https://immich.example.com/api\"", json);
        Assert.Contains("\"timeline\"", json);
        Assert.Contains(Environment.NewLine, json);
        Assert.DoesNotContain("HasAlbum", json);
    }

    [Fact]
    public void Load_PartialFile_UsesDefaultsForMissingFields()
    {
        File.WriteAllText(Path.Combine(_exeDir, "config.json"),
            "{ // comment\n \"AlbumName\": \"VRChat\", }");
        var result = new ConfigStore(_exeDir, _appDataDir).Load();
        Assert.Null(result.Error);
        Assert.Equal(new AppConfig { AlbumName = "VRChat" }, result.Config);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{\"Visibility\": \"nonsense\"}")]
    public void Load_InvalidFile_GivesDefaultsAndError(string content)
    {
        File.WriteAllText(Path.Combine(_exeDir, "config.json"), content);
        var result = new ConfigStore(_exeDir, _appDataDir).Load();
        Assert.True(result.Exists);
        Assert.NotNull(result.Error);
        Assert.Equal(new AppConfig(), result.Config);
    }

    [Fact]
    public void Fallback_WithNoFileYet_ReadsExeDirFileAsStartingPoint()
    {
        // An exe dir that holds a config.json but can't be written to (e.g. Program Files).
        var seedDir = Path.Combine(_root, "seed");
        Directory.CreateDirectory(seedDir);
        File.WriteAllText(Path.Combine(seedDir, "config.json"), "{\"AlbumName\":\"Seed\"}");
        var store = new ConfigStore(seedDir, _appDataDir, isWritable: _ => false);
        Assert.True(store.UsesAppDataFallback);

        var result = store.Load();
        Assert.Equal(Path.Combine(seedDir, "config.json"), result.Path);
        Assert.Equal("Seed", result.Config.AlbumName);

        store.Save(result.Config with { AlbumName = "Changed" });
        Assert.Equal("Changed", store.Load().Config.AlbumName);
        Assert.Equal("{\"AlbumName\":\"Seed\"}", File.ReadAllText(Path.Combine(seedDir, "config.json")));
    }

    [Theory]
    [InlineData("ftp://immich.example.com")]
    [InlineData("immich.example.com")]
    [InlineData("/api")]
    [InlineData("file:///C:/temp")]
    [InlineData("not a url")]
    public void Save_InvalidServerUrl_Throws(string url)
    {
        var store = new ConfigStore(_exeDir, _appDataDir);
        Assert.Throws<ArgumentException>(() => store.Save(new AppConfig { ServerUrl = url }));
        Assert.False(File.Exists(store.ConfigPath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Save_NoServerUrl_IsAllowed(string? url)
    {
        var store = new ConfigStore(_exeDir, _appDataDir);
        store.Save(new AppConfig { ServerUrl = url });
        Assert.True(File.Exists(store.ConfigPath));
    }

    [Theory]
    [InlineData("https://immich.example.com", true)]
    [InlineData("https://immich.example.com/api", true)]
    [InlineData("http://192.168.1.10:2283/api", true)]
    [InlineData("HTTPS://IMMICH.EXAMPLE.COM/", true)]
    [InlineData("  https://immich.example.com  ", true)]
    [InlineData("ftp://immich.example.com", false)]
    [InlineData("immich.example.com", false)]
    [InlineData("/api", false)]
    [InlineData("file:///C:/temp", false)]
    [InlineData("https://", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsValidServerUrl(string? url, bool expected)
    {
        Assert.Equal(expected, ConfigStore.IsValidServerUrl(url));
    }

    [Fact]
    public void Load_InvalidServerUrlInFile_IsReportedButKept()
    {
        File.WriteAllText(Path.Combine(_exeDir, "config.json"), "{\"ServerUrl\":\"immich.example.com\"}");
        var result = new ConfigStore(_exeDir, _appDataDir).Load();
        Assert.Null(result.Error);
        Assert.Equal("immich.example.com", result.Config.ServerUrl);
        Assert.False(result.HasValidServerUrl);
    }
}
