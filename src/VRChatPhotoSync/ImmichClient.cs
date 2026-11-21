using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace VRChatPhotoSync;

/// <summary>
/// Talks to the Immich API. Holds one <see cref="HttpClient"/> for the whole session, set up
/// with the API base address, the <c>x-api-key</c> header, <c>Accept: application/json</c>,
/// a generous timeout (uploads of large PNGs over slow links) and the app's User-Agent.
/// Request paths are relative to the base address (for example <c>server/ping</c>), which
/// always ends in <c>/api/</c>.
/// </summary>
public sealed class ImmichClient : IDisposable
{
    /// <summary>Timeout for every request. Long enough for a ~50 MB PNG over a slow upload link.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    /// <summary>Header Immich reads the API key from.</summary>
    public const string ApiKeyHeader = "x-api-key";

    /// <summary>Product name used in the User-Agent header.</summary>
    public const string UserAgentProduct = "VRChatPhotoSync";

    private readonly HttpClient _http;

    /// <summary>
    /// Creates a client for <paramref name="serverUrl"/> (any form accepted by
    /// <see cref="ConfigStore.NormalizeServerUrl"/>, for example <c>https://immich.example.com</c>
    /// or <c>…/api</c>). <paramref name="handler"/> lets tests substitute a fake transport; it
    /// is disposed together with the client. Throws <see cref="ArgumentException"/> if the URL
    /// isn't an absolute http/https URL or the key is empty.
    /// </summary>
    public ImmichClient(string serverUrl, string apiKey, HttpMessageHandler? handler = null)
    {
        if (!ConfigStore.IsValidServerUrl(serverUrl))
            throw new ArgumentException("The server URL must be an absolute http or https URL.", nameof(serverUrl));
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        BaseAddress = new Uri(ConfigStore.NormalizeServerUrl(serverUrl)! + "/");

        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.BaseAddress = BaseAddress;
        _http.Timeout = DefaultTimeout;

        var headers = _http.DefaultRequestHeaders;
        headers.Add(ApiKeyHeader, apiKey.Trim());
        headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        headers.UserAgent.Add(new ProductInfoHeaderValue(UserAgentProduct, AppVersion));
    }

    /// <summary>The API base address, always ending in <c>/api/</c>.</summary>
    public Uri BaseAddress { get; }

    /// <summary>The shared HTTP client (exposed for the API methods and tests).</summary>
    internal HttpClient Http => _http;

    /// <summary>The app version for the User-Agent, for example <c>0.1.0</c> (no build metadata).</summary>
    public static string AppVersion { get; } = GetAppVersion();

    private static string GetAppVersion()
    {
        var assembly = typeof(ImmichClient).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // Strip SourceLink build metadata such as "+abc123".
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>Default time limit for <see cref="PingAsync"/>; much shorter than the upload timeout.</summary>
    public static readonly TimeSpan DefaultPingTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Checks that the server is reachable: <c>GET server/ping</c>, which needs no permission
    /// and answers <c>{"res":"pong"}</c>. Returns true only for a success status with that body.
    /// Network errors, timeouts (<paramref name="timeout"/>, default
    /// <see cref="DefaultPingTimeout"/>), non-success statuses and unexpected bodies return false.
    /// Throws <see cref="OperationCanceledException"/> only when <paramref name="cancellationToken"/>
    /// itself is cancelled.
    /// </summary>
    public async Task<bool> PingAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? DefaultPingTimeout);
        try
        {
            using var response = await _http.GetAsync("server/ping", cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return false;

            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("res", out var res)
                && res.ValueKind == JsonValueKind.String
                && res.GetString() == "pong";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false; // our own timeout (or HttpClient's)
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}
