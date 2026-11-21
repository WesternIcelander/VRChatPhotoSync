using System.Runtime.InteropServices;
using System.Text.Json;

namespace VRChatPhotoSync;

/// <summary>
/// The photo folder settings read from VRChat's own <c>config.json</c>
/// (<c>%LocalAppData%Low\VRChat\VRChat\config.json</c>). The file belongs to VRChat: it is
/// only ever read (with <c>FileShare.ReadWrite | FileShare.Delete</c>), never written.
/// </summary>
/// <param name="PictureFolder">Full path of the camera roll root (env vars expanded).</param>
/// <param name="SplitByDate"><c>picture_output_split_by_date</c>; true when missing. Only describes where new photos go.</param>
/// <param name="Source">Where <paramref name="PictureFolder"/> came from: <see cref="SourceVRChatConfig"/>, <see cref="SourceDefault"/> or <see cref="SourceCommandLine"/>.</param>
/// <param name="ConfigPath">The VRChat config file that was looked for, or null if its location couldn't be found.</param>
/// <param name="Warnings">Problems met while loading (already logged). Empty when everything was fine.</param>
public sealed record VRChatSettings(
    string PictureFolder,
    bool SplitByDate,
    string Source,
    string? ConfigPath,
    IReadOnlyList<string> Warnings)
{
    public const string SourceVRChatConfig = "VRChat config";
    public const string SourceDefault = "default";
    public const string SourceCommandLine = "--folder";

    public const string FolderKey = "picture_output_folder";
    public const string SplitByDateKey = "picture_output_split_by_date";

    /// <summary>The name of the folder VRChat uses inside Pictures by default.</summary>
    public const string DefaultFolderName = "VRChat";

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Loads VRChat's photo folder settings. Never throws: any problem is logged through
    /// <paramref name="log"/>, recorded in <see cref="Warnings"/>, and the defaults are used.
    /// </summary>
    /// <param name="folderOverride">The <c>--folder</c> value, which wins over VRChat's setting for this run.</param>
    /// <param name="configPath">VRChat's config file; null finds it under <c>LocalLow</c>. Injectable for tests.</param>
    /// <param name="getPicturesFolder">Returns the user's Pictures folder; null uses <c>GetFolderPath(MyPictures)</c>. Injectable for tests.</param>
    /// <param name="log">Receives warning messages.</param>
    public static VRChatSettings Load(
        string? folderOverride = null,
        string? configPath = null,
        Func<string?>? getPicturesFolder = null,
        Action<string>? log = null)
    {
        var warnings = new List<string>();
        void Warn(string message)
        {
            warnings.Add(message);
            try { log?.Invoke(message); } catch { /* logging must never break loading */ }
        }

        string? folder = null;
        bool splitByDate = true;

        string? findError = null;
        try
        {
            configPath ??= FindDefaultConfigPath();
        }
        catch (Exception ex)
        {
            configPath = null;
            findError = $" ({ex.Message})";
        }

        if (configPath is null)
        {
            Warn($"Could not find VRChat's config folder{findError}; using the default photo folder.");
        }
        else
        {
            try
            {
                ReadConfig(configPath, Warn, out folder, out splitByDate);
            }
            catch (Exception ex)
            {
                folder = null;
                splitByDate = true;
                Warn($"Could not read VRChat config {configPath}: {ex.Message}");
            }
        }

        string source = SourceCommandLine;
        string? resolved = null;
        if (!string.IsNullOrWhiteSpace(folderOverride))
            resolved = TryNormalize(folderOverride, Warn, "--folder");

        if (resolved is null && folder is not null)
        {
            source = SourceVRChatConfig;
            resolved = TryNormalize(folder, Warn, FolderKey);
        }

        if (resolved is null)
        {
            source = SourceDefault;
            resolved = DefaultPictureFolder(getPicturesFolder, Warn);
        }

        return new VRChatSettings(resolved, splitByDate, source, configPath, warnings);
    }

    /// <summary>
    /// Reads the two keys from VRChat's config. A missing file is normal (defaults, no
    /// warning). Invalid JSON or wrong value types are warned about and give the defaults.
    /// </summary>
    private static void ReadConfig(string path, Action<string> warn, out string? folder, out bool splitByDate)
    {
        folder = null;
        splitByDate = true;

        if (!File.Exists(path))
            return;

        string json;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream))
            json = reader.ReadToEnd();

        if (string.IsNullOrWhiteSpace(json))
        {
            warn($"VRChat config {path} is empty; using the defaults.");
            return;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            warn($"VRChat config {path} is not valid JSON ({ex.Message}); using the defaults.");
            return;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                warn($"VRChat config {path} is not a JSON object; using the defaults.");
                return;
            }

            if (TryGetProperty(doc.RootElement, FolderKey, out var folderElement))
            {
                if (folderElement.ValueKind == JsonValueKind.String)
                {
                    var value = folderElement.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                        folder = value.Trim();
                }
                else if (folderElement.ValueKind != JsonValueKind.Null)
                {
                    warn($"VRChat config {FolderKey} is not a string; using the default photo folder.");
                }
            }

            if (TryGetProperty(doc.RootElement, SplitByDateKey, out var splitElement))
            {
                switch (splitElement.ValueKind)
                {
                    case JsonValueKind.True: splitByDate = true; break;
                    case JsonValueKind.False: splitByDate = false; break;
                    case JsonValueKind.Null: break;
                    case JsonValueKind.Number when splitElement.TryGetInt64(out var n): splitByDate = n != 0; break;
                    case JsonValueKind.String when bool.TryParse(splitElement.GetString()?.Trim(), out var b): splitByDate = b; break;
                    default:
                        warn($"VRChat config {SplitByDateKey} is not true/false; assuming true.");
                        break;
                }
            }
        }
    }

    /// <summary>Exact match first, then a case-insensitive one.</summary>
    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
            return true;
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    /// <summary>Expands environment variables and makes the path absolute, or returns null (with a warning) if it isn't a usable path.</summary>
    private static string? TryNormalize(string path, Action<string> warn, string what)
    {
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
            if (string.IsNullOrWhiteSpace(expanded))
                return null;
            // Checked before and after expansion: ExpandEnvironmentVariables stops at a NUL.
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || expanded.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                warn($"{what} \"{path}\" contains invalid path characters; ignoring it.");
                return null;
            }
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
        }
        catch (Exception ex)
        {
            warn($"{what} \"{path}\" is not a valid path ({ex.Message}); ignoring it.");
            return null;
        }
    }

    /// <summary>
    /// <c>&lt;Pictures&gt;\VRChat</c>, using the real (possibly moved) Pictures folder.
    /// Falls back to <c>%USERPROFILE%\Pictures</c> only if the lookup gives nothing.
    /// </summary>
    private static string DefaultPictureFolder(Func<string?>? getPicturesFolder, Action<string> warn)
    {
        string? pictures = null;
        try
        {
            pictures = getPicturesFolder is null
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
                : getPicturesFolder();
        }
        catch (Exception ex)
        {
            warn($"Could not look up the Pictures folder: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(pictures))
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(profile))
                profile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrWhiteSpace(profile))
                profile = AppContext.BaseDirectory;
            pictures = Path.Combine(profile, "Pictures");
        }

        var folder = Path.Combine(pictures, DefaultFolderName);
        return TryNormalize(folder, warn, "Default photo folder") ?? folder;
    }

    /// <summary>
    /// <c>&lt;LocalLow&gt;\VRChat\VRChat\config.json</c>, with LocalLow from the Known Folder
    /// API, or <c>%LOCALAPPDATA%\..\LocalLow</c> if that fails. Null if neither is available.
    /// </summary>
    public static string? FindDefaultConfigPath()
    {
        var localLow = GetLocalLowFolder();
        return localLow is null ? null : Path.Combine(localLow, "VRChat", "VRChat", "config.json");
    }

    /// <summary>The user's <c>AppData\LocalLow</c> folder, or null if it can't be found.</summary>
    public static string? GetLocalLowFolder()
    {
        try
        {
            var guid = FolderIdLocalAppDataLow;
            if (SHGetKnownFolderPath(ref guid, 0, IntPtr.Zero, out var pathPtr) == 0 && pathPtr != IntPtr.Zero)
            {
                try
                {
                    var path = Marshal.PtrToStringUni(pathPtr);
                    if (!string.IsNullOrWhiteSpace(path))
                        return path;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pathPtr);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            // Fall through to the environment-based fallback.
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData))
            return null;
        return Path.GetFullPath(Path.Combine(localAppData, "..", "LocalLow"));
    }

    /// <summary>FOLDERID_LocalAppDataLow.</summary>
    private static readonly Guid FolderIdLocalAppDataLow = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    /// <summary>A one-line description for the UI and log, e.g. <c>D:\Photos\VRChat (from VRChat config, split by month)</c>.</summary>
    public string Describe() =>
        $"{PictureFolder} ({(Source == SourceVRChatConfig ? "from VRChat config" : Source == SourceCommandLine ? "from --folder" : "VRChat default")}, {(SplitByDate ? "split by month" : "no month folders")})";
}
