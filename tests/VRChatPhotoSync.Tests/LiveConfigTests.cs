namespace VRChatPhotoSync.Tests;

public class LiveConfigTests
{
    [Fact]
    public void Update_AppliesToBothAndSavesOnlyTheSavedConfig()
    {
        var saved = new AppConfig { ServerUrl = "https://immich.example.com/api" };
        var current = saved with { ServerUrl = "https://other.example.com/api" }; // --server override
        var written = new List<AppConfig>();
        var live = new LiveConfig(saved, current, written.Add);
        AppConfig? notified = null;
        live.Changed += c => notified = c;

        live.Update(c => c with { ExcludePrints = false });

        Assert.False(live.Saved.ExcludePrints);
        Assert.False(live.Current.ExcludePrints);
        Assert.Equal("https://immich.example.com/api", Assert.Single(written).ServerUrl);
        Assert.Equal("https://other.example.com/api", live.Current.ServerUrl);
        Assert.Same(live.Current, notified);
    }

    [Fact]
    public void Update_WithRealStore_WritesConfigFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PhotoSyncTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new ConfigStore(dir, Path.Combine(dir, "appdata"));
            var live = new LiveConfig(new AppConfig(), new AppConfig(), store.Save);

            live.Update(c => c with { UploadLayers = true, CloseButtonAction = CloseButtonAction.HideToTray });

            var loaded = store.Load().Config;
            Assert.True(loaded.UploadLayers);
            Assert.Equal(CloseButtonAction.HideToTray, loaded.CloseButtonAction);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Update_SaveFailure_StillAppliesAndRethrows()
    {
        var live = new LiveConfig(new AppConfig(), new AppConfig(), _ => throw new IOException("disk full"));
        var notified = false;
        live.Changed += _ => notified = true;

        var ex = Assert.Throws<IOException>(() => live.Update(c => c with { ExcludePrints = false }));

        Assert.Equal("disk full", ex.Message);
        Assert.False(live.Current.ExcludePrints);
        Assert.True(notified);
    }
}
