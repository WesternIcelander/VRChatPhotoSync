using System.Buffers.Binary;

namespace VRChatPhotoSync;

/// <summary>Reads a PNG's width and height from its <c>IHDR</c> header without decoding the image.</summary>
public static class ImageSize
{
    // 8-byte signature, then the IHDR chunk: length (4) + "IHDR" (4) + width (4) + height (4).
    private const int HeaderBytes = 24;

    /// <summary>
    /// Reads the size from the PNG header (bytes 16–23). Returns false, without throwing, when the file is
    /// missing, can't be opened, is too short, or doesn't start with a PNG signature and an <c>IHDR</c> chunk.
    /// The file is opened read-only and shared with writers and deleters (VRChat/VRCX may still have it open).
    /// </summary>
    public static bool TryRead(string path, out int width, out int height)
    {
        width = height = 0;
        Span<byte> header = stackalloc byte[HeaderBytes];
        try
        {
            using var stream = PngChunks.OpenShared(path);
            stream.ReadExactly(header);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // EndOfStreamException (too short) is an IOException.
            return false;
        }
        return TryParse(header, out width, out height);
    }

    /// <summary>Parses the size from the first 24 bytes of a PNG (signature + start of <c>IHDR</c>).</summary>
    public static bool TryParse(ReadOnlySpan<byte> pngStart, out int width, out int height)
    {
        width = height = 0;
        if (pngStart.Length < HeaderBytes || !pngStart[..8].SequenceEqual(PngChunks.Signature))
            return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(pngStart[8..]) != 13 || !pngStart[12..16].SequenceEqual("IHDR"u8))
            return false;
        var w = BinaryPrimitives.ReadUInt32BigEndian(pngStart[16..]);
        var h = BinaryPrimitives.ReadUInt32BigEndian(pngStart[20..]);
        // The PNG spec limits both to 1 .. 2^31-1.
        if (w is 0 or > int.MaxValue || h is 0 or > int.MaxValue)
            return false;
        width = (int)w;
        height = (int)h;
        return true;
    }
}
