using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace VRChatPhotoSync;

// Bulk upload check, albums, replace (copy + trash) and server version.
public sealed partial class ImmichClient
{
    /// <summary>Most items sent in one <c>assets/bulk-upload-check</c> request.</summary>
    public const int BulkCheckBatchSize = 500;

    private readonly SemaphoreSlim _albumLock = new(1, 1);
    private (string Name, string Id)? _albumCache;

    /// <summary>
    /// Asks the server which files it already has: <c>POST assets/bulk-upload-check</c> with
    /// <c>{"assets":[{"id","checksum"}]}</c>, in batches of <see cref="BulkCheckBatchSize"/>.
    /// The id is the caller's own key (for example the relative path) and comes back in the
    /// matching result. Checksums are SHA-1 as 40 hex characters. An empty list makes no request.
    /// <para>
    /// Returns <see cref="ApiStatus.Ok"/> with every batch's results in order, or the first
    /// failing batch's status (results of earlier batches are then dropped; the check is cheap
    /// to repeat). Throws <see cref="OperationCanceledException"/> only when
    /// <paramref name="cancellationToken"/> is cancelled.
    /// </para>
    /// </summary>
    public async Task<ApiResult<IReadOnlyList<BulkCheckResult>>> BulkUploadCheckAsync(
        IReadOnlyList<BulkCheckItem> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item, nameof(items));
            if (string.IsNullOrEmpty(item.Id))
                throw new ArgumentException("Every item needs an id.", nameof(items));
            if (!IsSha1Hex(item.Sha1))
                throw new ArgumentException($"The checksum for '{item.Id}' must be a SHA-1 as 40 hex characters.", nameof(items));
        }

        var all = new List<BulkCheckResult>(items.Count);
        foreach (var batch in items.Chunk(BulkCheckBatchSize))
        {
            var body = new BulkUploadCheckRequestDto
            {
                Assets = batch.Select(i => new BulkUploadCheckItemDto { Id = i.Id, Checksum = i.Sha1.ToLowerInvariant() }).ToList(),
            };
            var result = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Post, "assets/bulk-upload-check")
                {
                    Content = JsonContent.Create(body, ImmichJsonContext.Default.BulkUploadCheckRequestDto),
                },
                ParseBulkCheck, null, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
                return result.As<IReadOnlyList<BulkCheckResult>>();
            all.AddRange(result.Value!);
        }
        return ApiResult<IReadOnlyList<BulkCheckResult>>.Ok(all);
    }

    private static List<BulkCheckResult>? ParseBulkCheck(string body)
    {
        var dto = JsonSerializer.Deserialize(body, ImmichJsonContext.Default.BulkUploadCheckResponseDto);
        if (dto?.Results is not { } results)
            return null;
        var list = new List<BulkCheckResult>(results.Count);
        foreach (var r in results)
        {
            if (r?.Id is null || r.Action is null)
                return null;
            list.Add(new BulkCheckResult(r.Id, r.Action == "accept", r.Reason, r.AssetId, r.IsTrashed ?? false));
        }
        return list;
    }

    /// <summary>
    /// Finds the album called exactly <paramref name="name"/> (<c>GET albums?name=…</c>; the
    /// answer is also filtered by exact name here) or creates it (<c>POST albums</c>
    /// <c>{"albumName"}</c>). The id is cached until a different name is asked for or
    /// <see cref="ForgetAlbum"/> is called; concurrent calls share one lookup, so an album is
    /// never created twice by this client. Failures aren't cached.
    /// </summary>
    public async Task<ApiResult<string>> GetOrCreateAlbumAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await _albumLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_albumCache is { } cached && cached.Name == name)
                return ApiResult<string>.Ok(cached.Id);
            _albumCache = null;

            var found = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, "albums?name=" + Uri.EscapeDataString(name)),
                b => ParseJson(b, ImmichJsonContext.Default.ListAlbumResponseDto), null, cancellationToken).ConfigureAwait(false);
            if (!found.Succeeded)
                return found.As<string>();

            var id = found.Value!.FirstOrDefault(a => a is not null && a.AlbumName == name && !string.IsNullOrWhiteSpace(a.Id))?.Id;
            if (id is null)
            {
                var created = await SendAsync(
                    () => new HttpRequestMessage(HttpMethod.Post, "albums")
                    {
                        Content = JsonContent.Create(new CreateAlbumDto { AlbumName = name }, ImmichJsonContext.Default.CreateAlbumDto),
                    },
                    b => ParseJson(b, ImmichJsonContext.Default.AlbumResponseDto) is { Id: { Length: > 0 } newId } ? newId : null,
                    null, cancellationToken).ConfigureAwait(false);
                if (!created.Succeeded)
                    return created;
                id = created.Value!;
            }

            _albumCache = (name, id);
            return ApiResult<string>.Ok(id);
        }
        finally
        {
            _albumLock.Release();
        }
    }

    /// <summary>Drops the cached album id (for example after the album was deleted on the server).</summary>
    public void ForgetAlbum()
    {
        _albumLock.Wait();
        try { _albumCache = null; }
        finally { _albumLock.Release(); }
    }

    /// <summary>
    /// Adds assets to an album: <c>PUT albums/{albumId}/assets</c> <c>{"ids":[…]}</c>. The server
    /// answers per asset; <c>error: "duplicate"</c> (already in the album) counts as success. Items
    /// that failed for another reason are listed in <see cref="AlbumAddResult.Failed"/> while the
    /// call itself is still <see cref="ApiStatus.Ok"/>. An empty list makes no request.
    /// </summary>
    public async Task<ApiResult<AlbumAddResult>> AddToAlbumAsync(
        string albumId, IReadOnlyCollection<string> assetIds, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(albumId);
        ValidateIds(assetIds, nameof(assetIds));
        if (assetIds.Count == 0)
            return ApiResult<AlbumAddResult>.Ok(new AlbumAddResult([], [], []));

        var body = new BulkIdsDto { Ids = assetIds.ToList() };
        return await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Put, $"albums/{Uri.EscapeDataString(albumId)}/assets")
            {
                Content = JsonContent.Create(body, ImmichJsonContext.Default.BulkIdsDto),
            },
            ParseAlbumAdd, null, cancellationToken).ConfigureAwait(false);
    }

    private static AlbumAddResult? ParseAlbumAdd(string body)
    {
        var items = JsonSerializer.Deserialize(body, ImmichJsonContext.Default.ListBulkIdResponseDto);
        if (items is null)
            return null;
        List<string> added = [], already = [];
        List<AlbumAddFailure> failed = [];
        foreach (var item in items)
        {
            if (item?.Id is null)
                return null;
            if (item.Success)
                added.Add(item.Id);
            else if (item.Error == "duplicate")
                already.Add(item.Id);
            else
                failed.Add(new AlbumAddFailure(item.Id, item.Error));
        }
        return new AlbumAddResult(added, already, failed);
    }

    /// <summary>
    /// Moves albums, favourite, shared links, stack and sidecar from an old asset to its
    /// replacement: <c>PUT assets/copy</c> <c>{"sourceId","targetId"}</c> (needs <c>asset.copy</c>).
    /// </summary>
    public Task<ApiResult> CopyAssetAsync(string sourceId, string targetId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        var body = new AssetCopyDto { SourceId = sourceId, TargetId = targetId };
        return SendNoValueAsync(
            () => new HttpRequestMessage(HttpMethod.Put, "assets/copy")
            {
                Content = JsonContent.Create(body, ImmichJsonContext.Default.AssetCopyDto),
            },
            cancellationToken);
    }

    /// <summary>
    /// Moves assets to the trash: <c>DELETE assets</c> <c>{"ids":[…]}</c> (needs <c>asset.delete</c>).
    /// <c>force</c> is never sent, so they can always be restored from the trash. An empty list
    /// makes no request.
    /// </summary>
    public Task<ApiResult> TrashAssetsAsync(IReadOnlyCollection<string> assetIds, CancellationToken cancellationToken = default)
    {
        ValidateIds(assetIds, nameof(assetIds));
        if (assetIds.Count == 0)
            return Task.FromResult(ApiResult.Ok());
        var body = new AssetBulkDeleteDto { Ids = assetIds.ToList() };
        return SendNoValueAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, "assets")
            {
                Content = JsonContent.Create(body, ImmichJsonContext.Default.AssetBulkDeleteDto),
            },
            cancellationToken);
    }

    /// <summary>
    /// The server's version (<c>GET server/version</c>, no permission needed), for the settings
    /// window. Uses a short timeout (default <see cref="DefaultPingTimeout"/>).
    /// </summary>
    public Task<ApiResult<Version>> GetServerVersionAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "server/version"),
            b => ParseJson(b, ImmichJsonContext.Default.ServerVersionResponseDto) is { Major: int major and >= 0, Minor: int minor and >= 0, Patch: int patch and >= 0 }
                ? new Version(major, minor, patch)
                : null,
            timeout ?? DefaultPingTimeout, cancellationToken);

    private static void ValidateIds(IReadOnlyCollection<string> ids, string paramName)
    {
        ArgumentNullException.ThrowIfNull(ids, paramName);
        if (ids.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Asset ids must not be empty.", paramName);
    }

    private static T? ParseJson<T>(string body, JsonTypeInfo<T> typeInfo) => JsonSerializer.Deserialize(body, typeInfo);

    private async Task<ApiResult> SendNoValueAsync(Func<HttpRequestMessage> createRequest, CancellationToken cancellationToken)
    {
        var result = await SendAsync<bool?>(createRequest, _ => true, null, cancellationToken).ConfigureAwait(false);
        return new ApiResult(result.Status, result.HttpStatus, result.Message);
    }

    /// <summary>
    /// Sends a request and maps the answer like uploads do: success with a body that
    /// <paramref name="parse"/> accepts → <see cref="ApiStatus.Ok"/>; 401/403 →
    /// <see cref="ApiStatus.AuthError"/>; 408, 429, 5xx, timeouts, network errors and unreadable
    /// success bodies → <see cref="ApiStatus.Transient"/>; other statuses → <see cref="ApiStatus.Rejected"/>.
    /// </summary>
    private async Task<ApiResult<T>> SendAsync<T>(
        Func<HttpRequestMessage> createRequest, Func<string, T?> parse, TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } t)
            cts.CancelAfter(t);
        try
        {
            using var request = createRequest();
            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                T? value;
                try
                {
                    value = parse(body);
                }
                catch (JsonException)
                {
                    value = default;
                }
                return value is null
                    ? ApiResult<T>.Fail(ApiStatus.Transient, status, $"Unexpected response body: {Truncate(body)}")
                    : ApiResult<T>.Ok(value, status);
            }
            return status switch
            {
                401 or 403 => ApiResult<T>.Fail(ApiStatus.AuthError, status, Truncate(body)),
                408 or 429 or >= 500 => ApiResult<T>.Fail(ApiStatus.Transient, status, Truncate(body)),
                _ => ApiResult<T>.Fail(ApiStatus.Rejected, status, Truncate(body)),
            };
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ApiResult<T>.Fail(ApiStatus.Transient, null, $"Timed out: {ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<T>.Fail(ApiStatus.Transient, null, ex.Message);
        }
        catch (IOException ex)
        {
            return ApiResult<T>.Fail(ApiStatus.Transient, null, ex.Message);
        }
    }
}

/// <summary>How an API call (other than an upload) went.</summary>
public enum ApiStatus
{
    /// <summary>Success.</summary>
    Ok,
    /// <summary>401/403: the key is wrong or lacks the permission for this call.</summary>
    AuthError,
    /// <summary>The server refused the request (400, 404 or another 4xx). Retrying won't help.</summary>
    Rejected,
    /// <summary>5xx, 408/429, timeout, network error or an unreadable success body. Retry later.</summary>
    Transient,
}

/// <summary>Result of an API call that returns nothing.</summary>
/// <param name="Status">What happened.</param>
/// <param name="HttpStatus">The HTTP status code, or null when there was no response.</param>
/// <param name="Message">The (truncated) response body or error message for failures.</param>
public sealed record ApiResult(ApiStatus Status, int? HttpStatus, string? Message)
{
    public static ApiResult Ok(int? httpStatus = null) => new(ApiStatus.Ok, httpStatus, null);
    public bool Succeeded => Status == ApiStatus.Ok;
}

/// <summary>Result of an API call that returns a value (set only when <see cref="Status"/> is Ok).</summary>
public sealed record ApiResult<T>(ApiStatus Status, T? Value, int? HttpStatus, string? Message)
{
    public static ApiResult<T> Ok(T value, int? httpStatus = null) => new(ApiStatus.Ok, value, httpStatus, null);
    public static ApiResult<T> Fail(ApiStatus status, int? httpStatus, string? message) => new(status, default, httpStatus, message);
    public bool Succeeded => Status == ApiStatus.Ok;

    /// <summary>The same failure with another value type.</summary>
    internal ApiResult<TOther> As<TOther>() => ApiResult<TOther>.Fail(Status, HttpStatus, Message);
}

/// <summary>One file for <see cref="ImmichClient.BulkUploadCheckAsync"/>: the caller's id and the file's SHA-1 (hex).</summary>
public sealed record BulkCheckItem(string Id, string Sha1);

/// <summary>
/// The server's answer for one file of a bulk upload check. <paramref name="Accepted"/> means
/// "upload it"; otherwise <paramref name="Reason"/> says why not (<c>duplicate</c> with the
/// existing <paramref name="AssetId"/>, or <c>unsupported-format</c>).
/// </summary>
public sealed record BulkCheckResult(string Id, bool Accepted, string? Reason, string? AssetId, bool IsTrashed)
{
    /// <summary>True when the server already has this file.</summary>
    public bool IsDuplicate => !Accepted && Reason == "duplicate";
}

/// <summary>Per-asset outcome of <see cref="ImmichClient.AddToAlbumAsync"/>.</summary>
/// <param name="Added">Assets newly added.</param>
/// <param name="AlreadyInAlbum">Assets that were already in the album (counts as success).</param>
/// <param name="Failed">Assets the server refused, with its error code (for example <c>no_permission</c> or <c>not_found</c>).</param>
public sealed record AlbumAddResult(IReadOnlyList<string> Added, IReadOnlyList<string> AlreadyInAlbum, IReadOnlyList<AlbumAddFailure> Failed)
{
    public bool AllSucceeded => Failed.Count == 0;
}

/// <summary>An asset that couldn't be added to an album.</summary>
public sealed record AlbumAddFailure(string AssetId, string? Error);
