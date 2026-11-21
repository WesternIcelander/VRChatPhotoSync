using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit.Abstractions;

namespace VRChatPhotoSync.Tests;

/// <summary>
/// Where the integration tests find a real Immich server: the environment variables
/// <c>IMMICH_TEST_URL</c>/<c>IMMICH_TEST_API_KEY</c>, or else the git-ignored
/// <c>secrets.local.json</c> (<c>{"serverUrl","apiKey"}</c>) at the repository root.
/// The key is never printed.
/// </summary>
internal static class IntegrationSettings
{
    public const string UrlVariable = "IMMICH_TEST_URL";
    public const string KeyVariable = "IMMICH_TEST_API_KEY";
    public const string SecretsFileName = "secrets.local.json";

    private static readonly Lazy<(string Url, string Key)?> s_settings = new(Load);

    /// <summary>The server URL and key, or null when neither source has both.</summary>
    public static (string Url, string Key)? Settings => s_settings.Value;

    /// <summary>Why the tests are skipped, or null when a server is configured.</summary>
    public static string? SkipReason => Settings is null
        ? $"No Immich test server: set {UrlVariable}/{KeyVariable} or fill in {SecretsFileName} at the repo root."
        : null;

    private static (string, string)? Load()
    {
        var url = Environment.GetEnvironmentVariable(UrlVariable);
        var key = Environment.GetEnvironmentVariable(KeyVariable);
        if (!string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(key))
            return (url.Trim(), key.Trim());

        var file = FindSecretsFile();
        if (file is null)
            return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            url = root.TryGetProperty("serverUrl", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            key = root.TryGetProperty("apiKey", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
        return string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key) ? null : (url.Trim(), key.Trim());
    }

    /// <summary>Walks up from the test output folder to the folder holding the solution.</summary>
    private static string? FindSecretsFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VRChatPhotoSync.sln")))
            {
                var path = Path.Combine(dir.FullName, SecretsFileName);
                return File.Exists(path) ? path : null;
            }
        }
        return null;
    }
}

/// <summary>A <see cref="FactAttribute"/> that skips itself when no Immich test server is configured.</summary>
public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (IntegrationSettings.SkipReason is { } reason)
            Skip = reason;
    }
}

/// <summary>
/// Runs <see cref="ImmichClient"/> against a real Immich server (see <see cref="IntegrationSettings"/>).
/// Every uploaded asset is a freshly generated PNG and is moved to the trash at the end when
/// the key has <c>asset.delete</c>. The test album is left in place; its name is logged.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ImmichIntegrationTests(ITestOutputHelper output)
{
    private static ImmichClient CreateClient()
    {
        var (url, key) = IntegrationSettings.Settings!.Value;
        return new ImmichClient(url, key);
    }

    [IntegrationFact]
    public async Task Ping_ReturnsTrue()
    {
        using var client = CreateClient();
        Assert.True(await client.PingAsync());
    }

    [IntegrationFact]
    public async Task ApiKeysMe_ReturnsKeyThatCanUpload()
    {
        using var client = CreateClient();
        var result = await client.GetMyApiKeyAsync();

        Assert.Equal(ApiKeyCheckStatus.Ok, result.Status);
        Assert.NotNull(result.Key);
        Assert.True(result.CanUpload, "The test API key needs 'asset.upload'.");
        output.WriteLine($"API key '{result.Key!.Name}' permissions: {string.Join(", ", result.Key.Permissions)}");
    }

    [IntegrationFact]
    public async Task ServerVersion_IsReturned()
    {
        using var client = CreateClient();
        var result = await client.GetServerVersionAsync();

        Assert.Equal(ApiStatus.Ok, result.Status);
        Assert.True(result.Value!.Major >= 1, $"Unexpected version {result.Value}");
        output.WriteLine($"Immich server version {result.Value}");
    }

    [IntegrationFact]
    public async Task Upload_Duplicate_BulkCheck_Album_CopyAndTrash()
    {
        using var client = CreateClient();
        var keyCheck = await client.GetMyApiKeyAsync();
        Assert.Equal(ApiKeyCheckStatus.Ok, keyCheck.Status);
        var key = keyCheck.Key!;

        var runId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        var created = new List<string>();
        try
        {
            // Upload a generated PNG with pixels unique to this run.
            var png = GeneratePng(64, 48);
            var sha1 = Convert.ToHexStringLower(SHA1.HashData(png));
            var fileName = $"VRChatPhotoSync-test_{runId}_a.png";
            var now = DateTimeOffset.Now;

            var first = await client.UploadAsync(png, fileName, sha1, now, now);
            Assert.True(first.Status == UploadStatus.Created, $"Upload: {first.Status} {first.HttpStatus} {first.Message}");
            created.Add(first.AssetId!);
            output.WriteLine($"Uploaded test asset {first.AssetId} ({fileName})");

            // Same bytes again → duplicate of the same asset.
            var second = await client.UploadAsync(png, fileName, sha1, now, now);
            Assert.True(second.Status == UploadStatus.Duplicate, $"Re-upload: {second.Status} {second.HttpStatus} {second.Message}");
            Assert.Equal(first.AssetId, second.AssetId);

            // Bulk check on the same bytes → reject/duplicate pointing at the asset.
            var check = await client.BulkUploadCheckAsync([new BulkCheckItem("test/" + fileName, sha1)]);
            Assert.True(check.Succeeded, $"Bulk check: {check.Status} {check.HttpStatus} {check.Message}");
            var item = Assert.Single(check.Value!);
            Assert.Equal("test/" + fileName, item.Id);
            Assert.False(item.Accepted);
            Assert.True(item.IsDuplicate, $"Bulk check reason: {item.Reason}");
            Assert.Equal(first.AssetId, item.AssetId);

            // Album: create, find again (with a fresh client so the cache doesn't answer), add.
            var albumName = $"VRChatPhotoSync integration test {runId}";
            string? albumId = null;
            if (key.MissingAlbumPermissions.Count == 0)
            {
                var album = await client.GetOrCreateAlbumAsync(albumName);
                Assert.True(album.Succeeded, $"Album create: {album.Status} {album.HttpStatus} {album.Message}");
                albumId = album.Value!;
                output.WriteLine($"Test album left on the server: '{albumName}' ({albumId})");

                using (var other = CreateClient())
                {
                    var found = await other.GetOrCreateAlbumAsync(albumName);
                    Assert.True(found.Succeeded, $"Album find: {found.Status} {found.HttpStatus} {found.Message}");
                    Assert.Equal(albumId, found.Value);
                }

                var add = await client.AddToAlbumAsync(albumId, [first.AssetId!]);
                Assert.True(add.Succeeded, $"Album add: {add.Status} {add.HttpStatus} {add.Message}");
                Assert.Equal([first.AssetId!], add.Value!.Added);
                Assert.True(add.Value.AllSucceeded);

                var again = await client.AddToAlbumAsync(albumId, [first.AssetId!]);
                Assert.True(again.Succeeded, $"Album re-add: {again.Status} {again.HttpStatus} {again.Message}");
                Assert.Equal([first.AssetId!], again.Value!.AlreadyInAlbum);
            }
            else
            {
                output.WriteLine($"Skipped album checks; key lacks: {string.Join(", ", key.MissingAlbumPermissions)}");
            }

            // Replace: upload a second PNG, copy old → new, trash old.
            if (key.CanReplace)
            {
                var png2 = GeneratePng(64, 48);
                var sha2 = Convert.ToHexStringLower(SHA1.HashData(png2));
                var replacement = await client.UploadAsync(png2, $"VRChatPhotoSync-test_{runId}_b.png", sha2, now, now);
                Assert.True(replacement.Status == UploadStatus.Created, $"Upload 2: {replacement.Status} {replacement.HttpStatus} {replacement.Message}");
                created.Add(replacement.AssetId!);
                output.WriteLine($"Uploaded test asset {replacement.AssetId} (replacement)");

                var copy = await client.CopyAssetAsync(first.AssetId!, replacement.AssetId!);
                Assert.True(copy.Succeeded, $"Copy: {copy.Status} {copy.HttpStatus} {copy.Message}");

                if (albumId is not null)
                {
                    // The copy moved the album membership, so adding again reports "already in album".
                    var inAlbum = await client.AddToAlbumAsync(albumId, [replacement.AssetId!]);
                    Assert.True(inAlbum.Succeeded, $"Album check: {inAlbum.Status} {inAlbum.HttpStatus} {inAlbum.Message}");
                    Assert.Equal([replacement.AssetId!], inAlbum.Value!.AlreadyInAlbum);
                }

                var trash = await client.TrashAssetsAsync([first.AssetId!]);
                Assert.True(trash.Succeeded, $"Trash: {trash.Status} {trash.HttpStatus} {trash.Message}");

                var trashedCheck = await client.BulkUploadCheckAsync([new BulkCheckItem("old", sha1)]);
                Assert.True(trashedCheck.Succeeded);
                var trashed = Assert.Single(trashedCheck.Value!);
                Assert.True(trashed.IsDuplicate && trashed.IsTrashed, $"Trashed asset: reason {trashed.Reason}, trashed {trashed.IsTrashed}");
            }
            else
            {
                output.WriteLine("Skipped copy/trash; key lacks asset.copy and/or asset.delete.");
            }
        }
        finally
        {
            if (created.Count > 0)
            {
                if (key.Has(ApiKeyInfo.PermissionAssetDelete))
                {
                    var cleanup = await client.TrashAssetsAsync(created);
                    output.WriteLine($"Trashed test assets {string.Join(", ", created)}: {cleanup.Status}");
                }
                else
                {
                    output.WriteLine($"Key lacks asset.delete; test assets left on the server: {string.Join(", ", created)}");
                }
            }
        }
    }

    /// <summary>A small PNG filled with random pixels, so every call gives new bytes (and a new checksum).</summary>
    private static byte[] GeneratePng(int width, int height)
    {
        var rgb = RandomNumberGenerator.GetBytes(width * height * 3);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var i = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++, i += 3)
                bitmap.SetPixel(x, y, Color.FromArgb(rgb[i], rgb[i + 1], rgb[i + 2]));
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
