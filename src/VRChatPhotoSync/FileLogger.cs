using System.Globalization;
using System.Text;

namespace VRChatPhotoSync;

/// <summary>How serious a log line is.</summary>
public enum LogLevel
{
    Info,
    Warn,
    Error,
}

/// <summary>
/// The app's log: one file per day, <c>logs\photosync-YYYYMMDD.log</c> next to the config, kept for
/// <see cref="DefaultRetentionDays"/> days. Every line is flushed straight away, because VRCX hard-kills the app.
/// <para>
/// Thread-safe, and it never throws: a log that can't be written must not break uploading. Any value registered with
/// <see cref="AddSecret"/> (the API key) is replaced by <see cref="Redacted"/> before a line is written, as a safety net;
/// the components themselves never put the key in a message.
/// </para>
/// </summary>
public sealed class FileLogger : IDisposable
{
    public const string FolderName = "logs";
    public const string FilePrefix = "photosync-";
    public const string FileExtension = ".log";
    public const string DateFormat = "yyyyMMdd";
    public const int DefaultRetentionDays = 7;

    /// <summary>What a secret is replaced with.</summary>
    public const string Redacted = "[redacted]";

    /// <summary>Secrets shorter than this aren't redacted (they would mangle ordinary text).</summary>
    public const int MinSecretLength = 6;

    private readonly object _lock = new();
    private readonly Func<DateTime> _now;
    private readonly List<string> _secrets = [];
    private StreamWriter? _writer;
    private DateTime _writerDate;
    private bool _disposed;

    /// <param name="directory">The log folder (normally <see cref="ForConfigDirectory"/>). Created on the first write.</param>
    /// <param name="now">Local time; injectable for tests.</param>
    /// <param name="retentionDays">How many days of files to keep, including today.</param>
    public FileLogger(string directory, Func<DateTime>? now = null, int retentionDays = DefaultRetentionDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(retentionDays, 1);
        Directory = Path.GetFullPath(directory);
        _now = now ?? (() => DateTime.Now);
        RetentionDays = retentionDays;
    }

    /// <summary>The <c>logs</c> folder inside the config directory (<see cref="ConfigStore.Directory"/>).</summary>
    public static string ForConfigDirectory(string configDirectory) => Path.Combine(configDirectory, FolderName);

    /// <summary>The log folder.</summary>
    public string Directory { get; }

    /// <summary>How many days of files are kept, including today.</summary>
    public int RetentionDays { get; }

    /// <summary>The file for today's lines.</summary>
    public string CurrentPath => PathFor(_now());

    /// <summary>The file for <paramref name="date"/>.</summary>
    public string PathFor(DateTime date) =>
        Path.Combine(Directory, FilePrefix + date.ToString(DateFormat, CultureInfo.InvariantCulture) + FileExtension);

    /// <summary>
    /// Makes sure <paramref name="secret"/> never appears in the log (it is replaced by <see cref="Redacted"/>). Null,
    /// blank and very short values are ignored.
    /// </summary>
    public void AddSecret(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
            return;
        var trimmed = secret.Trim();
        if (trimmed.Length < MinSecretLength)
            return;
        lock (_lock)
        {
            if (!_secrets.Contains(trimmed, StringComparer.Ordinal))
            {
                _secrets.Add(trimmed);
                // Longest first, so a secret that contains another is replaced whole.
                _secrets.Sort((a, b) => b.Length.CompareTo(a.Length));
            }
        }
    }

    public void Info(string message) => Write(LogLevel.Info, message);

    public void Warn(string message) => Write(LogLevel.Warn, message);

    public void Error(string message, Exception? exception = null) =>
        Write(LogLevel.Error, exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    /// <summary>Writes one line (or several, for an exception). Never throws.</summary>
    public void Write(LogLevel level, string message)
    {
        try
        {
            lock (_lock)
            {
                if (_disposed)
                    return;
                var now = _now();
                var writer = WriterFor(now);
                if (writer is null)
                    return;
                var text = Redact(message ?? "");
                var prefix = $"{now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {LevelText(level)} ";
                var lines = text.Replace("\r\n", "\n").Split('\n');
                var sb = new StringBuilder();
                sb.Append(prefix).Append(lines[0]).Append(Environment.NewLine);
                for (var i = 1; i < lines.Length; i++)
                    sb.Append("    ").Append(lines[i]).Append(Environment.NewLine);
                writer.Write(sb.ToString());
                writer.Flush();
            }
        }
        catch
        {
            // A log that can't be written must never break the app.
        }
    }

    /// <summary>
    /// Deletes <c>photosync-YYYYMMDD.log</c> files older than <see cref="RetentionDays"/> days (today counts as one).
    /// Other files are left alone. Returns how many were deleted. Never throws.
    /// </summary>
    public int DeleteOldFiles()
    {
        lock (_lock)
            return DeleteOldFilesLocked(_now().Date);
    }

    private int DeleteOldFilesLocked(DateTime today)
    {
        var deleted = 0;
        try
        {
            if (!System.IO.Directory.Exists(Directory))
                return 0;
            var oldest = today.AddDays(-(RetentionDays - 1));
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, FilePrefix + "*" + FileExtension))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (name.Length != FilePrefix.Length + DateFormat.Length)
                    continue;
                if (!DateTime.TryParseExact(name[FilePrefix.Length..], DateFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date))
                    continue;
                if (date >= oldest)
                    continue;
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use or read-only: try again next time.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return deleted;
    }

    /// <summary>The writer for <paramref name="now"/>'s file, rolling over (and cleaning up) when the day changed.</summary>
    private StreamWriter? WriterFor(DateTime now)
    {
        if (_writer is not null && _writerDate == now.Date)
            return _writer;

        _writer?.Dispose();
        _writer = null;
        System.IO.Directory.CreateDirectory(Directory);
        var stream = new FileStream(PathFor(now), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _writerDate = now.Date;
        DeleteOldFilesLocked(now.Date);
        return _writer;
    }

    private string Redact(string text)
    {
        foreach (var secret in _secrets)
            text = text.Replace(secret, Redacted, StringComparison.Ordinal);
        return text;
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Warn => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "INFO ",
    };

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }
}

/// <summary>Connects the components' log events to a <see cref="FileLogger"/>.</summary>
public static class LogWiring
{
    /// <summary>Logs the worker's lines and a line per outcome (skip reasons, results with asset ids, failures).</summary>
    public static UploadWorker AttachLog(this UploadWorker worker, FileLogger logger)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(logger);
        worker.Log += logger.Info;
        worker.StatusChanged += s =>
        {
            if (s.State is WorkerState.PausedAuth or WorkerState.PausedNotSetUp)
                logger.Warn($"Status: {s.Text}");
        };
        return worker;
    }

    /// <summary>Logs the catch-up scan and backfill lines.</summary>
    public static CatchUpScanner AttachLog(this CatchUpScanner scanner, FileLogger logger)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(logger);
        scanner.Log += logger.Info;
        return scanner;
    }

    // FolderWatcher, VRChatConfigWatcher, Debouncer, VRChatSettings.Load and UploadStateStore.Load take their log
    // through a constructor or method parameter: pass logger.Info (or logger.Warn).

    /// <summary>
    /// The start line: version, where the config and logs are, and the picture folder with its source
    /// (see <see cref="VRChatSettings.Describe"/>).
    /// </summary>
    public static void LogStart(this FileLogger logger, string version, string configDirectory, VRChatSettings vrchat)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(vrchat);
        logger.Info($"PhotoSync {version} starting (pid {Environment.ProcessId}). Config folder: {configDirectory}");
        logger.Info($"Picture folder: {vrchat.Describe()}" +
                    (vrchat.ConfigPath is null ? "" : $"; VRChat config: {vrchat.ConfigPath}"));
    }

    /// <summary>The stop line, with the picture folder and its source.</summary>
    public static void LogStop(this FileLogger logger, string reason, VRChatSettings? vrchat)
    {
        ArgumentNullException.ThrowIfNull(logger);
        logger.Info($"PhotoSync stopping ({reason})." + (vrchat is null ? "" : $" Picture folder: {vrchat.Describe()}"));
    }
}
