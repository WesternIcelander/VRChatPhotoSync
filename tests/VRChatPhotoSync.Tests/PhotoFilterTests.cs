namespace VRChatPhotoSync.Tests;

public sealed class PhotoFilterTests : IDisposable
{
    private const string Root = @"D:\Photos\VRChat";
    private const string Photo = "VRChat_2024-11-23_19-17-27.652_1920x1080.png";
    private const string WorldId = "wrld_12345678-90ab-4cde-8f01-234567890abc";

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "PhotoSyncTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_temp))
            {
                foreach (var f in Directory.EnumerateFiles(_temp, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(_temp, recursive: true);
            }
        }
        catch (IOException) { }
    }

    private static string InRoot(string relative) => Path.Combine(Root, relative);

    // ---- names, strict on (the default) ----

    [Theory]
    [InlineData(Photo)]
    [InlineData("vrchat_2024-11-23_19-17-27.652_1920x1080.PNG")]
    [InlineData("VRChat_2026-11-21_21-03-08.102_3840x2160_Environment.png")]
    [InlineData("VRChat_2026-11-21_21-03-08.102_3840x2160_Player.png")]
    [InlineData("VRChat_2026-11-21_21-03-08.102_3840x2160_UI.png")]
    [InlineData("VRChat_2026-11-21_21-03-08.102_3840x2160_ui.png")]
    [InlineData("VRChat_2026-11-21_21-39-01.718_3840x2160_.png")]                 // VRCX rename with an empty world id
    [InlineData("VRChat_2025-04-20_03-28-00.594_3840x2160_.png")]                 // the hand-edited trailing-_ copy (accepted by design)
    [InlineData("VRChat_2026-11-21_21-41-26.711_3840x2160_" + WorldId + ".png")]  // VRCX rename with a world id
    [InlineData("VRChat_2026-11-21_21-41-26.711_3840x2160_WRLD_12345678-90AB-4CDE-8F01-234567890ABC.png")]
    [InlineData("VRChat_2025-08-01_01-54-22.987_7680x4320.png")]
    public void Strict_AcceptsVRChatNames(string name)
    {
        Assert.True(PhotoFilter.Accepts(Root, InRoot(name), strictNames: true));
        Assert.True(PhotoFilter.Accepts(Root, InRoot(name), strictNames: false));
    }

    [Theory]
    [InlineData("VRChat_2025-12-25_21-08-42.649_3840x2160 - Copy.png")]
    [InlineData("VRChat_2025-12-25_21-08-42.649_3840x2160 (1).png")]
    [InlineData("VRChat_2025-12-25_21-08-42.649_3840x2160_edited.png")]
    [InlineData("VRChat_2025-12-25_21-08-42_3840x2160.png")]                                  // missing milliseconds
    [InlineData("VRChat_2026-11-21_21-03-08.102_3840x2160_Environment_" + WorldId + ".png")] // layer + world
    [InlineData("VRChat_2026-11-21_21-03-08.102_3840x2160_wrld_1234.png")]                   // short world id
    [InlineData("VRChat_2026-11-21_21-03-08.102.png")]                                        // no size
    [InlineData("VRChat_2026-11-21_21-03-08.102_3840x2160__.png")]
    public void Strict_RejectsHandMadeCopies_ButLooseAcceptsThem(string name)
    {
        Assert.False(PhotoFilter.Accepts(Root, InRoot(name), strictNames: true));
        Assert.True(PhotoFilter.Accepts(Root, InRoot(name), strictNames: false));
    }

    [Theory]
    [InlineData("VRChat_2025-08-01_01-54-22.987_3840x2160.jpg")]
    [InlineData("VRChat_2025-08-01_01-54-22.987_3840x2160.png.jpg")]
    [InlineData("PrtScn Screenshots.lnk")]
    [InlineData("someone_2024-12-24_01-01-38.031_prnt_0a1b2c3d.png")]
    [InlineData("Screenshot_2024-11-23.png")]
    [InlineData("VRChat_.png")]
    [InlineData("VRChat.png")]
    [InlineData("desktop.ini")]
    [InlineData("VRChat_2024-11-23_19-17-27.652_1920x1080.png.tmp")]
    public void RejectsNonVRChatPng_EvenWhenLoose(string name)
    {
        Assert.False(PhotoFilter.Accepts(Root, InRoot(name), strictNames: true));
        Assert.False(PhotoFilter.Accepts(Root, InRoot(name), strictNames: false));
    }

    // ---- folders ----

    [Theory]
    [InlineData(Photo)]
    [InlineData(@"2024-11\" + Photo)]
    [InlineData(@"2026-11\VRChat_2026-11-21_21-03-08.102_3840x2160_UI.png")]
    [InlineData(@"Some\Deeper\Folder\" + Photo)]
    [InlineData(@"MyPrints\" + Photo)]
    [InlineData(@"2024-12\Prints\" + Photo)] // only the first segment under the root counts
    public void AcceptsRootAndSubfolders(string relative)
    {
        Assert.True(PhotoFilter.Accepts(Root, InRoot(relative), strictNames: true));
    }

    [Theory]
    [InlineData(@"Prints\2024-12\" + Photo)]
    [InlineData(@"Stickers\2024-12\" + Photo)]
    [InlineData(@"Emoji\2024-12\" + Photo)]
    [InlineData(@"prints\" + Photo)]
    [InlineData(@"STICKERS\" + Photo)]
    [InlineData(@"emoji\x\y\" + Photo)]
    public void RejectsExcludedFolders(string relative)
    {
        Assert.False(PhotoFilter.Accepts(Root, InRoot(relative), strictNames: true));
        Assert.False(PhotoFilter.Accepts(Root, InRoot(relative), strictNames: false));
    }

    [Theory]
    [InlineData(@"D:\Photos\Other\" + Photo)]
    [InlineData(@"D:\Photos\VRChatOld\" + Photo)]
    [InlineData(@"C:\" + Photo)]
    [InlineData(@"D:\Photos\VRChat\..\" + Photo)]
    public void RejectsFilesOutsideTheRoot(string path)
    {
        Assert.False(PhotoFilter.Accepts(Root, path, strictNames: false));
    }

    [Fact]
    public void RootWithTrailingSeparatorAndDifferentCase()
    {
        Assert.True(PhotoFilter.Accepts(@"d:\photos\vrchat\", InRoot(@"2024-11\" + Photo), strictNames: true));
        Assert.False(PhotoFilter.Accepts(@"d:\photos\vrchat\", InRoot(@"prints\" + Photo), strictNames: true));
    }

    [Theory]
    [InlineData("", Photo)]
    [InlineData(Root, "")]
    public void EmptyArguments_AreRejected(string root, string path)
    {
        Assert.False(PhotoFilter.Accepts(root, path, strictNames: true));
    }

    [Fact]
    public void ExcludedFolders_AreTheThreeVRChatFolders()
    {
        Assert.Equal(["Prints", "Stickers", "Emoji"], PhotoFilter.ExcludedFolders);
    }

    // ---- attributes (real files) ----

    private string Create(string relative, FileAttributes extra = 0)
    {
        var path = Path.Combine(_temp, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
        if (extra != 0)
            File.SetAttributes(path, File.GetAttributes(path) | extra);
        return path;
    }

    [Fact]
    public void RejectsHiddenAndTempFiles()
    {
        var normal = Create(Photo);
        var hidden = Create(@"2024-11\" + Photo, FileAttributes.Hidden);
        var temp = Create(@"2024-12\" + Photo, FileAttributes.Temporary);

        Assert.True(PhotoFilter.Accepts(_temp, normal, strictNames: true));
        Assert.False(PhotoFilter.Accepts(_temp, hidden, strictNames: true));
        Assert.False(PhotoFilter.Accepts(_temp, temp, strictNames: true));
    }

    // ---- enumeration ----

    [Fact]
    public void EnumerateCandidates_WalksRootAndMonths_SkipsExcluded()
    {
        var rootPhoto = Create(Photo);
        var monthPhoto = Create(@"2024-11\VRChat_2024-11-24_10-00-00.000_1920x1080.png");
        var layer = Create(@"2026-11\VRChat_2026-11-21_21-03-08.102_3840x2160_Player.png");
        var deep = Create(@"2026-11\nested\VRChat_2026-11-22_10-00-00.000_1920x1080.png");
        var copy = Create(@"2025-12\VRChat_2025-12-25_21-08-42.649_3840x2160 - Copy.png");
        Create(@"2025-08\VRChat_2025-08-01_01-54-22.987_3840x2160.jpg");
        Create("PrtScn Screenshots.lnk");
        Create(@"Prints\2024-12\VRChat_2024-12-24_01-01-38.031_2048x1440.png");
        Create(@"stickers\2024-12\VRChat_2024-12-24_01-01-38.031_1024x1024.png");
        Create(@"Emoji\VRChat_2024-12-24_01-01-38.031_1024x1024.png");
        Create(@"2024-10\VRChat_2024-10-01_10-00-00.000_1920x1080.png", FileAttributes.Hidden);

        var strict = PhotoFilter.EnumerateCandidates(_temp, strictNames: true).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(new HashSet<string>([rootPhoto, monthPhoto, layer, deep], StringComparer.OrdinalIgnoreCase), strict);

        var loose = PhotoFilter.EnumerateCandidates(_temp, strictNames: false).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(new HashSet<string>([rootPhoto, monthPhoto, layer, deep, copy], StringComparer.OrdinalIgnoreCase), loose);

        Assert.All(strict, p => Assert.True(PhotoFilter.Accepts(_temp, p, strictNames: true)));
    }

    [Fact]
    public void EnumerateCandidates_DefaultsToStrict()
    {
        var ok = Create(Photo);
        Create("VRChat_2025-12-25_21-08-42.649_3840x2160 - Copy.png");
        Assert.Equal([ok], PhotoFilter.EnumerateCandidates(_temp).ToList());
    }

    [Fact]
    public void EnumerateCandidates_OnlyExcludesTopLevelFolders()
    {
        var nested = Create(@"2024-12\Prints\" + Photo);
        Assert.Equal([nested], PhotoFilter.EnumerateCandidates(_temp).ToList());
    }

    [Fact]
    public void EnumerateCandidates_MissingRoot_GivesNothing()
    {
        Assert.Empty(PhotoFilter.EnumerateCandidates(Path.Combine(_temp, "does-not-exist")));
    }

    [Fact]
    public void EnumerateCandidates_IsLazyAndSorted()
    {
        var b = Create(@"2024-11\VRChat_2024-11-02_10-00-00.000_1920x1080.png");
        var a = Create(@"2024-11\VRChat_2024-11-01_10-00-00.000_1920x1080.png");
        var r = Create("VRChat_2024-10-01_10-00-00.000_1920x1080.png");
        Assert.Equal([r, a, b], PhotoFilter.EnumerateCandidates(_temp).ToList());
    }
}
