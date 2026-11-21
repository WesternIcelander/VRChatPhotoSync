using System.Buffers.Binary;
using System.Drawing;

namespace VRChatPhotoSync.Tests;

public sealed class PngHelpersTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "PhotoSyncPngTests_" + Guid.NewGuid().ToString("N"));

    public PngHelpersTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string PathFor(string name) => Path.Combine(_dir, name);

    private string WriteBytes(string name, byte[] bytes)
    {
        var path = PathFor(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ---- ImageSize ----

    [Theory]
    [InlineData(7, 5)]
    [InlineData(1, 1)]
    [InlineData(64, 33)]
    public void ImageSize_ReadsGeneratedPng(int width, int height)
    {
        var path = PathFor("a.png");
        PngTestFiles.Write(path, width, height);

        Assert.True(ImageSize.TryRead(path, out var w, out var h));
        Assert.Equal((width, height), (w, h));
    }

    [Fact]
    public void ImageSize_NeedsOnlyTheHeader()
    {
        // 2048x1440 signature + IHDR only: nothing to decode, the size still comes out.
        var header = HeaderOnly(2048, 1440);
        var path = WriteBytes("header.png", header);

        Assert.True(ImageSize.TryRead(path, out var w, out var h));
        Assert.Equal((2048, 1440), (w, h));
    }

    [Fact]
    public void ImageSize_ReadsWhileAnotherWriterHasTheFileOpen()
    {
        var path = PathFor("open.png");
        PngTestFiles.Write(path, 9, 4);
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        Assert.True(ImageSize.TryRead(path, out var w, out var h));
        Assert.Equal((9, 4), (w, h));
    }

    [Fact]
    public void ImageSize_InvalidOrMissing_ReturnsFalse()
    {
        var png = PngTestFiles.Generate(5, 5);

        Assert.False(ImageSize.TryRead(PathFor("missing.png"), out _, out _));
        Assert.False(ImageSize.TryRead(WriteBytes("empty.png", []), out _, out _));
        Assert.False(ImageSize.TryRead(WriteBytes("short.png", png[..23]), out _, out _));
        Assert.False(ImageSize.TryRead(WriteBytes("text.png", "not a png at all, just some text"u8.ToArray()), out _, out _));
        Assert.False(ImageSize.TryRead(_dir, out _, out _)); // a directory

        var badSignature = (byte[])png.Clone();
        badSignature[1] = (byte)'X';
        Assert.False(ImageSize.TryRead(WriteBytes("sig.png", badSignature), out _, out _));

        var notIhdr = (byte[])png.Clone();
        notIhdr[12] = (byte)'X';
        Assert.False(ImageSize.TryRead(WriteBytes("ihdr.png", notIhdr), out _, out _));

        Assert.False(ImageSize.TryRead(WriteBytes("zero.png", HeaderOnly(0, 10)), out _, out _));
        Assert.False(ImageSize.TryRead(WriteBytes("huge.png", HeaderOnly(0x80000000u, 10)), out _, out _));
    }

    [Fact]
    public void ImageSize_ExactlyTheHeaderBytesIsEnough()
    {
        var path = WriteBytes("24.png", PngTestFiles.Generate(12, 3)[..24]);

        Assert.True(ImageSize.TryRead(path, out var w, out var h));
        Assert.Equal((12, 3), (w, h));
    }

    // ---- PngChunks ----

    [Fact]
    public void Crc32_MatchesKnownValue()
    {
        // The IEND chunk's CRC is always AE 42 60 82.
        Assert.Equal(0xAE426082u, PngChunks.Crc32("IEND"u8, []));
    }

    [Fact]
    public void PlainPng_IsAbsent()
    {
        var path = PathFor("plain.png");
        PngTestFiles.Write(path, 8, 8);

        Assert.Equal(VrcxMetadataStatus.Absent, PngChunks.CheckVrcxMetadata(path));
        Assert.False(PngChunks.HasVrcxMetadata(path));
    }

    [Fact]
    public void ITxtDescription_IsPresent()
    {
        var png = PngTestFiles.InsertChunk(PngTestFiles.Generate(8, 8), PngTestFiles.ITxtChunk("Description", """{"a":1}"""));
        var path = WriteBytes("itxt.png", png);

        Assert.Equal(VrcxMetadataStatus.Present, PngChunks.CheckVrcxMetadata(path));
        Assert.True(PngChunks.HasVrcxMetadata(path));
    }

    [Fact]
    public void TExtDescription_IsPresent()
    {
        var png = PngTestFiles.InsertChunk(PngTestFiles.Generate(8, 8), PngTestFiles.TExtChunk("Description", "hello"));

        Assert.True(PngChunks.HasVrcxMetadata(WriteBytes("text.png", png)));
    }

    [Fact]
    public void DescriptionAfterImageData_IsPresent()
    {
        var png = PngTestFiles.InsertChunk(PngTestFiles.Generate(8, 8), PngTestFiles.ITxtChunk("Description", "{}"), beforeType: "IEND");

        Assert.True(PngChunks.HasVrcxMetadata(WriteBytes("late.png", png)));
    }

    [Fact]
    public void DescriptionAfterOtherTextChunks_IsPresent()
    {
        // VRChat's own XMP chunk first, as on real photos, then VRCX's.
        var png = PngTestFiles.Generate(8, 8);
        png = PngTestFiles.InsertChunk(png, PngTestFiles.ITxtChunk("XML:com.adobe.xmp", "<x:xmpmeta/>"));
        png = PngTestFiles.InsertChunk(png, PngTestFiles.TExtChunk("Software", "test"));
        png = PngTestFiles.InsertChunk(png, PngTestFiles.ITxtChunk("Description", "{}"));

        Assert.True(PngChunks.HasVrcxMetadata(WriteBytes("both.png", png)));
    }

    [Theory]
    [InlineData("XML:com.adobe.xmp")]
    [InlineData("description")]
    [InlineData("Descriptions")]
    [InlineData("Comment")]
    public void OtherKeywords_AreAbsent(string keyword)
    {
        var png = PngTestFiles.InsertChunk(PngTestFiles.Generate(8, 8), PngTestFiles.ITxtChunk(keyword, "Description"));

        Assert.Equal(VrcxMetadataStatus.Absent, PngChunks.CheckVrcxMetadata(WriteBytes("other.png", png)));
    }

    [Fact]
    public void DescriptionInANonTextChunk_IsAbsent()
    {
        var png = PngTestFiles.InsertChunk(PngTestFiles.Generate(8, 8), PngTestFiles.Chunk("zTXt", "Description\0\0x"u8.ToArray()));

        Assert.Equal(VrcxMetadataStatus.Absent, PngChunks.CheckVrcxMetadata(WriteBytes("ztxt.png", png)));
    }

    [Fact]
    public void EveryTruncation_IsNotReady_WithoutThrowing()
    {
        var png = PngTestFiles.InsertChunk(PngTestFiles.Generate(6, 6), PngTestFiles.ITxtChunk("Description", """{"x":"y"}"""));
        var path = PathFor("cut.png");
        Assert.True(PngChunks.HasVrcxMetadata(WriteBytes("cut.png", png)));

        for (var length = 0; length < png.Length; length++)
        {
            File.WriteAllBytes(path, png[..length]);
            Assert.Equal(VrcxMetadataStatus.NotReady, PngChunks.CheckVrcxMetadata(path));
        }
    }

    [Fact]
    public void CorruptDescriptionChunk_IsNotReady()
    {
        // A half-written chunk: the CRC doesn't match the data.
        var original = PngTestFiles.Generate(8, 8);
        var png = PngTestFiles.InsertChunk(original, PngTestFiles.ITxtChunk("Description", """{"x":"y"}"""));
        var offset = PngTestFiles.FindChunk(png, "iTXt");
        png[offset + 8 + 18] ^= 0xFF; // a byte of the JSON text (after "Description" + 5 separator/flag bytes)

        Assert.Equal(VrcxMetadataStatus.NotReady, PngChunks.CheckVrcxMetadata(WriteBytes("crc.png", png)));
    }

    [Fact]
    public void BrokenStructure_IsNotReady()
    {
        var png = PngTestFiles.Generate(8, 8);

        var badSignature = (byte[])png.Clone();
        badSignature[0] = 0;
        Assert.Equal(VrcxMetadataStatus.NotReady, PngChunks.CheckVrcxMetadata(WriteBytes("sig.png", badSignature)));

        // A chunk length pointing past the end of the file.
        var badLength = (byte[])png.Clone();
        var idat = PngTestFiles.FindChunk(badLength, "IDAT");
        BinaryPrimitives.WriteUInt32BigEndian(badLength.AsSpan(idat), 0x7FFFFFF0);
        Assert.Equal(VrcxMetadataStatus.NotReady, PngChunks.CheckVrcxMetadata(WriteBytes("len.png", badLength)));

        // A length with the high bit set (invalid in PNG).
        BinaryPrimitives.WriteUInt32BigEndian(badLength.AsSpan(idat), 0x80000000);
        Assert.Equal(VrcxMetadataStatus.NotReady, PngChunks.CheckVrcxMetadata(WriteBytes("len2.png", badLength)));

        // A chunk type that isn't four letters (e.g. garbage left by a rewrite).
        var badType = (byte[])png.Clone();
        badType[idat + 5] = (byte)'1';
        Assert.Equal(VrcxMetadataStatus.NotReady, PngChunks.CheckVrcxMetadata(WriteBytes("type.png", badType)));

        // First chunk isn't IHDR.
        var noIhdr = (byte[])png.Clone();
        noIhdr[12] = (byte)'X';
        Assert.Equal(VrcxMetadataStatus.NotReady, PngChunks.CheckVrcxMetadata(WriteBytes("ihdr.png", noIhdr)));

        // No IEND (file ends cleanly after the last IDAT).
        var noIend = png[..PngTestFiles.FindChunk(png, "IEND")];
        Assert.Equal(VrcxMetadataStatus.NotReady, PngChunks.CheckVrcxMetadata(WriteBytes("noiend.png", noIend)));
    }

    [Fact]
    public void TextChunkWithoutKeywordTerminator_IsNotReady()
    {
        var png = PngTestFiles.InsertChunk(PngTestFiles.Generate(8, 8), PngTestFiles.Chunk("iTXt", "Description"u8.ToArray()));

        Assert.Equal(VrcxMetadataStatus.NotReady, PngChunks.CheckVrcxMetadata(WriteBytes("nokw.png", png)));
    }

    [Fact]
    public void MissingFileOrDirectory_IsNotReady()
    {
        Assert.Equal(VrcxMetadataStatus.NotReady, PngChunks.CheckVrcxMetadata(PathFor("missing.png")));
        Assert.Equal(VrcxMetadataStatus.NotReady, PngChunks.CheckVrcxMetadata(_dir));
        Assert.Equal(VrcxMetadataStatus.NotReady, PngChunks.CheckVrcxMetadata(WriteBytes("empty.png", [])));
        Assert.False(PngChunks.HasVrcxMetadata(""));
    }

    [Fact]
    public void FileLockedExclusively_IsNotReady()
    {
        var path = PathFor("locked.png");
        PngTestFiles.Write(path, 8, 8);
        using var locker = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(VrcxMetadataStatus.NotReady, PngChunks.CheckVrcxMetadata(path));
    }

    [Fact]
    public void ReadsWhileAWriterHasTheFileOpen_AndDoesNotBlockDeleteOrWrite()
    {
        var path = PathFor("shared.png");
        PngTestFiles.Write(path, 8, 8);
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            Assert.Equal(VrcxMetadataStatus.Absent, PngChunks.CheckVrcxMetadata(path));

        // The helper's own handle must let VRCX keep writing (it opens with FileShare.ReadWrite).
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            PngTestFiles.InsertDescriptionChunk(path);
            Assert.True(PngChunks.HasVrcxMetadata(path));
        }
    }

    // ---- Test helper ----

    [Fact]
    public void InsertDescriptionChunk_WorksLikeVrcx()
    {
        var path = PathFor("vrcx.png");
        var original = PngTestFiles.Write(path, 16, 9, seed: 7);
        Assert.False(PngChunks.HasVrcxMetadata(path));

        var json = """{"application":"VRCX","world":{"name":"Example"}}""";
        PngTestFiles.InsertDescriptionChunk(path, json);

        Assert.True(PngChunks.HasVrcxMetadata(path));
        var bytes = File.ReadAllBytes(path);
        var chunk = PngTestFiles.ITxtChunk("Description", json);
        Assert.Equal(original.Length + chunk.Length, bytes.Length);

        // Inserted right before the first IDAT; everything else unchanged.
        var idat = PngTestFiles.FindChunk(original, "IDAT");
        Assert.Equal(original[..idat], bytes[..idat]);
        Assert.Equal(chunk, bytes[idat..(idat + chunk.Length)]);
        Assert.Equal(original[idat..], bytes[(idat + chunk.Length)..]);

        // Still a valid image with the same size and pixels.
        Assert.True(ImageSize.TryRead(path, out var w, out var h));
        Assert.Equal((16, 9), (w, h));
        using var before = new Bitmap(new MemoryStream(original));
        using var after = new Bitmap(path);
        Assert.Equal(before.GetPixel(3, 4), after.GetPixel(3, 4));
        Assert.Equal(before.GetPixel(15, 8), after.GetPixel(15, 8));
    }

    private static byte[] HeaderOnly(uint width, uint height)
    {
        // Signature + IHDR (13 data bytes + CRC) + IEND: a header with no image data.
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // RGB
        return [.. PngChunks.Signature, .. PngTestFiles.Chunk("IHDR", ihdr), .. PngTestFiles.Chunk("IEND", [])];
    }
}
