namespace VRChatPhotoSync.Tests;

public sealed class VRChatSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoSyncTests", Guid.NewGuid().ToString("N"));
    private readonly string _configPath;
    private readonly string _fakePictures;
    private readonly List<string> _log = new();

    public VRChatSettingsTests()
    {
        Directory.CreateDirectory(_root);
        _configPath = Path.Combine(_root, "LocalLow", "VRChat", "VRChat", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
        // A "moved" Pictures folder, deliberately not under %USERPROFILE%.
        _fakePictures = Path.Combine(_root, "MovedPictures");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private VRChatSettings Load(string? folderOverride = null, Func<string?>? pictures = null) =>
        VRChatSettings.Load(folderOverride, _configPath, pictures ?? (() => _fakePictures), _log.Add);

    private void WriteConfig(string json) => File.WriteAllText(_configPath, json);

    private string DefaultFolder => Path.Combine(_fakePictures, "VRChat");

    [Fact]
    public void FullFile_GivesFolderAndSplit()
    {
        WriteConfig("{\"picture_output_folder\": \"D:\\\\Photos\\\\VRChat\", \"picture_output_split_by_date\": true, \"other\": 5}");
        var s = Load();
        Assert.Equal(@"D:\Photos\VRChat", s.PictureFolder);
        Assert.True(s.SplitByDate);
        Assert.Equal(VRChatSettings.SourceVRChatConfig, s.Source);
        Assert.Equal(_configPath, s.ConfigPath);
        Assert.Empty(s.Warnings);
        Assert.Empty(_log);
    }

    [Fact]
    public void SplitByDateFalse_IsRead()
    {
        WriteConfig("{\"picture_output_folder\": \"D:\\\\Photos\\\\VRChat\", \"picture_output_split_by_date\": false}");
        var s = Load();
        Assert.False(s.SplitByDate);
        Assert.Equal(@"D:\Photos\VRChat", s.PictureFolder);
    }

    [Fact]
    public void MissingKey_UsesMovedPicturesFolder()
    {
        WriteConfig("{\"some_other_setting\": 1}");
        var s = Load();
        Assert.Equal(DefaultFolder, s.PictureFolder);
        Assert.True(s.SplitByDate);
        Assert.Equal(VRChatSettings.SourceDefault, s.Source);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(s.PictureFolder.StartsWith(Path.Combine(profile, "Pictures"), StringComparison.OrdinalIgnoreCase));
        Assert.Empty(s.Warnings);
    }

    [Theory]
    [InlineData("{\"picture_output_folder\": \"\"}")]
    [InlineData("{\"picture_output_folder\": \"   \"}")]
    [InlineData("{\"picture_output_folder\": null}")]
    public void EmptyKey_UsesDefault(string json)
    {
        WriteConfig(json);
        var s = Load();
        Assert.Equal(DefaultFolder, s.PictureFolder);
        Assert.Equal(VRChatSettings.SourceDefault, s.Source);
    }

    [Fact]
    public void EmptyPicturesLookup_FallsBackToUserProfilePictures()
    {
        var s = Load(pictures: () => "");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(profile, "Pictures", "VRChat"), s.PictureFolder);
        Assert.Equal(VRChatSettings.SourceDefault, s.Source);
    }

    [Fact]
    public void ThrowingPicturesLookup_DoesNotThrow()
    {
        var s = Load(pictures: () => throw new InvalidOperationException("boom"));
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(profile, "Pictures", "VRChat"), s.PictureFolder);
        Assert.NotEmpty(s.Warnings);
    }

    [Fact]
    public void MissingFile_GivesDefaultsWithoutWarning()
    {
        Assert.False(File.Exists(_configPath));
        var s = Load();
        Assert.Equal(DefaultFolder, s.PictureFolder);
        Assert.True(s.SplitByDate);
        Assert.Equal(VRChatSettings.SourceDefault, s.Source);
        Assert.Empty(s.Warnings);
    }

    [Fact]
    public void MissingConfigDirectory_GivesDefaults()
    {
        var s = VRChatSettings.Load(null, Path.Combine(_root, "nope", "config.json"), () => _fakePictures, _log.Add);
        Assert.Equal(DefaultFolder, s.PictureFolder);
        Assert.Equal(VRChatSettings.SourceDefault, s.Source);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("\"just a string\"")]
    [InlineData("{\"picture_output_folder\": 42, \"picture_output_split_by_date\": \"maybe\"}")]
    [InlineData("{\"picture_output_folder\": {\"a\": 1}, \"picture_output_split_by_date\": [true]}")]
    public void InvalidContent_GivesDefaultsAndWarns(string json)
    {
        WriteConfig(json);
        var s = Load();
        Assert.Equal(DefaultFolder, s.PictureFolder);
        Assert.True(s.SplitByDate);
        Assert.Equal(VRChatSettings.SourceDefault, s.Source);
        Assert.NotEmpty(s.Warnings);
        Assert.Equal(s.Warnings, _log);
    }

    [Fact]
    public void TrailingCommasAndComments_AreAccepted()
    {
        WriteConfig("""
            {
              // VRChat doesn't write comments, but be lenient anyway
              "picture_output_folder": "D:\\Photos\\VRChat", /* block */
              "picture_output_split_by_date": false,
            }
            """);
        var s = Load();
        Assert.Equal(@"D:\Photos\VRChat", s.PictureFolder);
        Assert.False(s.SplitByDate);
        Assert.Equal(VRChatSettings.SourceVRChatConfig, s.Source);
        Assert.Empty(s.Warnings);
    }

    [Fact]
    public void InvalidJsonWithFolder_StillDoesNotThrow()
    {
        WriteConfig("{\"picture_output_folder\": \"D:\\\\Photos\\\\VRChat\", \"picture_output_split_by_date\": fal");
        var s = Load();
        Assert.Equal(DefaultFolder, s.PictureFolder);
        Assert.NotEmpty(s.Warnings);
    }

    [Fact]
    public void EnvironmentVariables_AreExpanded()
    {
        var name = "PHOTOSYNC_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, @"E:\Somewhere");
        try
        {
            WriteConfig("{\"picture_output_folder\": \"%" + name + "%\\\\VRChat\"}");
            var s = Load();
            Assert.Equal(@"E:\Somewhere\VRChat", s.PictureFolder);
            Assert.Equal(VRChatSettings.SourceVRChatConfig, s.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Path_IsMadeFull()
    {
        WriteConfig("{\"picture_output_folder\": \"D:/Photos/./VRChat/\"}");
        var s = Load();
        Assert.Equal(@"D:\Photos\VRChat", s.PictureFolder);
    }

    [Fact]
    public void InvalidPath_FallsBackToDefault()
    {
        WriteConfig("{\"picture_output_folder\": \"D:\\\\bad\\u0000path\"}");
        var s = Load();
        Assert.Equal(DefaultFolder, s.PictureFolder);
        Assert.Equal(VRChatSettings.SourceDefault, s.Source);
        Assert.NotEmpty(s.Warnings);
    }

    [Fact]
    public void FolderOverride_WinsButSplitStillRead()
    {
        WriteConfig("{\"picture_output_folder\": \"D:\\\\Photos\\\\VRChat\", \"picture_output_split_by_date\": false}");
        var scratch = Path.Combine(_root, "scratch");
        var s = Load(folderOverride: scratch);
        Assert.Equal(scratch, s.PictureFolder);
        Assert.Equal(VRChatSettings.SourceCommandLine, s.Source);
        Assert.False(s.SplitByDate);
    }

    [Fact]
    public void FolderOverride_WithMissingConfig()
    {
        var s = Load(folderOverride: @"C:\Temp\Scratch");
        Assert.Equal(@"C:\Temp\Scratch", s.PictureFolder);
        Assert.Equal(VRChatSettings.SourceCommandLine, s.Source);
        Assert.True(s.SplitByDate);
    }

    [Fact]
    public void ConfigLockedByWriter_CanStillBeRead()
    {
        WriteConfig("{\"picture_output_folder\": \"D:\\\\Photos\\\\VRChat\"}");
        using var writer = new FileStream(_configPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var s = Load();
        Assert.Equal(@"D:\Photos\VRChat", s.PictureFolder);
    }

    [Fact]
    public void Load_DoesNotModifyConfig()
    {
        const string json = "{\"picture_output_folder\": \"D:\\\\Photos\\\\VRChat\",}";
        WriteConfig(json);
        var before = File.GetLastWriteTimeUtc(_configPath);
        Load();
        Assert.Equal(json, File.ReadAllText(_configPath));
        Assert.Equal(before, File.GetLastWriteTimeUtc(_configPath));
    }

    [Fact]
    public void ThrowingLogger_DoesNotBreakLoading()
    {
        WriteConfig("{ not json");
        var s = VRChatSettings.Load(null, _configPath, () => _fakePictures, _ => throw new Exception("log broke"));
        Assert.Equal(DefaultFolder, s.PictureFolder);
    }

    [Fact]
    public void DefaultConfigPath_IsUnderLocalLow()
    {
        var path = VRChatSettings.FindDefaultConfigPath();
        Assert.NotNull(path);
        Assert.EndsWith(@"\LocalLow\VRChat\VRChat\config.json", path, StringComparison.OrdinalIgnoreCase);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.Equal(Path.GetFullPath(Path.Combine(localAppData, "..", "LocalLow")),
            VRChatSettings.GetLocalLowFolder(), ignoreCase: true);
    }

    [Fact]
    public void Describe_MentionsSource()
    {
        var s = new VRChatSettings(@"D:\Photos\VRChat", true, VRChatSettings.SourceVRChatConfig, null, []);
        Assert.Equal(@"D:\Photos\VRChat (from VRChat config, split by month)", s.Describe());
    }
}
