namespace VRChatPhotoSync.Tests;

public class ServerSettingsFormTests
{
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA test timed out");
        if (error is not null)
            throw new Xunit.Sdk.XunitException($"STA test failed: {error}");
    }

    private static readonly VRChatSettings Folder =
        new(@"D:\Photos\VRChat", true, VRChatSettings.SourceVRChatConfig, null, []);

    private static AppConfig SetUp => new()
    {
        ServerUrl = "https://immich.example.com/api",
        ApiKeyProtected = SecretProtector.Protect("saved-key"),
    };

    private static IEnumerable<Control> All(Control root) =>
        root.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(All(c)));

    [Fact]
    public void FirstSetup_OffersExistingPhotos_KeyBoxIsEmptyPasswordBox()
    {
        RunSta(() =>
        {
            using var form = new ServerSettingsForm(new ServerSettingsModel(new AppConfig()), Folder, _ => { }, _ => Task.FromResult(3));
            Assert.True(form.OffersUploadExisting);
            var check = All(form).OfType<CheckBox>().Single(c => c.Text.StartsWith("Also upload my"));
            Assert.False(check.Checked);
            var key = All(form).OfType<TextBox>().Single(t => t.UseSystemPasswordChar);
            Assert.Equal("", key.Text);
            Assert.Contains(All(form).OfType<Label>(), l => l.Text == @"Photo folder: D:\Photos\VRChat (from VRChat)");
            Assert.Contains(All(form).OfType<LinkLabel>(), l => l.Text == "How do I get an API key?");
            Assert.Contains(All(form).OfType<LinkLabel>(), l => l.Text == "Open VRChat config folder");
        });
    }

    [Fact]
    public void SavedKey_IsNotShown_AndExistingPhotosNotOffered()
    {
        RunSta(() =>
        {
            using var form = new ServerSettingsForm(new ServerSettingsModel(SetUp), Folder, _ => { }, _ => Task.FromResult(3));
            Assert.False(form.OffersUploadExisting);
            var key = All(form).OfType<TextBox>().Single(t => t.UseSystemPasswordChar);
            Assert.Equal("", key.Text);
            Assert.Contains("leave blank", key.PlaceholderText);
            Assert.DoesNotContain(All(form).OfType<TextBox>(), t => t.Text.Contains("saved-key"));
            Assert.Contains(All(form).OfType<TextBox>(), t => t.Text == "https://immich.example.com/api");
        });
    }

    [Fact]
    public void ShowToggle_RevealsTypedKey()
    {
        RunSta(() =>
        {
            using var form = new ServerSettingsForm(new ServerSettingsModel(SetUp), Folder, _ => { });
            var key = All(form).OfType<TextBox>().Single(t => t.UseSystemPasswordChar);
            var show = All(form).OfType<CheckBox>().Single(c => c.Text == "Show");
            show.Checked = true;
            Assert.False(key.UseSystemPasswordChar);
            show.Checked = false;
            Assert.True(key.UseSystemPasswordChar);
        });
    }

    [Fact]
    public void MainForm_ApplyServerSettings_SavesRestartsClientAndResumes()
    {
        RunSta(() =>
        {
            var written = new List<AppConfig>();
            var live = new LiveConfig(new AppConfig(), new AppConfig(), written.Add);
            var vm = new MainViewModel(new AppConfig(), @"D:\Photos\VRChat");
            using var form = new MainForm(live, vm, @"D:\Photos\VRChat", null);
            var created = new List<(string Url, string Key)>();
            using var host = new ImmichClientHost((url, key) => { created.Add((url, key)); return new NullClient(); });
            form.Attach(host);

            var model = new ServerSettingsModel(new AppConfig()) { ServerUrl = "https://immich.example.com", ApiKey = " new-key " };
            form.ApplyServerSettings(model);

            var saved = Assert.Single(written);
            Assert.Equal("https://immich.example.com/api", saved.ServerUrl);
            Assert.Equal("new-key", SecretProtector.Unprotect(saved.ApiKeyProtected));
            Assert.Equal(saved.ApiKeyProtected, live.Current.ApiKeyProtected);
            Assert.Equal([("https://immich.example.com/api", "new-key")], created);
            Assert.NotNull(host.Current);
            Assert.True(MainViewModel.IsSetUp(live.Current));
        });
    }

    private sealed class NullClient : IUploadClient
    {
        public Task<UploadResult> UploadAsync(ReadOnlyMemory<byte> content, string fileName, string sha1, DateTimeOffset createdAt,
            DateTimeOffset modifiedAt, AssetVisibility visibility = AssetVisibility.Timeline, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ApiResult<string>> GetOrCreateAlbumAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ApiResult<AlbumAddResult>> AddToAlbumAsync(string albumId, IReadOnlyCollection<string> assetIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ApiKeyCheckResult> GetMyApiKeyAsync(string? albumName = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ApiResult> CopyAssetAsync(string sourceId, string targetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ApiResult> TrashAssetsAsync(IReadOnlyCollection<string> assetIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ApiResult<IReadOnlyList<BulkCheckResult>>> BulkUploadCheckAsync(IReadOnlyList<BulkCheckItem> items, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

public class ImmichClientHostTests
{
    private sealed class Disposable : IUploadClient, IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
        public Task<UploadResult> UploadAsync(ReadOnlyMemory<byte> content, string fileName, string sha1, DateTimeOffset createdAt,
            DateTimeOffset modifiedAt, AssetVisibility visibility = AssetVisibility.Timeline, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ApiResult<string>> GetOrCreateAlbumAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ApiResult<AlbumAddResult>> AddToAlbumAsync(string albumId, IReadOnlyCollection<string> assetIds, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ApiKeyCheckResult> GetMyApiKeyAsync(string? albumName = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<ApiResult> CopyAssetAsync(string sourceId, string targetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ApiResult> TrashAssetsAsync(IReadOnlyCollection<string> assetIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ApiResult<IReadOnlyList<BulkCheckResult>>> BulkUploadCheckAsync(IReadOnlyList<BulkCheckItem> items, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static AppConfig Config(string key) => new()
    {
        ServerUrl = "https://immich.example.com/api",
        ApiKeyProtected = "p:" + key,
    };

    private static string? Unprotect(string? v) => v is not null && v.StartsWith("p:") ? v[2..] : null;

    [Fact]
    public void Restart_ReplacesTheClient_AndRetiresTheOldOne()
    {
        var retired = new List<IDisposable>();
        var made = new List<Disposable>();
        using var host = new ImmichClientHost((_, _) => { var d = new Disposable(); made.Add(d); return d; }, Unprotect, retired.Add);

        Assert.Null(host.Current);
        Assert.True(host.Restart(Config("one")));
        var first = host.Current;
        Assert.True(host.Restart(Config("two")));

        Assert.NotSame(first, host.Current);
        Assert.Same(first, Assert.Single(retired));
        Assert.False(made[0].Disposed); // retired, not disposed straight away
    }

    [Fact]
    public void Restart_WithoutUsableKeyOrUrl_LeavesNoClient()
    {
        using var host = new ImmichClientHost((_, _) => new Disposable(), Unprotect, _ => { });
        Assert.True(host.Restart(Config("k")));
        Assert.False(host.Restart(Config("k") with { ApiKeyProtected = "garbage" }));
        Assert.Null(host.Current);
        Assert.False(host.Restart(Config("k") with { ServerUrl = null }));
        Assert.False(host.Restart(new AppConfig()));
    }

    [Fact]
    public void Dispose_DisposesTheCurrentClient()
    {
        var client = new Disposable();
        var host = new ImmichClientHost((_, _) => client, Unprotect, _ => { });
        host.Restart(Config("k"));
        host.Dispose();
        Assert.True(client.Disposed);
        Assert.Throws<ObjectDisposedException>(() => host.Restart(Config("k")));
    }

    [Fact]
    public void DefaultFactory_MakesARealImmichClient()
    {
        using var host = new ImmichClientHost(unprotect: Unprotect, retire: _ => { });
        Assert.True(host.Restart(Config("k")));
        var client = Assert.IsType<ImmichClient>(host.Current);
        Assert.Equal("https://immich.example.com/api/", client.BaseAddress.ToString());
    }
}
