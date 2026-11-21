using System.Net;
using System.Text.Json;

namespace VRChatPhotoSync.Tests;

public class ImmichClientHelpersTests
{
    private const string Sha1A = "0123456789abcdef0123456789abcdef01234567";
    private const string Sha1B = "89abcdef0123456789abcdef0123456789abcdef";

    private sealed record Call(HttpMethod Method, string Uri, string? ContentType, string? Body);

    /// <summary>Records every request (with its body) and answers from a script.</summary>
    private sealed class ScriptHandler(Func<Call, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var call = new Call(request.Method, request.RequestUri!.AbsoluteUri, request.Content?.Headers.ContentType?.MediaType, body);
            Calls.Add(call);
            return respond(call, Calls.Count - 1);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };

    private static ScriptHandler Always(HttpStatusCode status, string body = "") => new((_, _) => Json(status, body));

    private static ImmichClient Client(HttpMessageHandler handler) => new("https://immich.example.com", "key", handler);

    private static JsonElement Parse(string? json) => JsonDocument.Parse(json!).RootElement;

    // ---- Bulk upload check ----

    [Fact]
    public async Task BulkCheck_PostsAssets_AndMapsResults()
    {
        var handler = Always(HttpStatusCode.OK, """
            {"results":[
              {"id":"2024-11/a.png","action":"accept"},
              {"id":"2024-11/b.png","action":"reject","reason":"duplicate","assetId":"asset-b","isTrashed":true}
            ]}
            """);
        using var client = Client(handler);

        var result = await client.BulkUploadCheckAsync([new("2024-11/a.png", Sha1A), new("2024-11/b.png", Sha1B.ToUpperInvariant())]);

        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://immich.example.com/api/assets/bulk-upload-check", call.Uri);
        Assert.Equal("application/json", call.ContentType);
        var assets = Parse(call.Body).GetProperty("assets").EnumerateArray().ToArray();
        Assert.Equal(2, assets.Length);
        Assert.Equal("2024-11/a.png", assets[0].GetProperty("id").GetString());
        Assert.Equal(Sha1A, assets[0].GetProperty("checksum").GetString());
        Assert.Equal(Sha1B, assets[1].GetProperty("checksum").GetString()); // lower-cased

        Assert.Equal(ApiStatus.Ok, result.Status);
        Assert.Equal(
            new[]
            {
                new BulkCheckResult("2024-11/a.png", true, null, null, false),
                new BulkCheckResult("2024-11/b.png", false, "duplicate", "asset-b", true),
            },
            result.Value);
        Assert.False(result.Value![0].IsDuplicate);
        Assert.True(result.Value[1].IsDuplicate);
    }

    [Fact]
    public async Task BulkCheck_SplitsIntoBatchesOf500()
    {
        var handler = new ScriptHandler((call, _) =>
        {
            var ids = Parse(call.Body).GetProperty("assets").EnumerateArray().Select(a => a.GetProperty("id").GetString());
            var results = string.Join(",", ids.Select(id => $"{{\"id\":\"{id}\",\"action\":\"accept\"}}"));
            return Json(HttpStatusCode.OK, $"{{\"results\":[{results}]}}");
        });
        using var client = Client(handler);
        var items = Enumerable.Range(0, 1201).Select(i => new BulkCheckItem($"f{i}.png", Sha1A)).ToList();

        var result = await client.BulkUploadCheckAsync(items);

        Assert.Equal(500, ImmichClient.BulkCheckBatchSize);
        Assert.Equal(new[] { 500, 500, 201 }, handler.Calls.Select(c => Parse(c.Body).GetProperty("assets").GetArrayLength()));
        Assert.True(result.Succeeded);
        Assert.Equal(items.Select(i => i.Id), result.Value!.Select(r => r.Id));
    }

    [Fact]
    public async Task BulkCheck_Empty_MakesNoRequest()
    {
        var handler = Always(HttpStatusCode.InternalServerError);
        using var client = Client(handler);

        var result = await client.BulkUploadCheckAsync([]);

        Assert.Empty(handler.Calls);
        Assert.True(result.Succeeded);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task BulkCheck_StopsAtFirstFailedBatch()
    {
        var handler = new ScriptHandler((_, i) => i == 0
            ? Json(HttpStatusCode.OK, "{\"results\":[]}")
            : Json(HttpStatusCode.ServiceUnavailable, "busy"));
        using var client = Client(handler);
        var items = Enumerable.Range(0, 1500).Select(i => new BulkCheckItem($"f{i}", Sha1A)).ToList();

        var result = await client.BulkUploadCheckAsync(items);

        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal(ApiStatus.Transient, result.Status);
        Assert.Equal(503, result.HttpStatus);
        Assert.Equal("busy", result.Message);
        Assert.Null(result.Value);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"results\":[{\"action\":\"accept\"}]}")]
    [InlineData("{\"results\":[null]}")]
    public async Task BulkCheck_BadBody_IsTransient(string body)
    {
        using var client = Client(Always(HttpStatusCode.OK, body));
        var result = await client.BulkUploadCheckAsync([new("a", Sha1A)]);
        Assert.Equal(ApiStatus.Transient, result.Status);
        Assert.Equal(200, result.HttpStatus);
    }

    [Theory]
    [InlineData("", Sha1A)]
    [InlineData("a", "abc")]
    [InlineData("a", "zz23456789abcdef0123456789abcdef01234567")]
    public async Task BulkCheck_InvalidItem_Throws(string id, string sha1)
    {
        using var client = Client(Always(HttpStatusCode.OK));
        await Assert.ThrowsAsync<ArgumentException>(() => client.BulkUploadCheckAsync([new(id, sha1)]));
    }

    // ---- Status mapping (shared by every helper) ----

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ApiStatus.AuthError)]
    [InlineData(HttpStatusCode.Forbidden, ApiStatus.AuthError)]
    [InlineData(HttpStatusCode.BadRequest, ApiStatus.Rejected)]
    [InlineData(HttpStatusCode.NotFound, ApiStatus.Rejected)]
    [InlineData(HttpStatusCode.RequestTimeout, ApiStatus.Transient)]
    [InlineData(HttpStatusCode.TooManyRequests, ApiStatus.Transient)]
    [InlineData(HttpStatusCode.InternalServerError, ApiStatus.Transient)]
    [InlineData(HttpStatusCode.BadGateway, ApiStatus.Transient)]
    public async Task ErrorStatuses_AreMapped(HttpStatusCode status, ApiStatus expected)
    {
        using var client = Client(Always(status, "{\"message\":\"nope\"}"));

        var check = await client.BulkUploadCheckAsync([new("a", Sha1A)]);
        var album = await client.GetOrCreateAlbumAsync("VRChat");
        var add = await client.AddToAlbumAsync("album-1", ["x"]);
        var copy = await client.CopyAssetAsync("old", "new");
        var trash = await client.TrashAssetsAsync(["old"]);
        var version = await client.GetServerVersionAsync();

        Assert.All(new[] { check.Status, album.Status, add.Status, copy.Status, trash.Status, version.Status },
            s => Assert.Equal(expected, s));
        Assert.Equal((int)status, copy.HttpStatus);
        Assert.Equal("{\"message\":\"nope\"}", trash.Message);
    }

    [Fact]
    public async Task NetworkError_IsTransient()
    {
        var handler = new ScriptHandler((_, _) => throw new HttpRequestException("connection refused"));
        using var client = Client(handler);

        var copy = await client.CopyAssetAsync("old", "new");
        var album = await client.GetOrCreateAlbumAsync("VRChat");

        Assert.Equal(ApiStatus.Transient, copy.Status);
        Assert.Null(copy.HttpStatus);
        Assert.Equal("connection refused", copy.Message);
        Assert.Equal(ApiStatus.Transient, album.Status);
    }

    [Fact]
    public async Task CallerCancellation_Throws()
    {
        using var client = Client(Always(HttpStatusCode.OK, "{\"results\":[]}"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.BulkUploadCheckAsync([new("a", Sha1A)], cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.TrashAssetsAsync(["a"], cts.Token));
    }

    // ---- Albums ----

    [Fact]
    public async Task GetOrCreateAlbum_FindsExisting_ByExactName()
    {
        var handler = Always(HttpStatusCode.OK, """
            [{"id":"other","albumName":"vrchat"},{"id":"album-1","albumName":"VRChat & friends"}]
            """);
        using var client = Client(handler);

        var result = await client.GetOrCreateAlbumAsync("VRChat & friends");

        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal("https://immich.example.com/api/albums?name=VRChat%20%26%20friends", call.Uri);
        Assert.Equal(ApiResult<string>.Ok("album-1"), result with { HttpStatus = null });
    }

    [Fact]
    public async Task GetOrCreateAlbum_CreatesWhenMissing()
    {
        var handler = new ScriptHandler((call, _) => call.Method == HttpMethod.Get
            ? Json(HttpStatusCode.OK, "[{\"id\":\"x\",\"albumName\":\"VRChat old\"}]") // not an exact match
            : Json(HttpStatusCode.Created, "{\"id\":\"new-album\",\"albumName\":\"VRChat\"}"));
        using var client = Client(handler);

        var result = await client.GetOrCreateAlbumAsync("VRChat");

        Assert.Equal(2, handler.Calls.Count);
        var post = handler.Calls[1];
        Assert.Equal(HttpMethod.Post, post.Method);
        Assert.Equal("https://immich.example.com/api/albums", post.Uri);
        Assert.Equal("application/json", post.ContentType);
        Assert.Equal("VRChat", Parse(post.Body).GetProperty("albumName").GetString());
        Assert.True(result.Succeeded);
        Assert.Equal("new-album", result.Value);
    }

    [Fact]
    public async Task GetOrCreateAlbum_CachesId_UntilNameChanges()
    {
        var handler = new ScriptHandler((call, _) => Json(HttpStatusCode.OK,
            call.Uri.EndsWith("name=A") ? "[{\"id\":\"id-a\",\"albumName\":\"A\"}]" : "[{\"id\":\"id-b\",\"albumName\":\"B\"}]"));
        using var client = Client(handler);

        Assert.Equal("id-a", (await client.GetOrCreateAlbumAsync("A")).Value);
        Assert.Equal("id-a", (await client.GetOrCreateAlbumAsync("A")).Value);
        Assert.Single(handler.Calls);

        Assert.Equal("id-b", (await client.GetOrCreateAlbumAsync("B")).Value);
        Assert.Equal("id-a", (await client.GetOrCreateAlbumAsync("A")).Value);
        Assert.Equal(3, handler.Calls.Count);

        client.ForgetAlbum();
        Assert.Equal("id-a", (await client.GetOrCreateAlbumAsync("A")).Value);
        Assert.Equal(4, handler.Calls.Count);
    }

    [Fact]
    public async Task GetOrCreateAlbum_ConcurrentCalls_CreateOnce()
    {
        var handler = new ScriptHandler((call, _) => call.Method == HttpMethod.Get
            ? Json(HttpStatusCode.OK, "[]")
            : Json(HttpStatusCode.Created, "{\"id\":\"new-album\"}"));
        using var client = Client(handler);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => client.GetOrCreateAlbumAsync("VRChat"))));

        Assert.All(results, r => Assert.Equal("new-album", r.Value));
        Assert.Single(handler.Calls, c => c.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task GetOrCreateAlbum_FailureIsNotCached()
    {
        var handler = new ScriptHandler((_, i) => i == 0
            ? Json(HttpStatusCode.BadGateway, "")
            : Json(HttpStatusCode.OK, "[{\"id\":\"album-1\",\"albumName\":\"VRChat\"}]"));
        using var client = Client(handler);

        Assert.Equal(ApiStatus.Transient, (await client.GetOrCreateAlbumAsync("VRChat")).Status);
        Assert.Equal("album-1", (await client.GetOrCreateAlbumAsync("VRChat")).Value);
    }

    [Fact]
    public async Task GetOrCreateAlbum_CreateFailure_IsReturned()
    {
        var handler = new ScriptHandler((call, _) => call.Method == HttpMethod.Get
            ? Json(HttpStatusCode.OK, "[]")
            : Json(HttpStatusCode.Forbidden, "{\"message\":\"Missing permission: album.create\"}"));
        using var client = Client(handler);

        var result = await client.GetOrCreateAlbumAsync("VRChat");

        Assert.Equal(ApiStatus.AuthError, result.Status);
        Assert.Equal(403, result.HttpStatus);
        Assert.Contains("album.create", result.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task GetOrCreateAlbum_BlankName_Throws(string name)
    {
        using var client = Client(Always(HttpStatusCode.OK, "[]"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetOrCreateAlbumAsync(name));
    }

    [Fact]
    public async Task AddToAlbum_PutsIds_AndTreatsDuplicateAsSuccess()
    {
        var handler = Always(HttpStatusCode.OK, """
            [{"id":"a","success":true},{"id":"b","success":false,"error":"duplicate"},{"id":"c","success":false,"error":"no_permission"}]
            """);
        using var client = Client(handler);

        var result = await client.AddToAlbumAsync("album-1", ["a", "b", "c"]);

        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Put, call.Method);
        Assert.Equal("https://immich.example.com/api/albums/album-1/assets", call.Uri);
        Assert.Equal(new[] { "a", "b", "c" }, Parse(call.Body).GetProperty("ids").EnumerateArray().Select(e => e.GetString()));

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { "a" }, result.Value!.Added);
        Assert.Equal(new[] { "b" }, result.Value.AlreadyInAlbum);
        Assert.Equal(new[] { new AlbumAddFailure("c", "no_permission") }, result.Value.Failed);
        Assert.False(result.Value.AllSucceeded);
    }

    [Fact]
    public async Task AddToAlbum_OnlyDuplicates_AllSucceeded()
    {
        using var client = Client(Always(HttpStatusCode.OK, "[{\"id\":\"a\",\"success\":false,\"error\":\"duplicate\"}]"));
        var result = await client.AddToAlbumAsync("album-1", ["a"]);
        Assert.True(result.Value!.AllSucceeded);
    }

    [Fact]
    public async Task AddToAlbum_Empty_MakesNoRequest()
    {
        var handler = Always(HttpStatusCode.OK, "[]");
        using var client = Client(handler);
        var result = await client.AddToAlbumAsync("album-1", []);
        Assert.Empty(handler.Calls);
        Assert.True(result.Value!.AllSucceeded);
    }

    [Fact]
    public async Task AddToAlbum_BadBody_IsTransient()
    {
        using var client = Client(Always(HttpStatusCode.OK, "{\"id\":\"a\"}"));
        Assert.Equal(ApiStatus.Transient, (await client.AddToAlbumAsync("album-1", ["a"])).Status);
    }

    // ---- Replace helpers ----

    [Fact]
    public async Task CopyAsset_PutsSourceAndTarget()
    {
        var handler = Always(HttpStatusCode.NoContent);
        using var client = Client(handler);

        var result = await client.CopyAssetAsync("old-id", "new-id");

        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Put, call.Method);
        Assert.Equal("https://immich.example.com/api/assets/copy", call.Uri);
        var body = Parse(call.Body);
        Assert.Equal("old-id", body.GetProperty("sourceId").GetString());
        Assert.Equal("new-id", body.GetProperty("targetId").GetString());
        Assert.Equal(new ApiResult(ApiStatus.Ok, 204, null), result);
    }

    [Fact]
    public async Task TrashAssets_DeletesWithoutForce()
    {
        var handler = Always(HttpStatusCode.NoContent);
        using var client = Client(handler);

        var result = await client.TrashAssetsAsync(["old-1", "old-2"]);

        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Delete, call.Method);
        Assert.Equal("https://immich.example.com/api/assets", call.Uri);
        Assert.Equal("application/json", call.ContentType);
        var body = Parse(call.Body);
        Assert.Equal(new[] { "old-1", "old-2" }, body.GetProperty("ids").EnumerateArray().Select(e => e.GetString()));
        Assert.False(body.TryGetProperty("force", out _));
        Assert.DoesNotContain("force", call.Body, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task TrashAssets_Empty_MakesNoRequest()
    {
        var handler = Always(HttpStatusCode.NoContent);
        using var client = Client(handler);
        Assert.True((await client.TrashAssetsAsync([])).Succeeded);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task ReplaceHelpers_RejectBlankIds()
    {
        using var client = Client(Always(HttpStatusCode.NoContent));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CopyAssetAsync("", "new"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CopyAssetAsync("old", " "));
        await Assert.ThrowsAsync<ArgumentException>(() => client.TrashAssetsAsync(["a", ""]));
        await Assert.ThrowsAsync<ArgumentException>(() => client.AddToAlbumAsync(" ", ["a"]));
    }

    // ---- Server version ----

    [Fact]
    public async Task ServerVersion_GetsAndParses()
    {
        var handler = Always(HttpStatusCode.OK, "{\"major\":3,\"minor\":4,\"patch\":2}");
        using var client = Client(handler);

        var result = await client.GetServerVersionAsync();

        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal("https://immich.example.com/api/server/version", call.Uri);
        Assert.True(result.Succeeded);
        Assert.Equal(new Version(3, 4, 2), result.Value);
        Assert.Equal("3.4.2", result.Value!.ToString());
    }

    [Theory]
    [InlineData("{\"major\":3,\"minor\":4}")]
    [InlineData("{\"major\":-1,\"minor\":4,\"patch\":2}")]
    [InlineData("<html>")]
    public async Task ServerVersion_BadBody_IsTransient(string body)
    {
        using var client = Client(Always(HttpStatusCode.OK, body));
        Assert.Equal(ApiStatus.Transient, (await client.GetServerVersionAsync()).Status);
    }

    [Fact]
    public async Task ServerVersion_TimesOut()
    {
        var handler = new DelayHandler();
        using var client = Client(handler);

        var result = await client.GetServerVersionAsync(TimeSpan.FromMilliseconds(50));

        Assert.Equal(ApiStatus.Transient, result.Status);
        Assert.Null(result.HttpStatus);
    }

    private sealed class DelayHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}
