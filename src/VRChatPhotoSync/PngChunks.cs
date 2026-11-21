using System.Buffers.Binary;
using System.Text;

namespace VRChatPhotoSync;

/// <summary>What <see cref="PngChunks.CheckVrcxMetadata"/> found.</summary>
public enum VrcxMetadataStatus
{
    /// <summary>The file is missing, locked, truncated, not a PNG, or its chunk list is broken (for example mid-rewrite).</summary>
    NotReady,

    /// <summary>A complete, well-formed PNG without a <c>Description</c> text chunk.</summary>
    Absent,

    /// <summary>A complete, well-formed PNG with an <c>iTXt</c> or <c>tEXt</c> chunk whose keyword is <c>Description</c>.</summary>
    Present,
}

/// <summary>
/// Reads a PNG's chunk list (only the chunk headers, seeking past image data) to find the metadata VRCX adds:
/// an <c>iTXt</c> chunk with the keyword <c>Description</c> (a <c>tEXt</c> chunk with that keyword also counts).
/// </summary>
public static class PngChunks
{
    /// <summary>The 8-byte PNG file signature.</summary>
    public static ReadOnlySpan<byte> Signature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The keyword VRCX writes its JSON metadata under.</summary>
    public const string VrcxKeyword = "Description";

    // Text chunks up to this size are read whole and CRC-checked; larger ones only have their keyword read.
    private const int MaxCheckedTextChunk = 1 << 20;

    // PNG keywords are 1–79 Latin-1 bytes followed by a null separator.
    private const int MaxKeywordBytes = 79;

    /// <summary>
    /// True only when the file is a complete PNG (chunk list intact up to <c>IEND</c>) with a <c>Description</c>
    /// text chunk. A missing, locked, truncated or invalid file gives false ("not ready") and never throws.
    /// </summary>
    public static bool HasVrcxMetadata(string path) => CheckVrcxMetadata(path) == VrcxMetadataStatus.Present;

    /// <summary>
    /// Walks the chunk list and reports whether VRCX's <c>Description</c> chunk is present. Never throws.
    /// The file is opened read-only with <c>FileShare.ReadWrite | FileShare.Delete</c>, because VRCX rewrites it in place.
    /// </summary>
    public static VrcxMetadataStatus CheckVrcxMetadata(string path)
    {
        try
        {
            using var stream = OpenShared(path);
            return Check(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return VrcxMetadataStatus.NotReady;
        }
    }

    /// <summary>Opens a file read-only, letting others keep writing, renaming or deleting it.</summary>
    internal static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096);

    private static VrcxMetadataStatus Check(Stream stream)
    {
        Span<byte> header = stackalloc byte[8];
        if (!TryReadExactly(stream, header) || !header.SequenceEqual(Signature))
            return VrcxMetadataStatus.NotReady;

        var length = stream.Length;
        long position = 8;
        var first = true;
        var found = false;
        while (true)
        {
            if (!TryReadExactly(stream, header))
                return VrcxMetadataStatus.NotReady;
            var dataLength = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = header[4..8];
            if (dataLength > int.MaxValue || !IsValidType(type))
                return VrcxMetadataStatus.NotReady;
            if (first && (!type.SequenceEqual("IHDR"u8) || dataLength != 13))
                return VrcxMetadataStatus.NotReady;
            first = false;

            var chunkEnd = position + 8 + dataLength + 4;
            if (chunkEnd > length)
                return VrcxMetadataStatus.NotReady;

            if (type.SequenceEqual("IEND"u8))
                return found ? VrcxMetadataStatus.Present : VrcxMetadataStatus.Absent;

            if (!found && (type.SequenceEqual("iTXt"u8) || type.SequenceEqual("tEXt"u8)))
            {
                var keyword = ReadTextChunkKeyword(stream, type, (int)dataLength);
                if (keyword is null)
                    return VrcxMetadataStatus.NotReady;
                found = keyword == VrcxKeyword;
            }

            stream.Position = position = chunkEnd;
        }
    }

    /// <summary>
    /// Reads a text chunk's keyword (the stream is positioned at its data). Small chunks are read whole and their
    /// CRC is checked, so a half-written chunk isn't trusted. Returns null when the chunk is broken.
    /// </summary>
    private static string? ReadTextChunkKeyword(Stream stream, ReadOnlySpan<byte> type, int dataLength)
    {
        byte[] data;
        if (dataLength <= MaxCheckedTextChunk)
        {
            data = new byte[dataLength + 4];
            if (!TryReadExactly(stream, data))
                return null;
            var storedCrc = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(dataLength));
            if (storedCrc != Crc32(type, data.AsSpan(0, dataLength)))
                return null;
            data = data[..dataLength];
        }
        else
        {
            data = new byte[MaxKeywordBytes + 1];
            if (!TryReadExactly(stream, data))
                return null;
        }

        var end = Array.IndexOf(data, (byte)0);
        if (end is < 1 or > MaxKeywordBytes)
            return null;
        return Encoding.Latin1.GetString(data, 0, end);
    }

    // Chunk types are four ASCII letters.
    private static bool IsValidType(ReadOnlySpan<byte> type)
    {
        foreach (var b in type)
        {
            if (b is not (>= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'))
                return false;
        }
        return true;
    }

    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
                return false;
            total += read;
        }
        return true;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    /// <summary>The PNG chunk CRC (CRC-32 over the chunk type followed by the chunk data).</summary>
    public static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in type)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        foreach (var b in data)
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
