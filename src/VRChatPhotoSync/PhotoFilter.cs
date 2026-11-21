using System.Text.RegularExpressions;

namespace VRChatPhotoSync;

/// <summary>
/// Decides which files in the camera roll are the user's own photos and may be uploaded.
/// Only <c>VRChat_*.png</c> outside the folders holding other people's content
/// (<see cref="ExcludedFolders"/>) is accepted; with strict names on, the exact name
/// pattern VRChat and VRCX produce is required too.
/// </summary>
public static partial class PhotoFilter
{
    /// <summary>Top-level folders under the camera roll that hold other people's content and are never uploaded.</summary>
    public static readonly IReadOnlyList<string> ExcludedFolders = ["Prints", "Stickers", "Emoji"];

    public const string RequiredPrefix = "VRChat_";
    public const string RequiredExtension = ".png";

    /// <summary>
    /// The exact names VRChat writes, plus a layer suffix, or VRCX's rename to <c>_&lt;worldId&gt;</c>
    /// (which is a bare <c>_</c> when the world id is empty). <c>[0-9]</c> stands for the plan's <c>\d</c> (ASCII only).
    /// </summary>
    public const string StrictPattern =
        @"^VRChat_[0-9]{4}-[0-9]{2}-[0-9]{2}_[0-9]{2}-[0-9]{2}-[0-9]{2}\.[0-9]{3}_[0-9]+x[0-9]+(_(Environment|Player|UI)|_(wrld_[0-9a-f-]{36})?)?\.png$";

    [GeneratedRegex(StrictPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StrictRegex();

    /// <summary>True if the file name alone is acceptable (prefix, extension and, when <paramref name="strictNames"/>, the exact pattern).</summary>
    public static bool NameAccepted(string fileName, bool strictNames)
    {
        if (!fileName.StartsWith(RequiredPrefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(RequiredExtension, StringComparison.OrdinalIgnoreCase) ||
            fileName.Length <= RequiredPrefix.Length + RequiredExtension.Length)
            return false;
        return !strictNames || StrictRegex().IsMatch(fileName);
    }

    /// <summary>
    /// True if <paramref name="path"/> is one of the user's photos under <paramref name="root"/> that may be uploaded.
    /// Rejects files outside the root, in an excluded top-level folder, with the wrong name, or (if the file exists)
    /// marked hidden or temporary.
    /// </summary>
    public static bool Accepts(string root, string path, bool strictNames)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
            return false;

        string fullRoot, fullPath;
        try
        {
            fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!NameAccepted(Path.GetFileName(fullPath), strictNames))
            return false;

        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative == "." || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return false;

        var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        if (separator >= 0 && IsExcludedFolder(relative[..separator]))
            return false;

        return !IsHiddenOrTemp(fullPath);
    }

    /// <summary>True if <paramref name="folderName"/> is one of <see cref="ExcludedFolders"/> (ignoring case).</summary>
    public static bool IsExcludedFolder(string folderName)
    {
        foreach (var excluded in ExcludedFolders)
        {
            if (string.Equals(folderName, excluded, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Every accepted file under <paramref name="root"/>, the root itself included, walking subfolders recursively.
    /// The excluded top-level folders are skipped without being listed. Unreadable folders are skipped.
    /// A missing root gives nothing.
    /// </summary>
    public static IEnumerable<string> EnumerateCandidates(string root, bool strictNames = true)
    {
        string fullRoot;
        try
        {
            fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            yield break;
        }
        if (!Directory.Exists(fullRoot))
            yield break;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.Temporary,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();

            List<string> files, subdirs;
            try
            {
                files = Directory.EnumerateFiles(dir, "*", options).ToList();
                subdirs = Directory.EnumerateDirectories(dir, "*", options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            files.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (Accepts(fullRoot, file, strictNames))
                    yield return file;
            }

            subdirs.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = subdirs.Count - 1; i >= 0; i--)
            {
                var sub = subdirs[i];
                if (dir == fullRoot && IsExcludedFolder(Path.GetFileName(sub)))
                    continue;
                pending.Push(sub);
            }
        }
    }

    private static bool IsHiddenOrTemp(string fullPath)
    {
        try
        {
            var attributes = File.GetAttributes(fullPath);
            return (attributes & (FileAttributes.Hidden | FileAttributes.Temporary)) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // The file may already be gone (or not created yet); judge it by its name only.
            return false;
        }
    }
}
