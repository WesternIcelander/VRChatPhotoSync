using System.Net;

namespace VRChatPhotoSync.Tests;

public class ImmichClientTests
{
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public bool Disposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    [Theory]
    [InlineData("https://immich.example.com")]
    [InlineData("https://immich.example.com/")]
    [InlineData("https://immich.example.com/api")]
    [InlineData("https://immich.example.com/api/")]
    public void BaseAddress_EndsInApiSlash(string url)
    {
        using var client = new ImmichClient(url, "key", new CaptureHandler());
        Assert.Equal(new Uri("https://immich.example.com/api/"), client.BaseAddress);
        Assert.Equal(client.BaseAddress, client.Http.BaseAddress);
    }

    [Fact]
    public async Task RelativePath_ResolvesUnderApi()
    {
        var handler = new CaptureHandler();
        using var client = new ImmichClient("https://immich.example.com/sub/", "key", handler);
        await client.Http.GetAsync("server/ping");
        Assert.Equal("https://immich.example.com/sub/api/server/ping", handler.Request!.RequestUri!.ToString());
    }

    [Fact]
    public async Task Requests_CarryApiKeyAcceptAndUserAgent()
    {
        var handler = new CaptureHandler();
        using var client = new ImmichClient("https://immich.example.com", "  secret-key \n", handler);
        await client.Http.GetAsync("server/ping");

        var request = handler.Request!;
        Assert.Equal(new[] { "secret-key" }, request.Headers.GetValues("x-api-key"));
        Assert.Contains(request.Headers.Accept, a => a.MediaType == "application/json");
        var ua = Assert.Single(request.Headers.UserAgent);
        Assert.Equal("VRChatPhotoSync", ua.Product!.Name);
        Assert.Equal(ImmichClient.AppVersion, ua.Product.Version);
        Assert.Equal($"VRChatPhotoSync/{ImmichClient.AppVersion}", request.Headers.UserAgent.ToString());
    }

    [Fact]
    public void AppVersion_MatchesAssemblyVersionWithoutBuildMetadata()
    {
        Assert.Equal("0.1.0", ImmichClient.AppVersion);
    }

    [Fact]
    public void Timeout_IsGenerousForUploads()
    {
        using var client = new ImmichClient("https://immich.example.com", "key", new CaptureHandler());
        Assert.Equal(TimeSpan.FromMinutes(5), client.Http.Timeout);
    }

    [Theory]
    [InlineData("")]
    [InlineData("immich.example.com")]
    [InlineData("ftp://immich.example.com")]
    public void InvalidUrl_Throws(string url)
    {
        Assert.Throws<ArgumentException>(() => new ImmichClient(url, "key", new CaptureHandler()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyKey_Throws(string key)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ImmichClient("https://immich.example.com", key, new CaptureHandler()));
    }

    [Fact]
    public void Dispose_DisposesHandler()
    {
        var handler = new CaptureHandler();
        new ImmichClient("https://immich.example.com", "key", handler).Dispose();
        Assert.True(handler.Disposed);
    }
}
