using System.Text.Json;

namespace VRChatPhotoSync;

/// <summary>
/// Loads and saves <c>config.json</c>. The file lives next to the exe so the app is
/// portable. If that folder can't be written to, it lives in
/// <c>%APPDATA%\VRChatPhotoSync\</c> instead. <c>state.json</c> and the logs go in the
/// same <see cref="Directory"/>.
/// </summary>
public sealed class ConfigStore
{
    public const string FileName = "config.json";
    public const string AppDataFolderName = "VRChatPhotoSync";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _exeDirectory;

    /// <summary>Uses the exe directory and <c>%APPDATA%\VRChatPhotoSync</c>.</summary>
    public ConfigStore()
        : this(AppContext.BaseDirectory,
               Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppDataFolderName))
    {
    }

    /// <summary>Both directories are injectable so tests can use temp folders.</summary>
    public ConfigStore(string exeDirectory, string appDataDirectory)
        : this(exeDirectory, appDataDirectory, IsWritable)
    {
    }

    /// <summary>Lets tests fake an exe dir that can't be written to.</summary>
    internal ConfigStore(string exeDirectory, string appDataDirectory, Func<string, bool> isWritable)
    {
        _exeDirectory = Path.GetFullPath(exeDirectory);
        AppDataDirectory = Path.GetFullPath(appDataDirectory);
        Directory = isWritable(_exeDirectory) ? _exeDirectory : AppDataDirectory;
    }

    /// <summary>The fallback directory under <c>%APPDATA%</c>.</summary>
    public string AppDataDirectory { get; }

    /// <summary>Where the config (and state and logs) are kept: the exe dir if writable, else the <c>%APPDATA%</c> fallback.</summary>
    public string Directory { get; }

    /// <summary>True when the exe dir couldn't be written to and the <c>%APPDATA%</c> fallback is in use.</summary>
    public bool UsesAppDataFallback => !string.Equals(Directory, _exeDirectory, StringComparison.OrdinalIgnoreCase);

    /// <summary>Full path of the config file that <see cref="Save"/> writes.</summary>
    public string ConfigPath => Path.Combine(Directory, FileName);

    /// <summary>
    /// Loads the config. A missing file gives the defaults. When the fallback is in use and
    /// it has no file yet, a read-only <c>config.json</c> next to the exe is used as the
    /// starting point. A valid server URL is normalised (see <see cref="NormalizeServerUrl"/>).
    /// Never throws: an unreadable or invalid file gives the defaults and
    /// the reason in <see cref="ConfigLoadResult.Error"/>.
    /// </summary>
    public ConfigLoadResult Load()
    {
        var path = ConfigPath;
        if (!File.Exists(path) && UsesAppDataFallback)
        {
            var exePath = Path.Combine(_exeDirectory, FileName);
            if (File.Exists(exePath))
                path = exePath;
        }

        if (!File.Exists(path))
            return new ConfigLoadResult(new AppConfig(), path, Exists: false, Error: null);

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            if (config is null)
                return new ConfigLoadResult(new AppConfig(), path, Exists: true, Error: "The config file is empty.");
            config = config with { ServerUrl = NormalizeServerUrl(config.ServerUrl) };
            return new ConfigLoadResult(config, path, Exists: true, Error: null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new ConfigLoadResult(new AppConfig(), path, Exists: true, Error: $"Could not read {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes the config atomically (temp file, then replace). The server URL is stored in
    /// its normalised form (see <see cref="NormalizeServerUrl"/>). Throws
    /// <see cref="ArgumentException"/> if a server URL is set but isn't an absolute
    /// http/https URL.
    /// </summary>
    public void Save(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!string.IsNullOrWhiteSpace(config.ServerUrl) && !IsValidServerUrl(config.ServerUrl))
            throw new ArgumentException($"Server URL must be an absolute http or https URL: {config.ServerUrl}", nameof(config));

        config = config with { ServerUrl = NormalizeServerUrl(config.ServerUrl) };

        System.IO.Directory.CreateDirectory(Directory);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var tempPath = ConfigPath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, ConfigPath, overwrite: true);
    }

    /// <summary>
    /// Returns the Immich API base URL for <paramref name="url"/>: whitespace and any
    /// trailing <c>/</c> trimmed, and <c>/api</c> added if it isn't already the last path
    /// segment. So <c>https://immich.example.com/</c> becomes
    /// <c>https://immich.example.com/api</c>, and <c>https://immich.example.com/api/</c>
    /// becomes <c>https://immich.example.com/api</c>. A null, empty or invalid URL (see
    /// <see cref="IsValidServerUrl"/>) is returned unchanged so the caller can report it.
    /// </summary>
    public static string? NormalizeServerUrl(string? url)
    {
        if (!IsValidServerUrl(url))
            return url;

        var trimmed = url!.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            return trimmed[..^4] + "/api";
        return trimmed + "/api";
    }

    /// <summary>True if <paramref name="url"/> is an absolute <c>http</c> or <c>https</c> URL with a host.</summary>
    public static bool IsValidServerUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host);

    private static bool IsWritable(string directory)
    {
        try
        {
            if (!System.IO.Directory.Exists(directory))
                return false;
            var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}.tmp");
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}

/// <param name="Config">The loaded config, or the defaults.</param>
/// <param name="Path">The file that was read (or would have been).</param>
/// <param name="Exists">Whether that file exists.</param>
/// <param name="Error">Why the file couldn't be used, or null.</param>
public sealed record ConfigLoadResult(AppConfig Config, string Path, bool Exists, string? Error)
{
    /// <summary>True when a server is set and its URL is an absolute http/https URL.</summary>
    public bool HasValidServerUrl => ConfigStore.IsValidServerUrl(Config.ServerUrl);
}
