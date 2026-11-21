namespace VRChatPhotoSync;

/// <summary>
/// The Immich calls the <see cref="UploadWorker"/> makes. <see cref="ImmichClient"/> is the real one;
/// tests use a fake.
/// </summary>
public interface IUploadClient
{
    /// <summary>Uploads a PNG from an in-memory snapshot (see <see cref="ImmichClient.UploadAsync(ReadOnlyMemory{byte}, string, string, DateTimeOffset, DateTimeOffset, AssetVisibility, CancellationToken)"/>).</summary>
    Task<UploadResult> UploadAsync(
        ReadOnlyMemory<byte> content, string fileName, string sha1, DateTimeOffset createdAt, DateTimeOffset modifiedAt,
        AssetVisibility visibility = AssetVisibility.Timeline, CancellationToken cancellationToken = default);

    /// <summary>Finds the album by exact name, or creates it.</summary>
    Task<ApiResult<string>> GetOrCreateAlbumAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Adds assets to an album (already in the album counts as success).</summary>
    Task<ApiResult<AlbumAddResult>> AddToAlbumAsync(string albumId, IReadOnlyCollection<string> assetIds, CancellationToken cancellationToken = default);

    /// <summary>The key's permissions (<c>GET api-keys/me</c>), to decide whether a late-edited photo can replace its old asset.</summary>
    Task<ApiKeyCheckResult> GetMyApiKeyAsync(string? albumName = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>Copies albums, favourite etc. from the old asset to its replacement (<c>PUT assets/copy</c>).</summary>
    Task<ApiResult> CopyAssetAsync(string sourceId, string targetId, CancellationToken cancellationToken = default);

    /// <summary>Moves assets to the trash (never forced).</summary>
    Task<ApiResult> TrashAssetsAsync(IReadOnlyCollection<string> assetIds, CancellationToken cancellationToken = default);

    /// <summary>Asks which files the server already has, by SHA-1 (<c>POST assets/bulk-upload-check</c>), for the backfill.</summary>
    Task<ApiResult<IReadOnlyList<BulkCheckResult>>> BulkUploadCheckAsync(IReadOnlyList<BulkCheckItem> items, CancellationToken cancellationToken = default);
}

public sealed partial class ImmichClient : IUploadClient
{
}
