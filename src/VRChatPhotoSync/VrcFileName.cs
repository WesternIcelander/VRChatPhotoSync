using System.Globalization;
using System.Text.RegularExpressions;

namespace VRChatPhotoSync;

/// <summary>
/// Helpers for VRChat photo file names such as
/// <c>VRChat_2024-11-23_19-17-27.652_1920x1080.png</c> and its multi-layer siblings
/// (<c>…_1920x1080_Environment.png</c>, <c>_Player</c>, <c>_UI</c>).
/// All methods take a file name or a full path (only the file name part is used).
/// </summary>
public static partial class VrcFileName
{
    /// <summary>The suffixes (before <c>.png</c>) VRChat gives the layer files of a multi-layer photo.</summary>
    public static readonly IReadOnlyList<string> LayerSuffixes = ["_Environment", "_Player", "_UI"];

    private const string TimestampFormat = "yyyy-MM-dd_HH-mm-ss.fff";
    private const string TimestampFormatNoMs = "yyyy-MM-dd_HH-mm-ss";

    [GeneratedRegex(@"^VRChat_([0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{2}-[0-9]{2}-[0-9]{2}(?:\.[0-9]{3})?)(?![0-9.])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TimestampRegex();

    [GeneratedRegex(@"_([0-9]{1,6})x([0-9]{1,6})(?=[_.\s]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SizeRegex();

    /// <summary>
    /// Reads the capture time from the name (local time, to the millisecond).
    /// The result has <see cref="DateTimeKind.Local"/>.
    /// </summary>
    public static bool TryParseTimestamp(string fileNameOrPath, out DateTime localTime)
    {
        localTime = default;
        var match = TimestampRegex().Match(Path.GetFileName(fileNameOrPath));
        if (!match.Success)
            return false;
        var text = match.Groups[1].Value;
        if (!DateTime.TryParseExact(text, [TimestampFormat, TimestampFormatNoMs], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var parsed))
            return false;
        localTime = DateTime.SpecifyKind(parsed, DateTimeKind.Local);
        return true;
    }

    /// <summary>
    /// The capture time with the local UTC offset that applied at that moment: from the name when it
    /// has a timestamp, otherwise the earlier of the file's creation and last-write times.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="fileTimesUtc">Supplies (creation, lastWrite) in UTC; null reads them from disk. Injectable for tests.</param>
    public static DateTimeOffset GetCaptureTime(string path, Func<string, (DateTime CreatedUtc, DateTime ModifiedUtc)>? fileTimesUtc = null)
    {
        if (TryParseTimestamp(path, out var local))
            return new DateTimeOffset(local);

        var (created, modified) = fileTimesUtc is null
            ? (File.GetCreationTimeUtc(path), File.GetLastWriteTimeUtc(path))
            : fileTimesUtc(path);
        var utc = DateTime.SpecifyKind(created < modified ? created : modified, DateTimeKind.Utc);
        return new DateTimeOffset(utc).ToLocalTime();
    }

    /// <summary>Reads the <c>_&lt;W&gt;x&lt;H&gt;</c> part of the name, e.g. <c>_3840x2160</c>.</summary>
    public static bool TryParseSize(string fileNameOrPath, out int width, out int height)
    {
        width = height = 0;
        var match = SizeRegex().Match(Path.GetFileName(fileNameOrPath));
        if (!match.Success)
            return false;
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var w) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var h) ||
            w <= 0 || h <= 0)
            return false;
        width = w;
        height = h;
        return true;
    }

    /// <summary>True for a multi-layer photo's layer file (name ends in <c>_Environment.png</c>, <c>_Player.png</c> or <c>_UI.png</c>, ignoring case).</summary>
    public static bool IsLayer(string fileNameOrPath) => LayerSuffix(Path.GetFileName(fileNameOrPath)) is not null;

    /// <summary>
    /// The name without the extension and without any layer suffix, shared by a combined photo and its layers:
    /// <c>VRChat_…_3840x2160_UI.png</c> and <c>VRChat_…_3840x2160.png</c> both give <c>VRChat_…_3840x2160</c>.
    /// </summary>
    public static string BaseName(string fileNameOrPath)
    {
        var name = Path.GetFileName(fileNameOrPath);
        var suffix = LayerSuffix(name);
        var withoutExtension = Path.GetFileNameWithoutExtension(name);
        return suffix is null ? withoutExtension : withoutExtension[..^suffix.Length];
    }

    /// <summary>The possible layer file paths for a photo (in the same folder), e.g. to check whether any exists.</summary>
    public static IEnumerable<string> LayerSiblingPaths(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        var baseName = BaseName(path);
        foreach (var suffix in LayerSuffixes)
            yield return Path.Combine(dir, baseName + suffix + ".png");
    }

    /// <summary>The path of the combined photo that a layer belongs to (same folder); the path itself if it isn't a layer.</summary>
    public static string CombinedPath(string path)
    {
        if (!IsLayer(path))
            return path;
        return Path.Combine(Path.GetDirectoryName(path) ?? "", BaseName(path) + ".png");
    }

    private static string? LayerSuffix(string fileName)
    {
        if (!fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            return null;
        var stem = fileName[..^4];
        foreach (var suffix in LayerSuffixes)
        {
            if (stem.Length > suffix.Length && stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return suffix;
        }
        return null;
    }
}
