using System.Text.Json;
using System.Text.Json.Serialization;

namespace VRChatPhotoSync;

/// <summary>
/// The contents of <c>state.json</c>: which picture folder it belongs to, when the last
/// catch-up scan ran, every file that was uploaded or skipped, and the session counters
/// (which carry over a kill-and-relaunch, see <see cref="ResolveSession"/>).
/// Saved by <see cref="UploadStateStore"/> after every step, so a kill loses nothing.
/// </summary>
public sealed class UploadState
{
    /// <summary>A session continues when its last heartbeat is younger than this.</summary>
    public static readonly TimeSpan SessionContinueWindow = TimeSpan.FromMinutes(2);

    private Dictionary<string, StateEntry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The camera roll root the entries are relative to. Null before the first run.</summary>
    public string? PictureFolder { get; set; }

    /// <summary>When the last catch-up scan started. Null before the first run.</summary>
    public DateTime? LastScanUtc { get; set; }

    /// <summary>Uploaded or skipped files, keyed by path relative to <see cref="PictureFolder"/> (case-insensitive).</summary>
    public Dictionary<string, StateEntry> Entries
    {
        get => _entries;
        set => _entries = new Dictionary<string, StateEntry>(value ?? [], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The counters shown in the window ("uploaded this session").</summary>
    public SessionInfo Session { get; set; } = new();

    /// <summary>Refreshed every 30 s and after each upload; decides whether a relaunch continues the session.</summary>
    public DateTime? HeartbeatUtc { get; set; }

    /// <summary>
    /// The entry key for <paramref name="path"/>: a full path under <see cref="PictureFolder"/>
    /// becomes relative to it; separators are normalised to <c>\</c>.
    /// </summary>
    public string ToKey(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var key = path;
        if (Path.IsPathFullyQualified(path) && !string.IsNullOrEmpty(PictureFolder))
            key = Path.GetRelativePath(PictureFolder, path);
        return key.Replace('/', '\\');
    }

    /// <summary>The entry for <paramref name="path"/> (relative or full), or null.</summary>
    public StateEntry? Get(string path) => _entries.GetValueOrDefault(ToKey(path));

    /// <summary>Adds or replaces the entry for <paramref name="path"/> (relative or full).</summary>
    public void Set(string path, StateEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries[ToKey(path)] = entry;
    }

    /// <summary>Removes the entry for <paramref name="path"/>; true if there was one.</summary>
    public bool Remove(string path) => _entries.Remove(ToKey(path));

    /// <summary>
    /// True when <paramref name="path"/> has an entry (uploaded or skipped) with the same size
    /// and last-write time, so it doesn't need to be processed again.
    /// </summary>
    public bool IsUnchanged(string path, long size, DateTime mtimeUtc)
    {
        var entry = Get(path);
        return entry is not null
            && entry.Size == size
            && ToUtc(entry.MtimeUtc) == ToUtc(mtimeUtc);
    }

    /// <summary>
    /// Moves the entry of a renamed file to its new path (an existing entry there is
    /// replaced). Returns false, and changes nothing, if the old path has no entry.
    /// </summary>
    public bool MoveEntry(string oldPath, string newPath)
    {
        var oldKey = ToKey(oldPath);
        var newKey = ToKey(newPath);
        if (!_entries.Remove(oldKey, out var entry))
            return false;
        _entries[newKey] = entry;
        return true;
    }

    /// <summary>
    /// Decides at startup whether to continue the previous session: if the last heartbeat is
    /// under <see cref="SessionContinueWindow"/> old (and not in the future), the counters
    /// carry on; otherwise a new session starts at 0. Either way the heartbeat is set to
    /// <paramref name="nowUtc"/>. Returns true when the session was continued.
    /// </summary>
    public bool ResolveSession(DateTime nowUtc)
    {
        nowUtc = ToUtc(nowUtc);
        var continued = HeartbeatUtc is { } beat
            && nowUtc >= ToUtc(beat)
            && nowUtc - ToUtc(beat) < SessionContinueWindow;
        if (!continued)
            Session = new SessionInfo { StartUtc = nowUtc };
        HeartbeatUtc = nowUtc;
        return continued;
    }

    /// <summary>Refreshes the heartbeat (every 30 s and after each upload).</summary>
    public void Heartbeat(DateTime nowUtc) => HeartbeatUtc = ToUtc(nowUtc);

    /// <summary>
    /// Ties the state to <paramref name="pictureFolder"/>. On the very first run, or when the
    /// folder differs from the one in the state, this is treated as a first run: the entries
    /// (relative to the old folder) are dropped and <see cref="LastScanUtc"/> is set to
    /// <paramref name="nowUtc"/>, so nothing old is mass-uploaded. Returns true in that case.
    /// The session counters are kept.
    /// </summary>
    public bool BindToFolder(string pictureFolder, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(pictureFolder);
        var folder = NormalizeFolder(pictureFolder);
        if (LastScanUtc is not null
            && PictureFolder is not null
            && string.Equals(NormalizeFolder(PictureFolder), folder, StringComparison.OrdinalIgnoreCase))
            return false;

        PictureFolder = folder;
        LastScanUtc = ToUtc(nowUtc);
        _entries.Clear();
        return true;
    }

    private static string NormalizeFolder(string folder) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));

    internal static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}

/// <summary>One uploaded or skipped file in <c>state.json</c>.</summary>
public sealed record StateEntry
{
    public const string SkippedPrint = "print";
    public const string SkippedLayer = "layer";

    /// <summary>File size in bytes when it was processed.</summary>
    public long Size { get; init; }

    /// <summary>Last-write time (UTC) when it was processed.</summary>
    public DateTime MtimeUtc { get; init; }

    /// <summary>SHA-1 (hex) of the uploaded bytes; null for skipped files.</summary>
    public string? Sha1 { get; init; }

    /// <summary>The Immich asset id; null for skipped files.</summary>
    public string? AssetId { get; init; }

    /// <summary><see cref="SkippedPrint"/> or <see cref="SkippedLayer"/> when the file was not uploaded; else null.</summary>
    public string? Skipped { get; init; }
}

/// <summary>The counters of one session (a run of the app, carried over a quick relaunch).</summary>
public sealed record SessionInfo
{
    public DateTime StartUtc { get; init; }
    public int Uploaded { get; set; }
    public int Duplicates { get; set; }
    public int Failed { get; set; }
    public int PrintsSkipped { get; set; }
    public int LayersSkipped { get; set; }

    /// <summary>The most recently uploaded file (relative path), or null.</summary>
    public string? LastFile { get; set; }
}

/// <summary>
/// Loads and saves <c>state.json</c> in the config directory (see <see cref="ConfigStore.Directory"/>).
/// Saving is atomic (temp file, then <see cref="File.Replace(string, string, string?)"/>), so a
/// kill mid-save leaves the previous file intact.
/// </summary>
public sealed class UploadStateStore
{
    public const string FileName = "state.json";

    public UploadStateStore(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        Directory = Path.GetFullPath(directory);
    }

    /// <summary>The folder that holds <c>state.json</c>.</summary>
    public string Directory { get; }

    /// <summary>Full path of <c>state.json</c>.</summary>
    public string StatePath => Path.Combine(Directory, FileName);

    private string TempPath => StatePath + ".tmp";

    /// <summary>
    /// Loads the state. A missing file gives a fresh state; a corrupt or unreadable one gives
    /// a fresh state and a warning through <paramref name="log"/>. Never throws.
    /// </summary>
    public UploadState Load(Action<string>? log = null)
    {
        var path = StatePath;
        if (!File.Exists(path))
            return new UploadState();

        try
        {
            var json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize(json, StateJsonContext.Default.UploadState);
            if (state is null)
            {
                Warn(log, $"State file {path} is empty; starting with a fresh state.");
                return new UploadState();
            }
            state.Session ??= new SessionInfo();
            foreach (var key in state.Entries.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
                state.Entries.Remove(key);
            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Warn(log, $"Could not read state file {path} ({ex.Message}); starting with a fresh state.");
            return new UploadState();
        }
    }

    /// <summary>
    /// Writes the state atomically: the JSON goes to <c>state.json.tmp</c> (flushed to disk),
    /// which then replaces <c>state.json</c>.
    /// </summary>
    public void Save(UploadState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        System.IO.Directory.CreateDirectory(Directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, StateJsonContext.Default.UploadState);

        using (var stream = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(StatePath))
        {
            try
            {
                File.Replace(TempPath, StatePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
            {
                // File.Replace isn't supported everywhere (e.g. some network shares); a move
                // with overwrite is still a single rename on the same volume.
            }
        }
        File.Move(TempPath, StatePath, overwrite: true);
    }

    private static void Warn(Action<string>? log, string message)
    {
        try { log?.Invoke(message); } catch { /* logging must never break loading */ }
    }
}

/// <summary>Source-generated JSON metadata for <c>state.json</c> (camelCase, indented).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(UploadState))]
internal sealed partial class StateJsonContext : JsonSerializerContext
{
}
