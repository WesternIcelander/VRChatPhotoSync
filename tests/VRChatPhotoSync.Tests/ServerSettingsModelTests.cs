namespace VRChatPhotoSync.Tests;

public class ServerSettingsModelTests
{
    private const string Url = "https://immich.example.com";

    /// <summary>A fake server: answers the two test calls with fixed results and records how it was created.</summary>
    private sealed class FakeServer : IServerInfoClient
    {
        public ApiResult<Version> Version { get; set; } = ApiResult<Version>.Ok(new Version(3, 4, 2), 200);
        public ApiKeyCheckResult Key { get; set; } = KeyResult("PhotoSync", ApiKeyInfo.PermissionAll);
        public string? Url { get; set; }
        public string? ApiKey { get; set; }
        public string? AlbumAsked { get; private set; }
        public int VersionCalls { get; private set; }
        public int KeyCalls { get; private set; }
        public bool Disposed { get; private set; }

        public Task<ApiResult<Version>> GetServerVersionAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            VersionCalls++;
            return Task.FromResult(Version);
        }

        public Task<ApiKeyCheckResult> GetMyApiKeyAsync(string? albumName = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            KeyCalls++;
            AlbumAsked = albumName;
            return Task.FromResult(Key);
        }

        public void Dispose() => Disposed = true;
    }

    private static ApiKeyCheckResult KeyResult(string? name, params string[] permissions) =>
        new(ApiKeyCheckStatus.Ok, 200, new ApiKeyInfo("key-id", name, permissions), []);

    private static string FakeProtect(string key) => "protected:" + key;
    private static string? FakeUnprotect(string? value) =>
        value is not null && value.StartsWith("protected:", StringComparison.Ordinal) ? value["protected:".Length..] : null;

    private int _connects;

    private ServerSettingsModel Create(FakeServer server, AppConfig? config = null) =>
        new(config ?? new AppConfig(), (url, key) =>
        {
            _connects++;
            server.Url = url;
            server.ApiKey = key;
            return server;
        }, FakeUnprotect, FakeProtect);

    private static AppConfig Saved => new()
    {
        ServerUrl = Url + "/api",
        ApiKeyProtected = FakeProtect("saved-key"),
        AlbumName = "VRChat",
        Visibility = AssetVisibility.Archive,
        VrcxWaitSeconds = 7,
        StrictFileNames = false,
    };

    // ---- Fields and first setup ----

    [Fact]
    public void SavedKey_IsNeverPrefilled_AndBackfillIsNotOffered()
    {
        var model = Create(new FakeServer(), Saved);
        Assert.Equal("", model.ApiKey);
        Assert.True(model.HasSavedKey);
        Assert.False(model.IsFirstSetup);
        Assert.Contains("leave blank", model.ApiKeyPlaceholder);
        Assert.Equal(Url + "/api", model.ServerUrl);
        Assert.Equal("VRChat", model.AlbumName);
        Assert.Equal(AssetVisibility.Archive, model.Visibility);
        Assert.Equal(7, model.VrcxWaitSeconds);
        Assert.False(model.StrictFileNames);
    }

    [Fact]
    public void NoSavedKey_IsFirstSetup_WithBackfillUnticked()
    {
        var model = Create(new FakeServer());
        Assert.True(model.IsFirstSetup);
        Assert.False(model.UploadExisting);
    }

    [Theory]
    [InlineData(null, "Also upload my existing photos (counting…)")]
    [InlineData(1, "Also upload my 1 existing photo")]
    [InlineData(0, "Also upload my 0 existing photos")]
    [InlineData(12, "Also upload my 12 existing photos")]
    public void UploadExistingText(int? count, string expected) =>
        Assert.Equal(expected, ServerSettingsModel.UploadExistingText(count));

    // ---- Help link ----

    [Theory]
    [InlineData("https://immich.example.com", "https://immich.example.com/user-settings?isOpen=api-keys")]
    [InlineData("https://immich.example.com/api", "https://immich.example.com/user-settings?isOpen=api-keys")]
    [InlineData("https://immich.example.com/api/", "https://immich.example.com/user-settings?isOpen=api-keys")]
    [InlineData("http://192.168.1.10:2283", "http://192.168.1.10:2283/user-settings?isOpen=api-keys")]
    [InlineData("", ServerSettingsModel.ApiKeyDocsUrl)]
    [InlineData(null, ServerSettingsModel.ApiKeyDocsUrl)]
    [InlineData("immich.example.com", ServerSettingsModel.ApiKeyDocsUrl)]
    public void ApiKeyHelpUrl_IsTheServersKeyPage_OrTheDocs(string? url, string expected) =>
        Assert.Equal(expected, ServerSettingsModel.GetApiKeyHelpUrl(url));

    [Fact]
    public void RequiredPermissions_AreListed()
    {
        var text = ServerSettingsModel.RequiredPermissionsText;
        foreach (var p in new[] { "asset.upload", "album.read", "album.create", "albumAsset.create", "asset.copy", "asset.delete" })
            Assert.Contains(p, text);
        Assert.Contains("Account Settings", ServerSettingsModel.ApiKeyManualRoute);
    }

    // ---- Photo folder ----

    [Fact]
    public void PhotoFolder_ShowsItsSource()
    {
        var fromVrchat = new VRChatSettings(@"D:\Photos\VRChat", true, VRChatSettings.SourceVRChatConfig, @"C:\Users\Example\AppData\LocalLow\VRChat\VRChat\config.json", []);
        Assert.Equal(@"D:\Photos\VRChat (from VRChat)", ServerSettingsModel.PhotoFolderText(fromVrchat));
        Assert.Equal(@"C:\Users\Example\AppData\LocalLow\VRChat\VRChat", ServerSettingsModel.VRChatConfigFolder(fromVrchat));

        var fromCommandLine = fromVrchat with { Source = VRChatSettings.SourceCommandLine };
        Assert.Contains("--folder", ServerSettingsModel.PhotoFolderText(fromCommandLine));
        var byDefault = fromVrchat with { Source = VRChatSettings.SourceDefault };
        Assert.Contains("default", ServerSettingsModel.PhotoFolderText(byDefault));
    }

    // ---- Validation ----

    [Theory]
    [InlineData("", "server URL")]
    [InlineData("   ", "server URL")]
    [InlineData("immich.example.com", "http://")]
    [InlineData("ftp://immich.example.com", "http://")]
    public void Validate_RejectsBadUrls(string url, string expected)
    {
        var model = Create(new FakeServer());
        model.ServerUrl = url;
        model.ApiKey = "abc";
        Assert.Contains(model.Validate(), e => e.Contains(expected));
    }

    [Theory]
    [InlineData("https://immich.example.com")]
    [InlineData("https://immich.example.com/api")]
    [InlineData("  https://immich.example.com/api/  ")]
    public void Validate_AcceptsUrlsWithOrWithoutApi(string url)
    {
        var model = Create(new FakeServer());
        model.ServerUrl = url;
        model.ApiKey = "abc";
        Assert.Empty(model.Validate());
    }

    [Fact]
    public void Validate_NeedsAKeyOnFirstSetup()
    {
        var model = Create(new FakeServer());
        model.ServerUrl = Url;
        Assert.Contains(model.Validate(), e => e.Contains("Enter an API key"));
        model.ApiKey = "   ";
        Assert.Contains(model.Validate(), e => e.Contains("Enter an API key"));
    }

    [Fact]
    public void Validate_BlankKeyKeepsTheSavedOne()
    {
        var model = Create(new FakeServer(), Saved);
        Assert.Empty(model.Validate());
    }

    [Fact]
    public void Validate_SavedKeyThatCantBeDecrypted_MustBeEnteredAgain()
    {
        var model = Create(new FakeServer(), Saved with { ApiKeyProtected = "from-another-pc" });
        Assert.Contains(model.Validate(), e => e.Contains("Enter it again"));
        model.ApiKey = "new-key";
        Assert.Empty(model.Validate());
    }

    [Fact]
    public void Validate_KeyWithInnerSpaces_IsRejected_SurroundingWhitespaceIsFine()
    {
        var model = Create(new FakeServer());
        model.ServerUrl = Url;
        model.ApiKey = "abc def";
        Assert.Contains(model.Validate(), e => e.Contains("spaces"));
        model.ApiKey = "  abcdef\r\n";
        Assert.Empty(model.Validate());
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(10, true)]
    [InlineData(ServerSettingsModel.MaxVrcxWaitSeconds, true)]
    [InlineData(ServerSettingsModel.MaxVrcxWaitSeconds + 1, false)]
    public void Validate_VrcxWaitRange(int seconds, bool ok)
    {
        var model = Create(new FakeServer(), Saved);
        model.VrcxWaitSeconds = seconds;
        Assert.Equal(ok, model.Validate().Count == 0);
    }

    // ---- Result ----

    [Fact]
    public void ApplyTo_BlankKeyKeepsSavedKey_AndNormalisesTheUrl()
    {
        var model = Create(new FakeServer(), Saved);
        model.ServerUrl = " https://immich.example.com/ ";
        model.AlbumName = "   ";
        model.Visibility = AssetVisibility.Hidden;
        model.VrcxWaitSeconds = 0;
        model.StrictFileNames = true;

        var config = model.ApplyTo(Saved with { ExcludePrints = false });

        Assert.Equal("https://immich.example.com/api", config.ServerUrl);
        Assert.Equal(Saved.ApiKeyProtected, config.ApiKeyProtected);
        Assert.Null(config.AlbumName);
        Assert.Equal(AssetVisibility.Hidden, config.Visibility);
        Assert.Equal(0, config.VrcxWaitSeconds);
        Assert.True(config.StrictFileNames);
        Assert.False(config.ExcludePrints); // untouched settings are kept
        Assert.False(model.KeyChanged);
    }

    [Fact]
    public void ApplyTo_NewKeyIsTrimmedAndProtectedOnce()
    {
        var protects = 0;
        var model = new ServerSettingsModel(Saved, (_, _) => new FakeServer(), FakeUnprotect, k => { protects++; return FakeProtect(k); })
        {
            ApiKey = "  new-key \n",
            AlbumName = " Trips ",
        };

        var a = model.ApplyTo(Saved);
        var b = model.ApplyTo(Saved with { StartMinimized = true });

        Assert.Equal(FakeProtect("new-key"), a.ApiKeyProtected);
        Assert.Equal(a.ApiKeyProtected, b.ApiKeyProtected);
        Assert.Equal(1, protects);
        Assert.Equal("Trips", a.AlbumName);
        Assert.True(model.KeyChanged);
    }

    [Fact]
    public void ApplyTo_RealDpapiKey_RoundTrips()
    {
        var model = new ServerSettingsModel(new AppConfig()) { ServerUrl = Url, ApiKey = " real-key " };
        var config = model.ApplyTo(new AppConfig());
        Assert.NotEqual("real-key", config.ApiKeyProtected);
        Assert.Equal("real-key", SecretProtector.Unprotect(config.ApiKeyProtected));
    }

    // ---- Connection test ----

    [Fact]
    public async Task Test_InvalidInput_DoesNotConnect()
    {
        var server = new FakeServer();
        var model = Create(server);
        model.ServerUrl = "not a url";
        model.ApiKey = "abc";

        var result = await model.TestAsync();

        Assert.Equal(ConnectionTestOutcome.InvalidInput, result.Outcome);
        Assert.False(result.Ok);
        Assert.Equal(0, _connects);
    }

    [Fact]
    public async Task Test_UsesNormalisedUrlAndTrimmedTypedKey_AndDisposesTheClient()
    {
        var server = new FakeServer();
        var model = Create(server, Saved);
        model.ServerUrl = "https://immich.example.com/";
        model.ApiKey = "  typed-key  ";
        model.AlbumName = "";

        var result = await model.TestAsync();

        Assert.True(result.Ok);
        Assert.Equal("https://immich.example.com/api", server.Url);
        Assert.Equal("typed-key", server.ApiKey);
        Assert.Null(server.AlbumAsked);
        Assert.True(server.Disposed);
    }

    [Fact]
    public async Task Test_BlankKey_UsesTheSavedKey()
    {
        var server = new FakeServer();
        var model = Create(server, Saved);
        await model.TestAsync();
        Assert.Equal("saved-key", server.ApiKey);
        Assert.Equal("VRChat", server.AlbumAsked);
    }

    [Fact]
    public async Task Test_CantReach()
    {
        var server = new FakeServer { Version = ApiResult<Version>.Fail(ApiStatus.Transient, null, "No such host is known.") };
        var result = await Create(server, Saved).TestAsync();
        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
        Assert.Contains("Can't reach", result.Summary);
        Assert.Equal(0, server.KeyCalls);
    }

    [Theory]
    [InlineData(ApiStatus.Rejected, 404)]
    [InlineData(ApiStatus.Transient, 200)] // a web page instead of JSON
    [InlineData(ApiStatus.AuthError, 401)] // a login page in front of it
    public async Task Test_NotImmich(ApiStatus status, int http)
    {
        var server = new FakeServer { Version = ApiResult<Version>.Fail(status, http, "<html>") };
        var result = await Create(server, Saved).TestAsync();
        Assert.Equal(ConnectionTestOutcome.NotImmich, result.Outcome);
        Assert.Contains("doesn't look like Immich", result.Summary);
        Assert.Contains(http.ToString(), result.Summary);
    }

    [Fact]
    public async Task Test_ServerError()
    {
        var server = new FakeServer { Version = ApiResult<Version>.Fail(ApiStatus.Transient, 502, "Bad gateway") };
        var result = await Create(server, Saved).TestAsync();
        Assert.Equal(ConnectionTestOutcome.ServerError, result.Outcome);
        Assert.Contains("502", result.Summary);
    }

    [Fact]
    public async Task Test_KeyRejected()
    {
        var server = new FakeServer { Key = ApiKeyCheckResult.Failed(ApiKeyCheckStatus.KeyRejected, 401) };
        var result = await Create(server, Saved).TestAsync();
        Assert.Equal(ConnectionTestOutcome.KeyRejected, result.Outcome);
        Assert.Contains("rejected", result.Summary);
        Assert.Contains("Immich 3.4.2", result.Summary);
    }

    [Fact]
    public async Task Test_KeyCheckUnreachableOrUnexpected()
    {
        var server = new FakeServer { Key = ApiKeyCheckResult.Failed(ApiKeyCheckStatus.Unreachable, null) };
        Assert.Equal(ConnectionTestOutcome.Unreachable, (await Create(server, Saved).TestAsync()).Outcome);
        server.Key = ApiKeyCheckResult.Failed(ApiKeyCheckStatus.UnexpectedResponse, 500);
        Assert.Equal(ConnectionTestOutcome.ServerError, (await Create(server, Saved).TestAsync()).Outcome);
    }

    [Fact]
    public async Task Test_MissingUploadPermission()
    {
        var server = new FakeServer { Key = KeyResult("Read only", "asset.read", "album.read") };
        var result = await Create(server, Saved).TestAsync();
        Assert.Equal(ConnectionTestOutcome.MissingUploadPermission, result.Outcome);
        Assert.Contains("asset.upload", result.Summary);
        Assert.Contains("\"Read only\"", result.Summary);
    }

    [Fact]
    public async Task Test_Success_ShowsVersionAndKeyName_NoNotesForAll()
    {
        var server = new FakeServer();
        var result = await Create(server, Saved).TestAsync();
        Assert.Equal(ConnectionTestOutcome.Success, result.Outcome);
        Assert.Equal("✔ Connected — Immich 3.4.2, key \"PhotoSync\" can upload", result.Summary);
        Assert.Equal(new Version(3, 4, 2), result.ServerVersion);
        Assert.Equal("PhotoSync", result.KeyName);
        Assert.Single(result.Notes); // album OK
        Assert.Contains("can use it", result.Notes[0]);
    }

    [Fact]
    public async Task Test_Success_AlbumAndCopyDeleteNotes()
    {
        var server = new FakeServer { Key = KeyResult("Uploads", "asset.upload", "album.read", "asset.delete") };
        var result = await Create(server, Saved).TestAsync();

        Assert.True(result.Ok);
        Assert.Equal(2, result.Notes.Count);
        Assert.Contains("album.create", result.Notes[0]);
        Assert.Contains("albumAsset.create", result.Notes[0]);
        Assert.DoesNotContain("album.read", result.Notes[0]);
        Assert.Contains("Optional: asset.copy to replace photos VRCX edits late", result.Notes[1]);
        Assert.Contains(result.Notes[1], result.Text);
    }

    [Fact]
    public async Task Test_Success_NoAlbum_OnlyCopyDeleteNote()
    {
        var server = new FakeServer { Key = KeyResult(null, "asset.upload") };
        var model = Create(server, Saved);
        model.AlbumName = " ";
        var result = await model.TestAsync();

        Assert.True(result.Ok);
        Assert.Equal("✔ Connected — Immich 3.4.2, key can upload", result.Summary);
        var note = Assert.Single(result.Notes);
        Assert.Contains("asset.copy, asset.delete", note);
    }

    [Fact]
    public async Task Test_ClientThatThrows_IsReportedNotThrown()
    {
        var model = new ServerSettingsModel(Saved, (_, _) => new ThrowingServer(), FakeUnprotect, FakeProtect);
        var result = await model.TestAsync();
        Assert.Equal(ConnectionTestOutcome.Unreachable, result.Outcome);
    }

    [Fact]
    public async Task Test_Cancellation_IsPassedOn()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var model = new ServerSettingsModel(Saved, (_, _) => new CancellingServer(), FakeUnprotect, FakeProtect);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => model.TestAsync(cts.Token));
    }

    private sealed class ThrowingServer : IServerInfoClient
    {
        public Task<ApiResult<Version>> GetServerVersionAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
        public Task<ApiKeyCheckResult> GetMyApiKeyAsync(string? albumName = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");
        public void Dispose() { }
    }

    private sealed class CancellingServer : IServerInfoClient
    {
        public Task<ApiResult<Version>> GetServerVersionAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            Task.FromCanceled<ApiResult<Version>>(cancellationToken);
        public Task<ApiKeyCheckResult> GetMyApiKeyAsync(string? albumName = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
            Task.FromCanceled<ApiKeyCheckResult>(cancellationToken);
        public void Dispose() { }
    }
}
