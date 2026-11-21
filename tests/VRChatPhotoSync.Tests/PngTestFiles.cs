using System.Buffers.Binary;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;

namespace VRChatPhotoSync.Tests;

/// <summary>
/// Generates PNG fixtures and adds VRCX-style metadata to them. Used by the PNG helper tests and, later,
/// to simulate VRCX writing its <c>Description</c> chunk into a photo the app is watching.
/// </summary>
public static class PngTestFiles
{
    /// <summary>A real PNG (encoded by GDI+) of the given size, filled with seeded pseudo-random pixels.</summary>
    public static byte[] Generate(int width, int height, int seed = 1)
    {
        var random = new Random(seed);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.SetPixel(x, y, Color.FromArgb(random.Next(256), random.Next(256), random.Next(256)));
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    /// <summary>Writes a generated PNG to <paramref name="path"/> and returns its bytes.</summary>
    public static byte[] Write(string path, int width, int height, int seed = 1)
    {
        var bytes = Generate(width, height, seed);
        File.WriteAllBytes(path, bytes);
        return bytes;
    }

    /// <summary>A complete chunk (length, type, data, CRC).</summary>
    public static byte[] Chunk(string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        typeBytes.CopyTo(chunk, 4);
        data.CopyTo(chunk, 8);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), PngChunks.Crc32(typeBytes, data));
        return chunk;
    }

    /// <summary>An uncompressed <c>iTXt</c> chunk (as VRCX writes it): keyword, flags 0/0, empty language and translated keyword, UTF-8 text.</summary>
    public static byte[] ITxtChunk(string keyword, string text)
    {
        var data = new List<byte>();
        data.AddRange(Encoding.Latin1.GetBytes(keyword));
        data.AddRange(new byte[] { 0, 0, 0, 0, 0 }); // null, compression flag, compression method, language null, translated keyword null
        data.AddRange(Encoding.UTF8.GetBytes(text));
        return Chunk("iTXt", [.. data]);
    }

    /// <summary>A <c>tEXt</c> chunk: keyword, null, Latin-1 text.</summary>
    public static byte[] TExtChunk(string keyword, string text) =>
        Chunk("tEXt", [.. Encoding.Latin1.GetBytes(keyword), 0, .. Encoding.Latin1.GetBytes(text)]);

    /// <summary>The byte offset of the first chunk of the given type (its length field), or -1.</summary>
    public static int FindChunk(byte[] png, string type)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        var position = 8;
        while (position + 8 <= png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(position));
            if (png.AsSpan(position + 4, 4).SequenceEqual(typeBytes))
                return position;
            position += 12 + length;
        }
        return -1;
    }

    /// <summary>Returns a copy of <paramref name="png"/> with <paramref name="chunk"/> inserted before the first chunk of type <paramref name="beforeType"/>.</summary>
    public static byte[] InsertChunk(byte[] png, byte[] chunk, string beforeType = "IDAT")
    {
        var offset = FindChunk(png, beforeType);
        if (offset < 0)
            throw new ArgumentException($"No {beforeType} chunk.", nameof(png));
        return [.. png.AsSpan(0, offset), .. chunk, .. png.AsSpan(offset)];
    }

    /// <summary>
    /// Adds an <c>iTXt</c> <c>Description</c> chunk to the file the way VRCX does (<c>PNGFile.WriteChunk</c>):
    /// in place, before the first <c>IDAT</c>, opened with <c>FileShare.ReadWrite</c>, buffering the tail,
    /// truncating with <c>SetLength</c>, then writing the chunk and the tail back. No temp file.
    /// </summary>
    public static void InsertDescriptionChunk(string path, string json = """{"application":"VRCX","version":1}""")
    {
        var chunk = ITxtChunk(PngChunks.VrcxKeyword, json);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var original = new byte[stream.Length];
        stream.ReadExactly(original);
        var offset = FindChunk(original, "IDAT");
        if (offset < 0)
            throw new InvalidDataException("No IDAT chunk.");
        stream.SetLength(offset);
        stream.Position = offset;
        stream.Write(chunk);
        stream.Write(original, offset, original.Length - offset);
    }
}
