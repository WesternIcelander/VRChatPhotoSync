namespace VRChatPhotoSync.Tests;

/// <summary>Checks the shipped <c>config.example.json</c> at the repo root.</summary>
public sealed class ConfigExampleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoSyncTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string ExamplePath => Path.Combine(AppContext.BaseDirectory, "config.example.json");

    [Fact]
    public void Example_LoadsWithoutError_AndMatchesDefaults()
    {
        var exeDir = Path.Combine(_root, "exe");
        Directory.CreateDirectory(exeDir);
        File.Copy(ExamplePath, Path.Combine(exeDir, ConfigStore.FileName));

        var result = new ConfigStore(exeDir, Path.Combine(_root, "appdata")).Load();

        Assert.True(result.Exists);
        Assert.Null(result.Error);
        Assert.True(result.HasValidServerUrl);
        Assert.Equal("https://immich.example.com/api", result.Config.ServerUrl);
        Assert.Null(result.Config.ApiKeyProtected);
        Assert.Equal("VRChat", result.Config.AlbumName);

        // Apart from the placeholders, every value is the app's default.
        var expected = new AppConfig { ServerUrl = result.Config.ServerUrl, AlbumName = "VRChat" };
        Assert.Equal(expected, result.Config);
    }

    [Fact]
    public void Example_HasNoRealPathsUrlsOrKeys()
    {
        var json = File.ReadAllText(ExamplePath);
        Assert.DoesNotContain(":\\\\", json);           // no Windows paths
        Assert.DoesNotContain("Users", json);
        Assert.Matches(@"^(?:(?!https?://)[\s\S])*https://immich\.example\.com/api(?:(?!https?://)[\s\S])*$", json); // only the example URL
        Assert.Contains("\"ApiKeyProtected\": null", json);
    }
}
