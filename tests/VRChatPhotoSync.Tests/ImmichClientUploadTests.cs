using System.Net;
using System.Text;

namespace VRChatPhotoSync.Tests;

public class ImmichClientUploadTests
{
    private const string Sha1 = "0123456789abcdef0123456789abcdef01234567";
    private const string FileName = "VRChat_2024-11-23_19-17-27.652_1920x1080.png";
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];
    private static readonly DateTimeOffset Created = new(2024, 11, 23, 19, 17, 27, 652, TimeSpan.FromHours(1));
    private static readonly DateTimeOffset Modified = new(2024, 11, 23, 18, 17, 28, 5, TimeSpan.Zero);

    private sealed record Part(string? Name, string? FileName, string? FileNameStar, string? ContentType, byte[] Body)
    {
        public string Text => Encoding.UTF8.GetString(Body);
    }

    /// <summary>Records the request (method, URI, headers, every multipart part) before the content is disposed.</summary>
    private sealed class UploadHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? ContentType { get; private set; }
        public string? RawBody { get; private set; }
        public List<Part> Parts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            ContentType = request.Content?.Headers.ContentType?.ToString();
            if (request.Content is MultipartFormDataContent form)
            {
                foreach (var part in form)
                {
                    var cd = part.Headers.ContentDisposition;
                    Parts.Add(new Part(Unquote(cd?.Name), Unquote(cd?.FileName), cd?.FileNameStar, part.Headers.ContentType?.MediaType,
                        await part.ReadAsByteArrayAsync(cancellationToken)));
                }
                RawBody = Encoding.UTF8.GetString(await form.ReadAsByteArrayAsync(cancellationToken));
            }
            return await respond(request, cancellationToken);
        }

        public Part Field(string name) => Assert.Single(Parts, p => p.Name == name);

        private static string? Unquote(string? s) => s is ['"', .., '"'] ? s[1..^1] : s;
    }

    private static UploadHandler Respond(HttpStatusCode status, string body) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) }));

    private static ImmichClient Client(HttpMessageHandler handler) => new("https://immich.example.com", "key", handler);

    [Fact]
    public async Task Upload_PostsMultipartToAssets_WithAllFields()
    {
        var handler = Respond(HttpStatusCode.Created, "{\"id\":\"asset-1\",\"status\":\"created\"}");
        using var client = Client(handler);

        var result = await client.UploadAsync(Png, FileName, Sha1, Created, Modified);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://immich.example.com/api/assets", handler.Request.RequestUri!.ToString());
        Assert.StartsWith("multipart/form-data", handler.ContentType);

        Assert.Equal(new[] { "assetData", "fileCreatedAt", "fileModifiedAt", "filename", "isFavorite" },
            handler.Parts.Select(p => p.Name));

        var asset = handler.Field("assetData");
        Assert.Equal(FileName, asset.FileName);
        Assert.Null(asset.FileNameStar);
        Assert.Equal("image/png", asset.ContentType);
        Assert.Equal(Png, asset.Body);

        Assert.Equal("2024-11-23T19:17:27.6520000+01:00", handler.Field("fileCreatedAt").Text);
        Assert.Equal("2024-11-23T18:17:28.0050000+00:00", handler.Field("fileModifiedAt").Text);
        Assert.Equal(FileName, handler.Field("filename").Text);
        Assert.Equal("false", handler.Field("isFavorite").Text);
        Assert.DoesNotContain(handler.Parts, p => p.Name is "visibility" or "deviceAssetId" or "deviceId");

        // On the wire: quoted names, the file name, and no per-field content types except the PNG.
        Assert.Contains($"Content-Disposition: form-data; name=\"assetData\"; filename=\"{FileName}\"", handler.RawBody);
        Assert.Contains("Content-Disposition: form-data; name=\"fileCreatedAt\"", handler.RawBody);
        Assert.Single(handler.Parts, p => p.ContentType is not null);

        Assert.Equal(UploadResult.Created("asset-1"), result);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Upload_SendsChecksumHeader_AndKeepsDefaultHeaders()
    {
        var handler = Respond(HttpStatusCode.Created, "{\"id\":\"a\",\"status\":\"created\"}");
        using var client = Client(handler);

        await client.UploadAsync(Png, FileName, Sha1.ToUpperInvariant(), Created, Modified);

        var headers = handler.Request!.Headers;
        Assert.Equal(new[] { Sha1 }, headers.GetValues("x-immich-checksum"));
        Assert.Equal(new[] { "key" }, headers.GetValues("x-api-key"));
        Assert.Contains(headers.Accept, a => a.MediaType == "application/json");
    }

    [Theory]
    [InlineData("2024-11-23T19:17:27.6520000+01:00")]
    [InlineData("2026-07-01T00:00:00.0000000-07:00")]
    [InlineData("2026-01-01T12:00:00.1230000+00:00")]
    public void FormatDate_IsRoundTripWithOffset(string expected)
    {
        var value = DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture);
        var text = ImmichClient.FormatDate(value);
        Assert.Equal(expected, text);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{7}[+-]\d{2}:\d{2}$", text);
    }

    [Fact]
    public void FormatDate_UsesInvariantCulture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            Assert.Equal("2024-11-23T19:17:27.6520000+01:00", ImmichClient.FormatDate(Created));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(AssetVisibility.Archive, "archive")]
    [InlineData(AssetVisibility.Hidden, "hidden")]
    [InlineData(AssetVisibility.Locked, "locked")]
    public async Task Upload_SendsVisibility_WhenNotTimeline(AssetVisibility visibility, string expected)
    {
        var handler = Respond(HttpStatusCode.Created, "{\"id\":\"a\",\"status\":\"created\"}");
        using var client = Client(handler);

        await client.UploadAsync(Png, FileName, Sha1, Created, Modified, visibility);

        Assert.Equal(expected, handler.Field("visibility").Text);
    }

    [Fact]
    public async Task Upload_Timeline_OmitsVisibility()
    {
        var handler = Respond(HttpStatusCode.Created, "{\"id\":\"a\",\"status\":\"created\"}");
        using var client = Client(handler);

        await client.UploadAsync(Png, FileName, Sha1, Created, Modified, AssetVisibility.Timeline);

        Assert.DoesNotContain(handler.Parts, p => p.Name == "visibility");
    }

    [Fact]
    public async Task Upload_FromStream_ReadsFromPosition_AndLeavesStreamOpen()
    {
        var handler = Respond(HttpStatusCode.Created, "{\"id\":\"a\",\"status\":\"created\"}");
        using var client = Client(handler);
        var buffer = new byte[] { 9, 9 }.Concat(Png).ToArray();
        using var stream = new MemoryStream(buffer);
        stream.Position = 2;

        var result = await client.UploadAsync(stream, FileName, Sha1, Created, Modified);

        Assert.Equal(UploadStatus.Created, result.Status);
        Assert.Equal(Png, handler.Field("assetData").Body);
        Assert.True(stream.CanRead); // not disposed
    }

    [Fact]
    public async Task Upload_FromMemorySlice_SendsOnlyThatSlice()
    {
        var handler = Respond(HttpStatusCode.Created, "{\"id\":\"a\",\"status\":\"created\"}");
        using var client = Client(handler);
        var buffer = new byte[] { 9 }.Concat(Png).Concat(new byte[] { 9 }).ToArray();

        await client.UploadAsync(buffer.AsMemory(1, Png.Length), FileName, Sha1, Created, Modified);

        Assert.Equal(Png, handler.Field("assetData").Body);
    }

    [Fact]
    public async Task Upload_NonAsciiFileName_UsesFileNameStar()
    {
        var handler = Respond(HttpStatusCode.Created, "{\"id\":\"a\",\"status\":\"created\"}");
        using var client = Client(handler);

        await client.UploadAsync(Png, "VRChat_é.png", Sha1, Created, Modified);

        Assert.Equal("VRChat_é.png", handler.Field("assetData").FileNameStar);
        Assert.Equal("VRChat_é.png", handler.Field("filename").Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0123456789abcdef0123456789abcdef0123456")]   // 39
    [InlineData("0123456789abcdef0123456789abcdef012345678")] // 41
    [InlineData("0123456789abcdef0123456789abcdef0123456g")]
    public async Task Upload_InvalidChecksum_Throws_WithoutSending(string sha1)
    {
        var handler = Respond(HttpStatusCode.Created, "{}");
        using var client = Client(handler);
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.UploadAsync(Png, FileName, sha1, Created, Modified));
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task Upload_EmptyFileName_Throws()
    {
        using var client = Client(Respond(HttpStatusCode.Created, "{}"));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.UploadAsync(Png, " ", Sha1, Created, Modified));
    }

    [Fact]
    public async Task Upload_201_Created()
    {
        using var client = Client(Respond(HttpStatusCode.Created, "{\"id\":\"new-id\",\"status\":\"created\"}"));
        var result = await client.UploadAsync(Png, FileName, Sha1, Created, Modified);
        Assert.Equal(UploadStatus.Created, result.Status);
        Assert.Equal("new-id", result.AssetId);
        Assert.Equal(201, result.HttpStatus);
    }

    [Fact]
    public async Task Upload_200_Duplicate()
    {
        using var client = Client(Respond(HttpStatusCode.OK, "{\"id\":\"old-id\",\"status\":\"duplicate\"}"));
        var result = await client.UploadAsync(Png, FileName, Sha1, Created, Modified);
        Assert.Equal(UploadResult.Duplicate("old-id"), result);
        Assert.Equal(200, result.HttpStatus);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Upload_401_403_AuthError(HttpStatusCode status)
    {
        const string body = "{\"message\":\"Missing required permission: asset.upload\",\"statusCode\":403}";
        using var client = Client(Respond(status, body));
        var result = await client.UploadAsync(Png, FileName, Sha1, Created, Modified);
        Assert.Equal(UploadStatus.AuthError, result.Status);
        Assert.Equal((int)status, result.HttpStatus);
        Assert.Null(result.AssetId);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Upload_400_Rejected_WithBody()
    {
        const string body = "{\"message\":[\"fileCreatedAt must be a Date instance\"],\"error\":\"Bad Request\",\"statusCode\":400}";
        using var client = Client(Respond(HttpStatusCode.BadRequest, body));
        var result = await client.UploadAsync(Png, FileName, Sha1, Created, Modified);
        Assert.Equal(UploadResult.Rejected(400, body), result);
        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge)]
    [InlineData(HttpStatusCode.UnsupportedMediaType)]
    public async Task Upload_OtherClientErrors_Rejected(HttpStatusCode status)
    {
        using var client = Client(Respond(status, "nope"));
        var result = await client.UploadAsync(Png, FileName, Sha1, Created, Modified);
        Assert.Equal(UploadResult.Rejected((int)status, "nope"), result);
    }

    [Fact]
    public async Task Upload_LongErrorBody_IsTruncated()
    {
        var body = new string('x', ImmichClient.MaxErrorBodyLength + 100);
        using var client = Client(Respond(HttpStatusCode.BadRequest, body));
        var result = await client.UploadAsync(Png, FileName, Sha1, Created, Modified);
        Assert.Equal(ImmichClient.MaxErrorBodyLength + 1, result.Message!.Length);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Upload_ServerErrors_Transient(HttpStatusCode status)
    {
        using var client = Client(Respond(status, "busy"));
        var result = await client.UploadAsync(Png, FileName, Sha1, Created, Modified);
        Assert.Equal(UploadResult.Transient((int)status, "busy"), result);
        Assert.False(result.Succeeded);
    }

    [Theory]
    [InlineData(HttpStatusCode.Created, "")]
    [InlineData(HttpStatusCode.Created, "{}")]
    [InlineData(HttpStatusCode.OK, "<html>captive portal</html>")]
    [InlineData(HttpStatusCode.OK, "{\"id\":\"\"}")]
    [InlineData(HttpStatusCode.OK, "[]")]
    public async Task Upload_SuccessWithoutId_Transient(HttpStatusCode status, string body)
    {
        using var client = Client(Respond(status, body));
        var result = await client.UploadAsync(Png, FileName, Sha1, Created, Modified);
        Assert.Equal(UploadStatus.Transient, result.Status);
        Assert.Equal((int)status, result.HttpStatus);
    }

    [Fact]
    public async Task Upload_NetworkError_Transient()
    {
        using var client = Client(new UploadHandler((_, _) => throw new HttpRequestException("connection refused")));
        var result = await client.UploadAsync(Png, FileName, Sha1, Created, Modified);
        Assert.Equal(UploadStatus.Transient, result.Status);
        Assert.Null(result.HttpStatus);
        Assert.Contains("connection refused", result.Message);
    }

    [Fact]
    public async Task Upload_StreamReadError_Transient()
    {
        using var client = Client(new UploadHandler((_, _) => throw new IOException("file locked")));
        var result = await client.UploadAsync(Png, FileName, Sha1, Created, Modified);
        Assert.Equal(UploadStatus.Transient, result.Status);
    }

    [Fact]
    public async Task Upload_Timeout_Transient()
    {
        var handler = new UploadHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        using var client = Client(handler);
        client.Http.Timeout = TimeSpan.FromMilliseconds(50);

        var result = await client.UploadAsync(Png, FileName, Sha1, Created, Modified);

        Assert.Equal(UploadStatus.Transient, result.Status);
        Assert.Null(result.HttpStatus);
    }

    [Fact]
    public async Task Upload_CallerCancellation_Throws()
    {
        var handler = new UploadHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });
        using var client = Client(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.UploadAsync(Png, FileName, Sha1, Created, Modified, cancellationToken: cts.Token));
    }
}
