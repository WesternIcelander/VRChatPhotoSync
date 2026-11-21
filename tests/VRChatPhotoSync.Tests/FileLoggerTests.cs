using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace VRChatPhotoSync.Tests;

public sealed class FileLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PhotoSyncLogTests-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 11, 21, 21, 34, 55, 168, DateTimeKind.Local);

    private string LogDir => FileLogger.ForConfigDirectory(_dir);

    private FileLogger CreateLogger() => new(LogDir, () => _now);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private string AllLogText() =>
        string.Concat(Directory.EnumerateFiles(LogDir).Select(ReadShared));

    [Fact]
    public void Writes_To_Logs_Folder_Next_To_Config_With_Date_In_Name()
    {
        using var logger = CreateLogger();

        logger.Info("hello");
        logger.Warn("careful");
        logger.Error("broken", new InvalidOperationException("boom"));

        var path = Path.Combine(_dir, "logs", "photosync-20261121.log");
        Assert.Equal(path, logger.CurrentPath);
        var lines = ReadShared(path).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("2026-11-21 21:34:55.168 INFO  hello", lines[0]);
        Assert.Equal("2026-11-21 21:34:55.168 WARN  careful", lines[1]);
        Assert.Equal("2026-11-21 21:34:55.168 ERROR broken", lines[2]);
        Assert.Contains(lines.Skip(3), l => l.StartsWith("    ") && l.Contains("InvalidOperationException: boom"));
    }

    [Fact]
    public void Each_Line_Is_Flushed_Straight_Away()
    {
        using var logger = CreateLogger();
        logger.Info("first");
        // Still open; a hard kill now must not lose the line.
        Assert.Contains("first", ReadShared(logger.CurrentPath));
    }

    [Fact]
    public void Appends_To_Existing_File_After_Restart()
    {
        using (var logger = CreateLogger())
            logger.Info("run 1");
        using (var logger = CreateLogger())
            logger.Info("run 2");

        var text = ReadShared(Path.Combine(LogDir, "photosync-20261121.log"));
        Assert.Contains("run 1", text);
        Assert.Contains("run 2", text);
    }

    [Fact]
    public void Rolls_Over_At_Midnight()
    {
        using var logger = CreateLogger();
        _now = new DateTime(2026, 11, 21, 23, 59, 59, DateTimeKind.Local);
        logger.Info("before midnight");
        _now = _now.AddSeconds(2);
        logger.Info("after midnight");

        Assert.Contains("before midnight", ReadShared(Path.Combine(LogDir, "photosync-20261121.log")));
        var next = ReadShared(Path.Combine(LogDir, "photosync-20261122.log"));
        Assert.Contains("after midnight", next);
        Assert.DoesNotContain("before midnight", next);
    }

    [Fact]
    public void Keeps_Seven_Days_And_Leaves_Other_Files_Alone()
    {
        Directory.CreateDirectory(LogDir);
        for (var d = 0; d <= 10; d++)
            File.WriteAllText(Path.Combine(LogDir, $"photosync-{_now.AddDays(-d):yyyyMMdd}.log"), "old");
        File.WriteAllText(Path.Combine(LogDir, "photosync-notadate.log"), "x");
        File.WriteAllText(Path.Combine(LogDir, "other.txt"), "x");

        using var logger = CreateLogger();
        logger.Info("start");

        var names = Directory.EnumerateFiles(LogDir).Select(p => Path.GetFileName(p)!).Order().ToList();
        var expected = Enumerable.Range(0, 7).Select(d => $"photosync-{_now.AddDays(-d):yyyyMMdd}.log")
            .Append("photosync-notadate.log").Append("other.txt").Order().ToList();
        Assert.Equal(expected, names);
    }

    [Fact]
    public void Rollover_Deletes_Files_That_Became_Too_Old()
    {
        using var logger = CreateLogger();
        logger.Info("day 1");
        _now = _now.AddDays(7);
        logger.Info("day 8");

        Assert.False(File.Exists(Path.Combine(LogDir, "photosync-20261121.log")));
        Assert.True(File.Exists(Path.Combine(LogDir, "photosync-20261128.log")));
    }

    [Fact]
    public void Redacts_Registered_Secrets()
    {
        const string key = "SuperSecretKey123456";
        using var logger = CreateLogger();
        logger.AddSecret(key);
        logger.AddSecret("abc"); // too short: ignored

        logger.Info($"Server said: invalid key {key}");
        logger.Error("failed", new Exception($"x-api-key: {key}"));
        logger.Info("abc stays");

        var text = AllLogText();
        Assert.DoesNotContain(key, text);
        Assert.Contains(FileLogger.Redacted, text);
        Assert.Contains("abc stays", text);
    }

    [Fact]
    public void Never_Throws_When_The_Folder_Cannot_Be_Written()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(LogDir, "a file where the folder should be");
        using var logger = CreateLogger();
        logger.Info("lost");
        logger.Error("lost too", new Exception());
    }

    [Fact]
    public void Ignores_Writes_After_Dispose()
    {
        var logger = CreateLogger();
        logger.Info("one");
        logger.Dispose();
        logger.Info("two");
        Assert.DoesNotContain("two", AllLogText());
    }

    [Fact]
    public void Start_And_Stop_Lines_Name_The_Picture_Folder_And_Source()
    {
        var vrchat = new VRChatSettings(@"D:\Photos\VRChat", true, VRChatSettings.SourceVRChatConfig,
            @"C:\Users\someone\AppData\LocalLow\VRChat\VRChat\config.json", []);
        using var logger = CreateLogger();

        logger.LogStart("0.1.0", _dir, vrchat);
        logger.LogStop("exit", vrchat);

        var text = AllLogText();
        Assert.Contains("PhotoSync 0.1.0 starting", text);
        Assert.Contains(@"Picture folder: D:\Photos\VRChat (from VRChat config, split by month)", text);
        Assert.Contains("PhotoSync stopping (exit)", text);
    }

    // ---- The API key never reaches the log ----

    private const string Root = @"D:\Photos\VRChat";
    private const string Month = Root + @"\2026-11";
    private const string Photo1 = Month + @"\VRChat_2026-11-21_21-34-55.168_3840x2160.png";
    private const string Photo2 = Month + @"\VRChat_2026-11-21_21-36-10.001_3840x2160.png";
    private const string Print = Month + @"\VRChat_2026-11-04_01-48-06.885_2048x1440.png";

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string?> Keys { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Keys)
                Keys.Add(request.Headers.TryGetValues("x-api-key", out var v) ? v.Single() : null);
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task Fake_Upload_Never_Writes_The_Api_Key_To_The_Log()
    {
        var apiKey = "TestKey-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var uploads = 0;
        // Upload 1: a server error that echoes the key (the redaction safety net), then created.
        // Upload 2: created, then the album calls.
        var handler = new ScriptedHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/assets") && request.Method == HttpMethod.Post)
            {
                var n = Interlocked.Increment(ref uploads);
                return n == 1
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent($"bad gateway for key {apiKey}") }
                    : new HttpResponseMessage(HttpStatusCode.Created)
                    {
                        Content = new StringContent($"{{\"id\":\"asset-{n}\",\"status\":\"created\"}}", Encoding.UTF8, "application/json"),
                    };
            }
            if (path.EndsWith("/albums") && request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"id\":\"album-1\",\"albumName\":\"VRChat\"}]", Encoding.UTF8, "application/json") };
            if (path.EndsWith("/albums/album-1/assets"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"id\":\"asset-2\",\"success\":true}]", Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var client = new ImmichClient("https://immich.example.com", apiKey, handler);

        var clock = new FakeClock();
        var probe = new FakeProbe(clock);
        var contents = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in new[] { Photo1, Print })
        {
            contents[p] = RandomNumberGenerator.GetBytes(1000);
            probe.Add(p, 1000);
        }

        var queue = new UploadQueue();
        var state = new UploadState { PictureFolder = Root, LastScanUtc = DateTime.UtcNow };
        var config = new AppConfig { VrcxWaitSeconds = 0, AlbumName = "VRChat" };
        using var logger = CreateLogger();
        logger.AddSecret(apiKey);

        var worker = new UploadWorker(queue, state, _ => { }, () => config, () => client, probe, clock,
                isPrint: p => p == Print,
                readFile: p => contents[p])
            .AttachLog(logger);
        var done = new TaskCompletionSource();
        worker.Uploaded += _ => done.TrySetResult();

        Assert.True(queue.Enqueue(Print));
        Assert.True(queue.Enqueue(Photo1));
        Assert.True(queue.Enqueue(Photo2)); // doesn't exist: "disappeared"
        queue.Complete();
        var run = worker.RunAsync();
        // The transient failure waits 5 s on the fake clock; cut it short.
        while (!run.IsCompleted)
        {
            await Task.Delay(10);
            worker.Resume();
        }
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(done.Task.IsCompleted);

        // The key really was sent with every request...
        Assert.NotEmpty(handler.Keys);
        Assert.All(handler.Keys, k => Assert.Equal(apiKey, k));

        // ...but it is nowhere in the log, while the interesting events are.
        var text = AllLogText();
        Assert.DoesNotContain(apiKey, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"Queued {Photo1}", text);
        Assert.Contains("Skipped 2026-11/VRChat_2026-11-04_01-48-06.885_2048x1440.png (print)", text.Replace('\\', '/'));
        Assert.Contains("Settled", text);
        Assert.Contains("retry 1 in 5 s", text);
        Assert.Contains("→ asset asset-2", text);
        Assert.Contains(FileLogger.Redacted, text);

        // Without the safety net, the normal path still never logs the key.
        var withoutRedaction = text.Split(Environment.NewLine).Where(l => !l.Contains("retry")).ToList();
        Assert.DoesNotContain(withoutRedaction, l => l.Contains(FileLogger.Redacted));
    }
}
