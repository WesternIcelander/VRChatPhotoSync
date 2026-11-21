using System.Text.Json;

namespace VRChatPhotoSync.Tests;

public class AppConfigTests
{
    [Fact]
    public void Defaults_MatchPlan()
    {
        var config = new AppConfig();
        Assert.Null(config.ServerUrl);
        Assert.Null(config.ApiKeyProtected);
        Assert.Null(config.AlbumName);
        Assert.False(config.HasAlbum);
        Assert.Equal(AssetVisibility.Timeline, config.Visibility);
        Assert.True(config.ExcludePrints);
        Assert.False(config.UploadLayers);
        Assert.True(config.StrictFileNames);
        Assert.Equal(10, config.VrcxWaitSeconds);
        Assert.Equal(CloseButtonAction.Exit, config.CloseButtonAction);
        Assert.Equal(MinimizeButtonAction.Taskbar, config.MinimizeButtonAction);
        Assert.False(config.StartMinimized);
    }

    [Fact]
    public void HasNoPhotoFolderOrFileTypeSetting()
    {
        var names = typeof(AppConfig).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("Folder", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Extension", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("FileType", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyAlbum_MeansNoAlbum(string? album)
    {
        Assert.False(new AppConfig { AlbumName = album }.HasAlbum);
    }

    [Fact]
    public void Album_WhenSet_HasAlbum()
    {
        Assert.True(new AppConfig { AlbumName = "VRChat" }.HasAlbum);
    }

    [Fact]
    public void Json_RoundTrip_KeepsAllValues()
    {
        var config = new AppConfig
        {
            ServerUrl = "https://immich.example.com/api",
            ApiKeyProtected = "AQAAANCMnd8=",
            AlbumName = "VRChat",
            Visibility = AssetVisibility.Archive,
            ExcludePrints = false,
            UploadLayers = true,
            StrictFileNames = false,
            VrcxWaitSeconds = 0,
            CloseButtonAction = CloseButtonAction.HideToTray,
            MinimizeButtonAction = MinimizeButtonAction.Tray,
            StartMinimized = true,
        };
        var json = JsonSerializer.Serialize(config);
        Assert.Equal(config, JsonSerializer.Deserialize<AppConfig>(json));
        Assert.Contains("\"archive\"", json);
        Assert.Contains("\"HideToTray\"", json);
        Assert.Contains("\"Tray\"", json);
        Assert.DoesNotContain("HasAlbum", json);
    }

    [Fact]
    public void Json_MissingFields_UseDefaults()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("{}");
        Assert.Equal(new AppConfig(), config);
    }

    [Fact]
    public void SourceGen_RoundTrip_KeepsAllValues_AndPascalCaseNames()
    {
        var config = new AppConfig
        {
            ServerUrl = "https://immich.example.com/api",
            ApiKeyProtected = "AQAAANCMnd8=",
            AlbumName = "VRChat",
            Visibility = AssetVisibility.Hidden,
            ExcludePrints = false,
            UploadLayers = true,
            StrictFileNames = false,
            VrcxWaitSeconds = 3,
            CloseButtonAction = CloseButtonAction.HideToTray,
            MinimizeButtonAction = MinimizeButtonAction.Tray,
            StartMinimized = true,
        };
        var json = JsonSerializer.Serialize(config, ConfigJsonContext.Default.AppConfig);
        Assert.Equal(config, JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig));
        Assert.Contains("\"ServerUrl\"", json);
        Assert.Contains("\"hidden\"", json);
        Assert.Contains("\"HideToTray\"", json);
        Assert.DoesNotContain("HasAlbum", json);
        Assert.Contains("\n", json); // indented
    }

    [Fact]
    public void SourceGen_MissingFields_UseDefaults()
    {
        Assert.Equal(new AppConfig(), JsonSerializer.Deserialize("{}", ConfigJsonContext.Default.AppConfig));
        Assert.Equal(new AppConfig { AlbumName = "A" },
            JsonSerializer.Deserialize("{\"AlbumName\":\"A\"}", ConfigJsonContext.Default.AppConfig));
    }

    [Fact]
    public void SourceGen_AllowsCommentsAndTrailingCommas()
    {
        var config = JsonSerializer.Deserialize("{ // note\n \"VrcxWaitSeconds\": 0, }", ConfigJsonContext.Default.AppConfig);
        Assert.Equal(0, config!.VrcxWaitSeconds);
    }

    [Fact]
    public void ImmichContext_UsesCamelCase()
    {
        var dto = JsonSerializer.Deserialize("{\"id\":\"x\",\"status\":\"created\"}", ImmichJsonContext.Default.AssetMediaResponseDto);
        Assert.Equal("x", dto!.Id);
        Assert.Equal("created", dto.Status);
        Assert.Null(JsonSerializer.Deserialize("{\"Id\":\"x\"}", ImmichJsonContext.Default.AssetMediaResponseDto)!.Id);
    }

    [Theory]
    [InlineData(AssetVisibility.Timeline, "timeline")]
    [InlineData(AssetVisibility.Archive, "archive")]
    [InlineData(AssetVisibility.Hidden, "hidden")]
    [InlineData(AssetVisibility.Locked, "locked")]
    public void Visibility_ApiValue(AssetVisibility visibility, string expected)
    {
        Assert.Equal(expected, visibility.ToApiValue());
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(visibility));
    }
}
