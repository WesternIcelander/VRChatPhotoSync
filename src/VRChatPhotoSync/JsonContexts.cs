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
