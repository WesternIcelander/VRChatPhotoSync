using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRChatPhotoSync;

/// <summary>
/// Source-generated JSON metadata for the Immich API's request and response bodies
/// (camelCase, as the API uses). Every API DTO is listed here so nothing is serialized
/// through reflection.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PingResponseDto))]
[JsonSerializable(typeof(ApiKeyResponseDto))]
[JsonSerializable(typeof(AssetMediaResponseDto))]
[JsonSerializable(typeof(BulkUploadCheckRequestDto))]
[JsonSerializable(typeof(BulkUploadCheckResponseDto))]
[JsonSerializable(typeof(List<AlbumResponseDto>))]
[JsonSerializable(typeof(AlbumResponseDto))]
[JsonSerializable(typeof(CreateAlbumDto))]
[JsonSerializable(typeof(BulkIdsDto))]
[JsonSerializable(typeof(List<BulkIdResponseDto>))]
[JsonSerializable(typeof(AssetCopyDto))]
[JsonSerializable(typeof(AssetBulkDeleteDto))]
[JsonSerializable(typeof(ServerVersionResponseDto))]
internal sealed partial class ImmichJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Source-generated JSON metadata for our own <c>config.json</c>: property names as declared
/// (PascalCase), indented, and lenient on read (comments and trailing commas are allowed,
/// since people may edit the file by hand).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext
{
}

/// <summary><c>GET server/ping</c> body: <c>{"res":"pong"}</c>.</summary>
internal sealed class PingResponseDto
{
    public string? Res { get; set; }
}

/// <summary><c>GET api-keys/me</c> body (<c>APIKeyResponseDto</c>); only the fields we use.</summary>
internal sealed class ApiKeyResponseDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public List<string?>? Permissions { get; set; }
}

/// <summary><c>POST assets</c> body (<c>AssetMediaResponseDto</c>): <c>{"id":"…","status":"created"|"duplicate"}</c>.</summary>
internal sealed class AssetMediaResponseDto
{
    public string? Id { get; set; }
    public string? Status { get; set; }
}

/// <summary><c>POST assets/bulk-upload-check</c> request (<c>AssetBulkUploadCheckDto</c>).</summary>
internal sealed class BulkUploadCheckRequestDto
{
    public List<BulkUploadCheckItemDto> Assets { get; set; } = [];
}

/// <summary>One item of a bulk upload check: a client-chosen id and the file's SHA-1.</summary>
internal sealed class BulkUploadCheckItemDto
{
    public string Id { get; set; } = "";
    public string Checksum { get; set; } = "";
}

/// <summary><c>POST assets/bulk-upload-check</c> body (<c>AssetBulkUploadCheckResponseDto</c>).</summary>
internal sealed class BulkUploadCheckResponseDto
{
    public List<BulkUploadCheckResultDto?>? Results { get; set; }
}

/// <summary>One result of a bulk upload check: <c>{id, action, reason?, assetId?, isTrashed?}</c>.</summary>
internal sealed class BulkUploadCheckResultDto
{
    public string? Id { get; set; }
    public string? Action { get; set; }
    public string? Reason { get; set; }
    public string? AssetId { get; set; }
    public bool? IsTrashed { get; set; }
}

/// <summary>An album (<c>AlbumResponseDto</c>); only the fields we use.</summary>
internal sealed class AlbumResponseDto
{
    public string? Id { get; set; }
    public string? AlbumName { get; set; }
}

/// <summary><c>POST albums</c> request (<c>CreateAlbumDto</c>).</summary>
internal sealed class CreateAlbumDto
{
    public string AlbumName { get; set; } = "";
}

/// <summary><c>{"ids":[…]}</c> (<c>BulkIdsDto</c>), for <c>PUT albums/{id}/assets</c>.</summary>
internal sealed class BulkIdsDto
{
    public List<string> Ids { get; set; } = [];
}

/// <summary>One item of <c>PUT albums/{id}/assets</c>'s answer (<c>BulkIdResponseDto</c>).</summary>
internal sealed class BulkIdResponseDto
{
    public string? Id { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}

/// <summary><c>PUT assets/copy</c> request (<c>AssetCopyDto</c>); the other flags default to true on the server.</summary>
internal sealed class AssetCopyDto
{
    public string SourceId { get; set; } = "";
    public string TargetId { get; set; } = "";
}

/// <summary>
/// <c>DELETE assets</c> request (<c>AssetBulkDeleteDto</c>). There is deliberately no <c>force</c>
/// property: assets always go to the trash, where they can be restored.
/// </summary>
internal sealed class AssetBulkDeleteDto
{
    public List<string> Ids { get; set; } = [];
}

/// <summary><c>GET server/version</c> body (<c>ServerVersionResponseDto</c>).</summary>
internal sealed class ServerVersionResponseDto
{
    public int? Major { get; set; }
    public int? Minor { get; set; }
    public int? Patch { get; set; }
}
