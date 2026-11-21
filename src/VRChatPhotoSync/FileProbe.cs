namespace VRChatPhotoSync;

/// <summary>A file's size and times at one moment.</summary>
public readonly record struct FileStat(long Length, DateTime LastWriteUtc, DateTime CreationUtc)
{
    /// <summary>The later of the creation and last-write times (a copied-in file keeps an old write time but gets a new creation time).</summary>
    public DateTime NewestUtc => CreationUtc > LastWriteUtc ? CreationUtc : LastWriteUtc;

    /// <summary>True when size and last-write time match (creation time is ignored).</summary>
    public bool SameContentAs(FileStat other) => Length == other.Length && LastWriteUtc == other.LastWriteUtc;
}

/// <summary>
/// The file-system questions <see cref="FileReadiness"/> and <see cref="VrcxSettle"/> ask.
/// Injectable so tests can script a file's life without touching the disk.
/// </summary>
public interface IFileProbe
{
    /// <summary>Size and times, or null when the file doesn't exist (or can't be read).</summary>
    FileStat? TryStat(string path);

    /// <summary>True when the file exists.</summary>
    bool Exists(string path);

    /// <summary>True when the file can be opened for reading while denying other writers (no one is still writing it).</summary>
    bool CanOpen(string path);

    /// <summary>Whether VRCX's <c>Description</c> chunk is present (see <see cref="PngChunks.CheckVrcxMetadata"/>).</summary>
    VrcxMetadataStatus CheckVrcxMetadata(string path);
}

/// <summary>The real file system.</summary>
public sealed class FileProbe : IFileProbe
{
    public static readonly FileProbe Instance = new();

    public FileStat? TryStat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;
            return new FileStat(info.Length, info.LastWriteTimeUtc, info.CreationTimeUtc);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public bool Exists(string path) => File.Exists(path);

    public bool CanOpen(string path)
    {
        try
        {
            // FileShare.Read (no Write) fails while another process still has the file open for writing.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, bufferSize: 1);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public VrcxMetadataStatus CheckVrcxMetadata(string path) => PngChunks.CheckVrcxMetadata(path);
}

/// <summary>Current time and waiting, injectable so tests can run on a fake clock.</summary>
public interface IClock
{
    DateTime UtcNow { get; }

    Task Delay(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>The real clock.</summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTime UtcNow => DateTime.UtcNow;

    public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}
