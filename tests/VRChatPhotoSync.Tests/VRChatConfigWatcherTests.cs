using System.Collections.Concurrent;

namespace VRChatPhotoSync.Tests;

public sealed class VRChatConfigWatcherTests : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "PhotoSyncTests", Guid.NewGuid().ToString("N"));
    private readonly string _configDir;
    private readonly string _configPath;
    private readonly string _pictures;
    private readonly ConcurrentQueue<PictureFolderChangedEventArgs> _changes = new();
    private readonly ConcurrentQueue<VRChatSettings> _reloads = new();

    public VRChatConfigWatcherTests()
    {
        _configDir = Path.Combine(_root, "LocalLow", "VRChat", "VRChat");
        _configPath = Path.Combine(_configDir, "config.json");
        _pictures = Path.Combine(_root, "Pictures");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private VRChatSettings Load() => VRChatSettings.Load(null, _configPath, () => _pictures);

    private VRChatConfigWatcher CreateWatcher()
    {
        var watcher = new VRChatConfigWatcher(_configPath, Load(), Load, Debounce, Poll);
        watcher.PictureFolderChanged += (_, e) => _changes.Enqueue(e);
        watcher.SettingsReloaded += _reloads.Enqueue;
        return watcher;
    }

    private void WriteConfig(string folder, bool split = true)
    {
        var json = $"{{\"picture_output_folder\": \"{folder.Replace("\\", "\\\\")}\", \"picture_output_split_by_date\": {(split ? "true" : "false")}}}";
        File.WriteAllText(_configPath, json);
    }

    private static void WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"Timed out waiting for {what}.");
            Thread.Sleep(20);
        }
    }

    [Fact]
    public void FolderChange_RaisesPictureFolderChanged()
    {
        Directory.CreateDirectory(_configDir);
        WriteConfig(@"D:\Photos\VRChat");
        using var watcher = CreateWatcher();
        watcher.Start();
        Assert.True(watcher.IsWatching);
        Assert.Equal(@"D:\Photos\VRChat", watcher.Current.PictureFolder);

        WriteConfig(@"E:\Other\VRChat");

        WaitFor(() => !_changes.IsEmpty, "PictureFolderChanged");
        Thread.Sleep(Debounce * 3);
        var change = Assert.Single(_changes);
        Assert.Equal(@"D:\Photos\VRChat", change.OldSettings.PictureFolder);
        Assert.Equal(@"E:\Other\VRChat", change.NewSettings.PictureFolder);
        Assert.Equal(@"E:\Other\VRChat", watcher.Current.PictureFolder);
    }

    [Fact]
    public void OtherChanges_ReloadButDoNotRaiseFolderChanged()
    {
        Directory.CreateDirectory(_configDir);
        WriteConfig(@"D:\Photos\VRChat", split: true);
        using var watcher = CreateWatcher();
        watcher.Start();

        WriteConfig(@"D:\Photos\VRChat", split: false);
        WaitFor(() => _reloads.Any(s => !s.SplitByDate), "reload with SplitByDate=false");

        // Same folder with a different case and a trailing separator is still the same folder.
        WriteConfig(@"d:\photos\vrchat\", split: false);
        WaitFor(() => _reloads.Count >= 2, "second reload");
        Thread.Sleep(Debounce * 3);

        Assert.Empty(_changes);
        Assert.False(watcher.Current.SplitByDate);
    }

    [Fact]
    public void UnrelatedFilesInTheFolder_AreIgnored()
    {
        Directory.CreateDirectory(_configDir);
        WriteConfig(@"D:\Photos\VRChat");
        using var watcher = CreateWatcher();
        watcher.Start();

        File.WriteAllText(Path.Combine(_configDir, "output_log_2026-11-21_21-00-00.txt"), "log line");
        Thread.Sleep(Debounce * 4);

        Assert.Empty(_reloads);
    }

    [Fact]
    public void ConfigDeleted_FallsBackToDefaultFolder()
    {
        Directory.CreateDirectory(_configDir);
        WriteConfig(@"D:\Photos\VRChat");
        using var watcher = CreateWatcher();
        watcher.Start();

        File.Delete(_configPath);

        WaitFor(() => !_changes.IsEmpty, "PictureFolderChanged after delete");
        Assert.Equal(Path.Combine(_pictures, "VRChat"), _changes.Single().NewSettings.PictureFolder);
    }

    [Fact]
    public void MissingConfigFolder_IsWaitedFor_NotCreated()
    {
        using var watcher = CreateWatcher();
        watcher.Start();
        Assert.False(watcher.IsWatching);

        Thread.Sleep(Poll * 5);
        Assert.False(Directory.Exists(_configDir));

        Directory.CreateDirectory(_configDir);
        WriteConfig(@"D:\Photos\VRChat");

        WaitFor(() => watcher.IsWatching, "watching the config folder");
        WaitFor(() => !_changes.IsEmpty, "PictureFolderChanged once the config appeared");
        Assert.Equal(@"D:\Photos\VRChat", watcher.Current.PictureFolder);
    }

    [Fact]
    public void Reload_NeverWritesTheConfigFile()
    {
        Directory.CreateDirectory(_configDir);
        WriteConfig(@"D:\Photos\VRChat");
        var old = DateTime.UtcNow.AddDays(-1);
        File.SetLastWriteTimeUtc(_configPath, old);
        var bytes = File.ReadAllBytes(_configPath);

        using (var watcher = CreateWatcher())
        {
            watcher.Start();
            watcher.Reload();
            watcher.Reload();
        }

        Assert.Equal(bytes, File.ReadAllBytes(_configPath));
        Assert.Equal(old, File.GetLastWriteTimeUtc(_configPath));
        Assert.Single(Directory.GetFiles(_configDir));
    }

    [Fact]
    public void Reload_WithFolderOverride_NeverChangesFolder()
    {
        Directory.CreateDirectory(_configDir);
        WriteConfig(@"D:\Photos\VRChat");
        VRChatSettings LoadWithOverride() => VRChatSettings.Load(@"C:\Scratch", _configPath, () => _pictures);
        using var watcher = new VRChatConfigWatcher(_configPath, LoadWithOverride(), LoadWithOverride, Debounce, Poll);
        watcher.PictureFolderChanged += (_, e) => _changes.Enqueue(e);
        watcher.Start();

        WriteConfig(@"E:\Other\VRChat");
        Thread.Sleep(Debounce * 4);
        watcher.Reload();

        Assert.Empty(_changes);
        Assert.Equal(@"C:\Scratch", watcher.Current.PictureFolder);
    }
}
