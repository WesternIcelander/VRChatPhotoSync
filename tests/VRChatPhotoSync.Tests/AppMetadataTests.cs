using System.Diagnostics;
using System.Reflection;

namespace VRChatPhotoSync.Tests;

public class AppMetadataTests
{
    private static readonly Assembly App = typeof(AppIcon).Assembly;

    [Fact]
    public void AssemblyName_IsVRChatPhotoSync()
    {
        Assert.Equal("VRChatPhotoSync", App.GetName().Name);
    }

    [Fact]
    public void Product_IsSet()
    {
        Assert.Equal("VRChat PhotoSync", App.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
    }

    [Fact]
    public void Version_IsSet()
    {
        Assert.Equal(new Version(1, 0, 0, 0), App.GetName().Version);
        var info = App.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.NotNull(info);
        Assert.StartsWith("1.0.0", info);
    }

    [Fact]
    public void EmbeddedIcon_LoadsWithSmallAndLargeSizes()
    {
        var icon = AppIcon.Icon;
        Assert.True(icon.Width > 0);
        using var small = new Icon(icon, 16, 16);
        Assert.Equal(16, small.Width);
        using var medium = new Icon(icon, 32, 32);
        Assert.Equal(32, medium.Width);
    }

    [Fact]
    public void EmbeddedIcon_ContainsTrayAndExplorerSizes()
    {
        // Read the ICO directory: 6-byte header, then 16-byte entries (width byte 0 = 256).
        using var stream = App.GetManifestResourceStream(AppIcon.ResourceName);
        Assert.NotNull(stream);
        using var reader = new BinaryReader(stream);
        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        int count = reader.ReadUInt16();
        var sizes = new HashSet<int>();
        for (int i = 0; i < count; i++)
        {
            int width = reader.ReadByte();
            reader.ReadBytes(15);
            sizes.Add(width == 0 ? 256 : width);
        }
        Assert.Superset(new HashSet<int> { 16, 24, 32, 48, 256 }, sizes);
    }

    [Fact]
    public void Executable_HasWin32ProductAndVersion()
    {
        var exe = Path.ChangeExtension(App.Location, ".exe");
        Assert.True(File.Exists(exe), $"Missing {exe}");
        var info = FileVersionInfo.GetVersionInfo(exe);
        Assert.Equal("VRChat PhotoSync", info.ProductName);
        Assert.Equal("1.0.0.0", info.FileVersion);
        using var icon = Icon.ExtractAssociatedIcon(exe);
        Assert.NotNull(icon);
    }
}
