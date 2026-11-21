namespace VRChatPhotoSync.Tests;

public sealed class CommandLineOptionsTests
{
    [Fact]
    public void NoArgs_GivesEmpty()
    {
        var options = CommandLineOptions.Parse([]);
        Assert.Null(options.Folder);
        Assert.Null(options.ServerUrl);
        Assert.False(options.Minimized);
        Assert.False(options.ForceSettings);
        Assert.Empty(options.Errors);
        Assert.False(options.HasConfigOverrides);

        Assert.Same(CommandLineOptions.Empty, CommandLineOptions.Parse(null));
    }

    [Fact]
    public void AllOptions_AreParsed()
    {
        var options = CommandLineOptions.Parse(
            ["--folder", @"D:\Photos\VRChat", "--server", "https://immich.example.com/", "--minimized", "--settings"]);

        Assert.Equal(@"D:\Photos\VRChat", options.Folder);
        Assert.Equal("https://immich.example.com/api", options.ServerUrl);
        Assert.True(options.Minimized);
        Assert.True(options.ForceSettings);
        Assert.Empty(options.Errors);
    }

    [Fact]
    public void EqualsSyntax_AndAnyCase_Work()
    {
        var options = CommandLineOptions.Parse(
            [@"--FOLDER=D:\Scratch\Photo Test", "--Server=http://localhost:2283/api/", "--Minimized"]);

        Assert.Equal(@"D:\Scratch\Photo Test", options.Folder);
        Assert.Equal("http://localhost:2283/api", options.ServerUrl);
        Assert.True(options.Minimized);
        Assert.False(options.ForceSettings);
        Assert.Empty(options.Errors);
    }

    [Fact]
    public void QuotedValue_IsUnquoted()
    {
        var options = CommandLineOptions.Parse(["--folder=\"D:\\Photos\\VRChat\""]);
        Assert.Equal(@"D:\Photos\VRChat", options.Folder);
    }

    [Fact]
    public void LastValue_Wins()
    {
        var options = CommandLineOptions.Parse(["--folder", @"D:\A", "--folder", @"D:\B"]);
        Assert.Equal(@"D:\B", options.Folder);
    }

    [Theory]
    [InlineData("--folder")]
    [InlineData("--server")]
    [InlineData("--folder=")]
    [InlineData("--server=  ")]
    public void MissingValue_IsAnError(string arg)
    {
        var options = CommandLineOptions.Parse([arg]);
        Assert.Null(options.Folder);
        Assert.Null(options.ServerUrl);
        Assert.Single(options.Errors);
        Assert.Contains("needs a value", options.Errors[0]);
    }

    [Fact]
    public void ValueThatIsAnotherOption_IsNotConsumed()
    {
        var options = CommandLineOptions.Parse(["--folder", "--minimized"]);
        Assert.Null(options.Folder);
        Assert.True(options.Minimized);
        Assert.Single(options.Errors);
    }

    [Theory]
    [InlineData("immich.example.com")]
    [InlineData("ftp://immich.example.com")]
    [InlineData("not a url")]
    public void InvalidServer_IsIgnoredWithError(string url)
    {
        var options = CommandLineOptions.Parse(["--server", url]);
        Assert.Null(options.ServerUrl);
        Assert.Single(options.Errors);
        Assert.Contains("--server", options.Errors[0]);
    }

    [Fact]
    public void UnknownOption_IsAnError_OthersStillParsed()
    {
        var options = CommandLineOptions.Parse(["--bogus", "--minimized", "stray"]);
        Assert.True(options.Minimized);
        Assert.Equal(2, options.Errors.Count);
        Assert.Contains("--bogus", options.Errors[0]);
        Assert.Contains("stray", options.Errors[1]);
    }

    [Fact]
    public void FlagWithValue_IsAnError()
    {
        var options = CommandLineOptions.Parse(["--minimized=false"]);
        Assert.False(options.Minimized);
        Assert.Single(options.Errors);
    }

    [Fact]
    public void ApplyTo_OverridesServerAndMinimized_LeavesOriginalUnchanged()
    {
        var saved = new AppConfig { ServerUrl = "https://saved.example.com/api", AlbumName = "VRChat", StartMinimized = false };
        var options = CommandLineOptions.Parse(["--server", "https://immich.example.com", "--minimized"]);

        var run = options.ApplyTo(saved);

        Assert.True(options.HasConfigOverrides);
        Assert.Equal("https://immich.example.com/api", run.ServerUrl);
        Assert.True(run.StartMinimized);
        Assert.Equal("VRChat", run.AlbumName);
        Assert.Equal("https://saved.example.com/api", saved.ServerUrl);
        Assert.False(saved.StartMinimized);
    }

    [Fact]
    public void ApplyTo_WithoutOverrides_KeepsConfig()
    {
        var saved = new AppConfig { ServerUrl = "https://saved.example.com/api", StartMinimized = true };
        var run = CommandLineOptions.Parse(["--settings", "--folder", @"D:\Photos\VRChat"]).ApplyTo(saved);
        Assert.Equal(saved, run);
    }

    [Fact]
    public void Overrides_AreNotWrittenToConfigFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "PhotoSyncTests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigStore(Path.Combine(root, "exe"), Path.Combine(root, "appdata"));
            Directory.CreateDirectory(Path.Combine(root, "exe"));
            store.Save(new AppConfig { ServerUrl = "https://saved.example.com" });

            var options = CommandLineOptions.Parse(["--server", "https://immich.example.com", "--minimized"]);
            var saved = store.Load().Config;
            var run = options.ApplyTo(saved);
            Assert.Equal("https://immich.example.com/api", run.ServerUrl);

            // A settings change is saved on top of the file's config, not the run config.
            store.Save(saved with { AlbumName = "VRChat" });
            var reloaded = store.Load().Config;
            Assert.Equal("https://saved.example.com/api", reloaded.ServerUrl);
            Assert.False(reloaded.StartMinimized);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Folder_FeedsVRChatSettingsOverride()
    {
        var root = Path.Combine(Path.GetTempPath(), "PhotoSyncTests", Guid.NewGuid().ToString("N"));
        var scratch = Path.Combine(root, "scratch");
        var options = CommandLineOptions.Parse(["--folder", scratch]);

        var settings = VRChatSettings.Load(
            options.Folder,
            configPath: Path.Combine(root, "missing", "config.json"),
            getPicturesFolder: () => Path.Combine(root, "Pictures"));

        Assert.Equal(VRChatSettings.SourceCommandLine, settings.Source);
        Assert.Equal(scratch, settings.PictureFolder);
    }
}
