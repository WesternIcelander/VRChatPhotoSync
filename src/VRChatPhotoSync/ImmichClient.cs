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

    /// <summary>
    /// Asks the server about the API key in use: <c>GET api-keys/me</c>, which needs no
    /// permission, only a valid key. On success the result carries the key's name and
    /// permissions plus warnings: one if the key can't upload (neither <c>all</c> nor
    /// <c>asset.upload</c>), and one listing the missing album permissions when
    /// <paramref name="albumName"/> is set. 401/403 → <see cref="ApiKeyCheckStatus.KeyRejected"/>;
    /// network errors and timeouts (default <see cref="DefaultPingTimeout"/>) →
    /// <see cref="ApiKeyCheckStatus.Unreachable"/>; other statuses or bodies →
    /// <see cref="ApiKeyCheckStatus.UnexpectedResponse"/>. Throws
    /// <see cref="OperationCanceledException"/> only when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    public async Task<ApiKeyCheckResult> GetMyApiKeyAsync(
        string? albumName = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout ?? DefaultPingTimeout);
        try
        {
            using var response = await _http.GetAsync("api-keys/me", cts.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return ApiKeyCheckResult.Failed(ApiKeyCheckStatus.KeyRejected, status);
            if (!response.IsSuccessStatusCode)
                return ApiKeyCheckResult.Failed(ApiKeyCheckStatus.UnexpectedResponse, status);

            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var key = ApiKeyInfo.TryParse(body);
            if (key is null)
                return ApiKeyCheckResult.Failed(ApiKeyCheckStatus.UnexpectedResponse, status);

            return new ApiKeyCheckResult(ApiKeyCheckStatus.Ok, status, key, key.GetWarnings(albumName));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiKeyCheckResult.Failed(ApiKeyCheckStatus.Unreachable, null);
        }
        catch (HttpRequestException)
        {
            return ApiKeyCheckResult.Failed(ApiKeyCheckStatus.Unreachable, null);
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Outcome of <see cref="ImmichClient.GetMyApiKeyAsync"/>.</summary>
public enum ApiKeyCheckStatus
{
    /// <summary>The key is valid; see <see cref="ApiKeyCheckResult.Key"/> and the warnings.</summary>
    Ok,
    /// <summary>The server answered 401 or 403: the key is wrong, expired or deleted.</summary>
    KeyRejected,
    /// <summary>Network error or timeout.</summary>
    Unreachable,
    /// <summary>Another status, or a body that isn't an API key object (probably not Immich).</summary>
    UnexpectedResponse,
}

/// <summary>Result of <see cref="ImmichClient.GetMyApiKeyAsync"/>.</summary>
/// <param name="Status">What happened.</param>
/// <param name="HttpStatus">The HTTP status code, or null when there was no response.</param>
/// <param name="Key">The key's details when <paramref name="Status"/> is <see cref="ApiKeyCheckStatus.Ok"/>.</param>
/// <param name="Warnings">Missing-permission warnings (empty unless the status is Ok).</param>
public sealed record ApiKeyCheckResult(
    ApiKeyCheckStatus Status, int? HttpStatus, ApiKeyInfo? Key, IReadOnlyList<string> Warnings)
{
    internal static ApiKeyCheckResult Failed(ApiKeyCheckStatus status, int? httpStatus) =>
        new(status, httpStatus, null, []);

    /// <summary>True when the key is valid and can upload.</summary>
    public bool CanUpload => Status == ApiKeyCheckStatus.Ok && Key!.CanUpload;
}

/// <summary>
/// An Immich API key as returned by <c>GET api-keys/me</c> (<c>APIKeyResponseDto</c>):
/// its id, name and permission list. <c>all</c> grants every permission.
/// </summary>
public sealed record ApiKeyInfo(string? Id, string? Name, IReadOnlyList<string> Permissions)
{
    public const string PermissionAll = "all";
    public const string PermissionAssetUpload = "asset.upload";
    public const string PermissionAlbumRead = "album.read";
    public const string PermissionAlbumCreate = "album.create";
    public const string PermissionAlbumAssetCreate = "albumAsset.create";
    public const string PermissionAssetCopy = "asset.copy";
    public const string PermissionAssetDelete = "asset.delete";

    /// <summary>Permissions the optional album feature needs.</summary>
    public static IReadOnlyList<string> AlbumPermissions { get; } =
        [PermissionAlbumRead, PermissionAlbumCreate, PermissionAlbumAssetCreate];

    /// <summary>True if the key has <c>all</c> or exactly <paramref name="permission"/>.</summary>
    public bool Has(string permission) =>
        Permissions.Contains(PermissionAll, StringComparer.Ordinal)
        || Permissions.Contains(permission, StringComparer.Ordinal);

    public bool CanUpload => Has(PermissionAssetUpload);

    /// <summary>Album permissions this key lacks (empty when it has them all).</summary>
    public IReadOnlyList<string> MissingAlbumPermissions => AlbumPermissions.Where(p => !Has(p)).ToArray();

    /// <summary>True if the key can replace late-edited photos (copy, then trash the old asset).</summary>
    public bool CanReplace => Has(PermissionAssetCopy) && Has(PermissionAssetDelete);

    /// <summary>
    /// Warnings for missing permissions: no <c>asset.upload</c> (and no <c>all</c>), and, when
    /// <paramref name="albumName"/> is not blank, any missing album permission.
    /// </summary>
    public IReadOnlyList<string> GetWarnings(string? albumName)
    {
        var warnings = new List<string>();
        if (!CanUpload)
            warnings.Add($"The API key is missing the '{PermissionAssetUpload}' permission, so photos can't be uploaded.");
        if (!string.IsNullOrWhiteSpace(albumName))
        {
            var missing = MissingAlbumPermissions;
            if (missing.Count > 0)
                warnings.Add($"An album is set, but the API key is missing: {string.Join(", ", missing.Select(p => $"'{p}'"))}.");
        }
        return warnings;
    }

    /// <summary>Parses the <c>api-keys/me</c> body. Returns null unless it is an object with a <c>permissions</c> string array.</summary>
    internal static ApiKeyInfo? TryParse(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("permissions", out var perms)
                || perms.ValueKind != JsonValueKind.Array)
                return null;

            var list = new List<string>();
            foreach (var p in perms.EnumerateArray())
            {
                if (p.ValueKind == JsonValueKind.String && p.GetString() is { } s)
                    list.Add(s);
            }
            return new ApiKeyInfo(GetString(root, "id"), GetString(root, "name"), list);
        }
        catch (JsonException)
        {
            return null;
        }

        static string? GetString(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
