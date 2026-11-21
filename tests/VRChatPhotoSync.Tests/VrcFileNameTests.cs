namespace VRChatPhotoSync.Tests;

public sealed class VrcFileNameTests
{
    private const string Photo = "VRChat_2024-11-23_19-17-27.652_1920x1080.png";

    [Theory]
    [InlineData(Photo)]
    [InlineData(@"D:\Photos\VRChat\2024-11\" + Photo)]
    [InlineData("vrchat_2024-11-23_19-17-27.652_1920x1080.PNG")]
    [InlineData("VRChat_2024-11-23_19-17-27.652_1920x1080_Environment.png")]
    [InlineData("VRChat_2024-11-23_19-17-27.652_1920x1080_wrld_12345678-90ab-4cde-8f01-234567890abc.png")]
    [InlineData("VRChat_2024-11-23_19-17-27.652_1920x1080 - Copy.png")]
    public void TryParseTimestamp_ReadsLocalTimeToTheMillisecond(string name)
    {
        Assert.True(VrcFileName.TryParseTimestamp(name, out var t));
        Assert.Equal(new DateTime(2024, 11, 23, 19, 17, 27, 652), t);
        Assert.Equal(DateTimeKind.Local, t.Kind);
    }

    [Fact]
    public void TryParseTimestamp_WithoutMilliseconds()
    {
        Assert.True(VrcFileName.TryParseTimestamp("VRChat_2024-11-23_19-17-27_1920x1080.png", out var t));
        Assert.Equal(new DateTime(2024, 11, 23, 19, 17, 27), t);
    }

    [Theory]
    [InlineData("PrtScn Screenshots.lnk")]
    [InlineData("someone_2024-12-24_01-01-38.031_prnt_0a1b.png")]
    [InlineData("VRChat_2024-13-23_19-17-27.652_1920x1080.png")]   // month 13
    [InlineData("VRChat_2024-11-23_25-17-27.652_1920x1080.png")]   // hour 25
    [InlineData("VRChat_2024-11-23_19-17-27.65_1920x1080.png")]    // two-digit ms
    [InlineData("VRChat_garbage.png")]
    [InlineData("")]
    public void TryParseTimestamp_RejectsOtherNames(string name)
    {
        Assert.False(VrcFileName.TryParseTimestamp(name, out _));
    }

    [Fact]
    public void GetCaptureTime_UsesNameWithLocalOffset()
    {
        var t = VrcFileName.GetCaptureTime(Photo, _ => throw new InvalidOperationException("file times must not be read"));
        var expectedLocal = new DateTime(2024, 11, 23, 19, 17, 27, 652, DateTimeKind.Local);
        Assert.Equal(expectedLocal, t.DateTime);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(expectedLocal), t.Offset);
    }

    [Fact]
    public void GetCaptureTime_FallsBackToEarlierFileTime()
    {
        var created = new DateTime(2025, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        var modified = new DateTime(2025, 2, 1, 10, 0, 0, DateTimeKind.Utc);
        var t = VrcFileName.GetCaptureTime("copied photo.png", _ => (created, modified));
        Assert.Equal(modified, t.UtcDateTime);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(modified), t.Offset);
    }

    [Fact]
    public void GetCaptureTime_ReadsRealFileTimes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"photosync-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, [1]);
        try
        {
            var when = new DateTime(2023, 6, 7, 8, 9, 10, DateTimeKind.Utc);
            File.SetCreationTimeUtc(path, when);
            File.SetLastWriteTimeUtc(path, when.AddDays(1));
            Assert.Equal(when, VrcFileName.GetCaptureTime(path).UtcDateTime);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(Photo, 1920, 1080)]
    [InlineData("VRChat_2026-11-21_21-03-08.102_3840x2160_Environment.png", 3840, 2160)]
    [InlineData("VRChat_2026-11-21_21-39-01.718_3840x2160_.png", 3840, 2160)]
    [InlineData("someone_2024-12-24_01-01-38.031_2048x1440.png", 2048, 1440)]
    [InlineData("VRChat_2025-08-01_01-54-22.987_7680x4320.PNG", 7680, 4320)]
    [InlineData("VRChat_2025-12-25_21-08-42.649_3840x2160 - Copy.png", 3840, 2160)]
    public void TryParseSize_ReadsWidthAndHeight(string name, int w, int h)
    {
        Assert.True(VrcFileName.TryParseSize(name, out var width, out var height));
        Assert.Equal(w, width);
        Assert.Equal(h, height);
    }

    [Theory]
    [InlineData("VRChat_2024-11-23_19-17-27.652.png")]
    [InlineData("VRChat_2024-11-23_19-17-27.652_0x1080.png")]
    [InlineData("VRChat_2024-11-23_19-17-27.652_1920x.png")]
    [InlineData("photo_1920x1080b.png")]
    [InlineData("PrtScn Screenshots.lnk")]
    public void TryParseSize_RejectsNamesWithoutSize(string name)
    {
        Assert.False(VrcFileName.TryParseSize(name, out _, out _));
    }

    [Theory]
    [InlineData("VRChat_2026-11-21_21-03-08.102_3840x2160_Environment.png", true)]
    [InlineData("VRChat_2026-11-21_21-03-08.102_3840x2160_Player.png", true)]
    [InlineData("VRChat_2026-11-21_21-03-08.102_3840x2160_UI.png", true)]
    [InlineData("vrchat_2026-11-21_21-03-08.102_3840x2160_environment.PNG", true)]
    [InlineData("VRChat_2026-11-21_21-03-08.102_3840x2160_ui.png", true)]
    [InlineData(@"D:\Photos\VRChat\2026-11\VRChat_2026-11-21_21-03-08.102_3840x2160_Player.png", true)]
    [InlineData("VRChat_2026-11-21_21-03-08.102_3840x2160.png", false)]
    [InlineData("VRChat_2026-11-21_21-03-08.102_3840x2160_UI.jpg", false)]
    [InlineData("VRChat_2026-11-21_21-03-08.102_3840x2160_Players.png", false)]
    [InlineData("VRChat_2026-11-21_21-03-08.102_3840x2160_GUI.png", false)]
    [InlineData("_UI.png", false)]
    public void IsLayer(string name, bool expected)
    {
        Assert.Equal(expected, VrcFileName.IsLayer(name));
    }

    [Theory]
    [InlineData("VRChat_2026-11-21_21-03-08.102_3840x2160.png")]
    [InlineData("VRChat_2026-11-21_21-03-08.102_3840x2160_Environment.png")]
    [InlineData("VRChat_2026-11-21_21-03-08.102_3840x2160_Player.png")]
    [InlineData("VRChat_2026-11-21_21-03-08.102_3840x2160_UI.png")]
    [InlineData("VRChat_2026-11-21_21-03-08.102_3840x2160_ui.PNG")]
    [InlineData(@"D:\Photos\VRChat\2026-11\VRChat_2026-11-21_21-03-08.102_3840x2160_UI.png")]
    public void BaseName_IsSharedByPhotoAndLayers(string name)
    {
        Assert.Equal("VRChat_2026-11-21_21-03-08.102_3840x2160", VrcFileName.BaseName(name));
    }

    [Fact]
    public void LayerSiblingPaths_AndCombinedPath()
    {
        var dir = @"D:\Photos\VRChat\2026-11";
        var combined = Path.Combine(dir, "VRChat_2026-11-21_21-03-08.102_3840x2160.png");
        var siblings = VrcFileName.LayerSiblingPaths(combined).ToList();
        Assert.Equal(
            [
                Path.Combine(dir, "VRChat_2026-11-21_21-03-08.102_3840x2160_Environment.png"),
                Path.Combine(dir, "VRChat_2026-11-21_21-03-08.102_3840x2160_Player.png"),
                Path.Combine(dir, "VRChat_2026-11-21_21-03-08.102_3840x2160_UI.png"),
            ],
            siblings);

        Assert.Equal(combined, VrcFileName.CombinedPath(siblings[1]));
        Assert.Equal(combined, VrcFileName.CombinedPath(combined));
        Assert.Equal(siblings, VrcFileName.LayerSiblingPaths(siblings[2]).ToList());
    }
}
