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
public sealed partial class ImmichClient : IDisposable
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
            var pong = JsonSerializer.Deserialize(body, ImmichJsonContext.Default.PingResponseDto);
            return pong?.Res == "pong";
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

    /// <summary>Header that lets Immich spot a duplicate from the SHA-1 before reading the whole body.</summary>
    public const string ChecksumHeader = "x-immich-checksum";

    /// <summary>Most characters of an error body kept in an <see cref="UploadResult"/>.</summary>
    internal const int MaxErrorBodyLength = 4000;

    /// <summary>
    /// Uploads a PNG from an in-memory snapshot. See
    /// <see cref="UploadAsync(Stream, string, string, DateTimeOffset, DateTimeOffset, AssetVisibility, CancellationToken)"/>.
    /// </summary>
    public Task<UploadResult> UploadAsync(
        ReadOnlyMemory<byte> content, string fileName, string sha1, DateTimeOffset createdAt, DateTimeOffset modifiedAt,
        AssetVisibility visibility = AssetVisibility.Timeline, CancellationToken cancellationToken = default) =>
        SendUploadAsync(new ReadOnlyMemoryContent(content), fileName, sha1, createdAt, modifiedAt, visibility, cancellationToken);

    /// <summary>
    /// Uploads a PNG: <c>POST assets</c> as multipart/form-data with <c>assetData</c>
    /// (<c>image/png</c>, read from <paramref name="content"/>'s current position; the stream is
    /// left open), <c>fileCreatedAt</c>/<c>fileModifiedAt</c> (ISO-8601 round-trip format with
    /// offset), <c>filename</c>, <c>isFavorite=false</c>, and <c>visibility</c> unless it is
    /// <see cref="AssetVisibility.Timeline"/> (the server default). <paramref name="sha1"/> (40 hex
    /// characters) is sent as <c>x-immich-checksum</c>.
    /// <para>
    /// Result: 201 → <see cref="UploadStatus.Created"/>; 200 → <see cref="UploadStatus.Duplicate"/>;
    /// 401/403 → <see cref="UploadStatus.AuthError"/>; 408, 429, 5xx, a timeout, a network error or
    /// a success body without an id → <see cref="UploadStatus.Transient"/>; 400 and other statuses →
    /// <see cref="UploadStatus.Rejected"/> with the response body. Throws
    /// <see cref="OperationCanceledException"/> only when <paramref name="cancellationToken"/> is cancelled.
    /// </para>
    /// </summary>
    public Task<UploadResult> UploadAsync(
        Stream content, string fileName, string sha1, DateTimeOffset createdAt, DateTimeOffset modifiedAt,
        AssetVisibility visibility = AssetVisibility.Timeline, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        return SendUploadAsync(new StreamContent(new LeaveOpenStream(content)), fileName, sha1, createdAt, modifiedAt, visibility, cancellationToken);
    }

    private async Task<UploadResult> SendUploadAsync(
        HttpContent fileContent, string fileName, string sha1, DateTimeOffset createdAt, DateTimeOffset modifiedAt,
        AssetVisibility visibility, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
            if (!IsSha1Hex(sha1))
                throw new ArgumentException("The checksum must be a SHA-1 as 40 hex characters.", nameof(sha1));
        }
        catch
        {
            fileContent.Dispose();
            throw;
        }

        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        fileContent.Headers.ContentDisposition = FormData("assetData", fileName);
        form.Add(fileContent);
        AddField(form, "fileCreatedAt", FormatDate(createdAt));
        AddField(form, "fileModifiedAt", FormatDate(modifiedAt));
        AddField(form, "filename", fileName);
        AddField(form, "isFavorite", "false");
        if (visibility != AssetVisibility.Timeline)
            AddField(form, "visibility", visibility.ToApiValue());

        using var request = new HttpRequestMessage(HttpMethod.Post, "assets") { Content = form };
        request.Headers.Add(ChecksumHeader, sha1.ToLowerInvariant());

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            switch (status)
            {
                case 200 or 201:
                    var id = TryParseAssetId(body);
                    if (id is null)
                        return UploadResult.Transient(status, $"Unexpected response body: {Truncate(body)}");
                    return status == 201 ? UploadResult.Created(id) : UploadResult.Duplicate(id);
                case 401 or 403:
                    return UploadResult.AuthError(status, Truncate(body));
                case 408 or 429 or >= 500:
                    return UploadResult.Transient(status, Truncate(body));
                default:
                    return UploadResult.Rejected(status, Truncate(body));
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return UploadResult.Transient(null, $"Timed out: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return UploadResult.Transient(null, ex.Message);
        }
        catch (IOException ex)
        {
            return UploadResult.Transient(null, ex.Message);
        }
    }

    /// <summary>Formats a date for Immich: ISO-8601 round-trip (<c>"o"</c>), always with an offset.</summary>
    internal static string FormatDate(DateTimeOffset value) =>
        value.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

    private static bool IsSha1Hex(string? value) =>
        value is { Length: 40 } && value.All(char.IsAsciiHexDigit);

    private static string? TryParseAssetId(string body)
    {
        try
        {
            var dto = JsonSerializer.Deserialize(body, ImmichJsonContext.Default.AssetMediaResponseDto);
            return string.IsNullOrWhiteSpace(dto?.Id) ? null : dto.Id;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string body) =>
        body.Length <= MaxErrorBodyLength ? body : body[..MaxErrorBodyLength] + "…";

    private static ContentDispositionHeaderValue FormData(string name, string? fileName = null)
    {
        var disposition = new ContentDispositionHeaderValue("form-data") { Name = Quote(name) };
        if (fileName is not null)
        {
            if (fileName.All(c => c is >= ' ' and < (char)127))
                disposition.FileName = Quote(fileName);
            else
                disposition.FileNameStar = fileName;
        }
        return disposition;

        static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static void AddField(MultipartFormDataContent form, string name, string value)
    {
        var content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(value));
        content.Headers.ContentDisposition = FormData(name);
        form.Add(content);
    }

    public void Dispose()
    {
        _http.Dispose();
        _albumLock.Dispose();
    }

    /// <summary>Wraps a caller's stream so disposing the request doesn't close it.</summary>
    private sealed class LeaveOpenStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Outcome of <see cref="ImmichClient.UploadAsync(Stream, string, string, DateTimeOffset, DateTimeOffset, AssetVisibility, CancellationToken)"/>.</summary>
public enum UploadStatus
{
    /// <summary>201: a new asset was created.</summary>
    Created,
    /// <summary>200: the server already has this file (by checksum). Counts as success; don't retry.</summary>
    Duplicate,
    /// <summary>401/403: the key is wrong or lacks <c>asset.upload</c>. Pause until the user fixes it.</summary>
    AuthError,
    /// <summary>The server refused this file (400 or another 4xx). Retrying won't help.</summary>
    Rejected,
    /// <summary>5xx, 408/429, timeout, network error or an unreadable success body. Retry later.</summary>
    Transient,
}

/// <summary>Result of an upload.</summary>
/// <param name="Status">What happened.</param>
/// <param name="AssetId">The Immich asset id for <see cref="UploadStatus.Created"/> and <see cref="UploadStatus.Duplicate"/>.</param>
/// <param name="HttpStatus">The HTTP status code, or null when there was no response.</param>
/// <param name="Message">The (truncated) response body or error message for failures.</param>
public sealed record UploadResult(UploadStatus Status, string? AssetId, int? HttpStatus, string? Message)
{
    public static UploadResult Created(string assetId) => new(UploadStatus.Created, assetId, 201, null);
    public static UploadResult Duplicate(string assetId) => new(UploadStatus.Duplicate, assetId, 200, null);
    public static UploadResult AuthError(int httpStatus, string? body) => new(UploadStatus.AuthError, null, httpStatus, body);
    public static UploadResult Rejected(int httpStatus, string? body) => new(UploadStatus.Rejected, null, httpStatus, body);
    public static UploadResult Transient(int? httpStatus, string? message) => new(UploadStatus.Transient, null, httpStatus, message);

    /// <summary>True for <see cref="UploadStatus.Created"/> and <see cref="UploadStatus.Duplicate"/>.</summary>
    public bool Succeeded => Status is UploadStatus.Created or UploadStatus.Duplicate;
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
            var dto = JsonSerializer.Deserialize(body, ImmichJsonContext.Default.ApiKeyResponseDto);
            if (dto?.Permissions is not { } perms)
                return null;
            return new ApiKeyInfo(dto.Id, dto.Name, perms.OfType<string>().ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
