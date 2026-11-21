using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VRChatPhotoSync.Tests;

public sealed class PrintDetectorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("photosync-print-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private const string PrintName = "VRChat_2026-11-04_01-48-06.885_2048x1440.png";

    [Fact]
    public void Layout_matches_the_measured_mask()
    {
        Assert.Equal(new Rectangle(64, 69, 1920, 1080), PrintLayout.Photo);
        Assert.Equal(new Rectangle(0, 1159, 2048, 277), PrintLayout.Caption);
        Assert.Equal(308_224, PrintLayout.BorderPixelCount);
        Assert.Equal(308, PrintDetector.MaxNonWhiteBorderPixels);

        Assert.True(PrintLayout.IsBorder(0, 0));
        Assert.True(PrintLayout.IsBorder(63, 69));
        Assert.False(PrintLayout.IsBorder(64, 69));
        Assert.False(PrintLayout.IsBorder(1983, 1148));
        Assert.True(PrintLayout.IsBorder(1984, 1148));
        Assert.True(PrintLayout.IsBorder(64, 1149));
        Assert.False(PrintLayout.IsBorder(0, 1159));
        Assert.False(PrintLayout.IsBorder(2047, 1435));
        Assert.True(PrintLayout.IsBorder(2047, 1436));
        Assert.False(PrintLayout.IsBorder(2048, 0));
    }

    [Fact]
    public void Made_up_print_is_a_print()
    {
        var path = WriteFixture(PrintName, border: 255);
        Assert.True(PrintDetector.IsPrint(path));
    }

    [Fact]
    public void Print_without_size_in_name_is_detected_from_the_header()
    {
        var path = WriteFixture("print.png", border: 255);
        Assert.True(PrintDetector.IsPrint(path));
    }

    [Fact]
    public void Slightly_off_white_border_at_the_threshold_is_a_print()
    {
        var path = WriteFixture(PrintName, border: PrintDetector.WhiteThreshold);
        Assert.True(PrintDetector.IsPrint(path));
    }

    [Fact]
    public void Full_canvas_noise_is_not_a_print()
    {
        var path = WriteFixture(PrintName, border: null);
        Assert.False(PrintDetector.IsPrint(path));
    }

    [Fact]
    public void Tinted_border_is_not_a_print()
    {
        var path = WriteFixture(PrintName, border: 230);
        Assert.False(PrintDetector.IsPrint(path));
    }

    [Fact]
    public void Border_with_one_tinted_channel_is_not_a_print()
    {
        // Only blue is below the threshold: the minimum channel decides.
        var path = WriteFixture(PrintName, border: 255, edit: (pixels, stride) =>
        {
            for (var y = 0; y < PrintLayout.Height; y++)
                for (var x = 0; x < PrintLayout.Width; x++)
                    if (PrintLayout.IsBorder(x, y))
                        pixels[y * stride + x * 3] = 200; // BGR: B
        });
        Assert.False(PrintDetector.IsPrint(path));
    }

    [Fact]
    public void A_few_stray_border_pixels_are_still_a_print()
    {
        var stray = new[] { (0, 0), (10, 5), (2047, 0), (30, 600), (2000, 1100), (1000, 1150), (5, 1439), (2047, 1439) };
        var path = WriteFixture(PrintName, border: 255, edit: (pixels, stride) =>
        {
            foreach (var (x, y) in stray)
            {
                Assert.True(PrintLayout.IsBorder(x, y));
                pixels.AsSpan(y * stride + x * 3, 3).Fill(0);
            }
        });
        Assert.True(PrintDetector.IsPrint(path));
    }

    [Fact]
    public void Non_white_pixels_up_to_the_limit_are_a_print_and_one_more_is_not()
    {
        var limit = PrintDetector.MaxNonWhiteBorderPixels;
        Assert.True(PrintDetector.IsPrint(WriteFixture("at-limit.png", border: 255, edit: DarkenTopRow(limit))));
        Assert.False(PrintDetector.IsPrint(WriteFixture("over-limit.png", border: 255, edit: DarkenTopRow(limit + 1))));
    }

    [Fact]
    public void Other_size_in_the_name_is_not_a_print_without_decoding()
    {
        // Not an image at all: decoding would throw, so returning false proves nothing was decoded.
        var path = Path.Combine(_dir, "VRChat_2026-11-04_01-48-06.885_1920x1080.png");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        Assert.False(PrintDetector.IsPrint(path));
    }

    [Fact]
    public void Other_size_in_the_header_is_not_a_print_without_decoding()
    {
        // Only the signature and IHDR (cut off after the size): decoding would fail.
        var header = PngTestFiles.Generate(1920, 1080)[..24];
        var path = Path.Combine(_dir, "photo.png");
        File.WriteAllBytes(path, header);
        Assert.False(PrintDetector.IsPrint(path));
    }

    [Fact]
    public void Real_1920x1080_image_is_not_a_print()
    {
        var path = Path.Combine(_dir, "VRChat_2026-11-04_01-48-06.885_1920x1080.png");
        File.WriteAllBytes(path, PngTestFiles.Generate(1920, 1080));
        Assert.False(PrintDetector.IsPrint(path));
    }

    [Fact]
    public void Unreadable_size_is_not_a_print()
    {
        var path = Path.Combine(_dir, "notes.png");
        File.WriteAllText(path, "not a png");
        Assert.False(PrintDetector.IsPrint(path));
        Assert.False(PrintDetector.IsPrint(Path.Combine(_dir, "missing.png")));
    }

    [Fact]
    public void Name_says_print_but_image_is_another_size_is_not_a_print()
    {
        var path = Path.Combine(_dir, PrintName);
        using (var bitmap = new Bitmap(64, 64, PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(bitmap))
                g.Clear(Color.White);
            bitmap.Save(path, ImageFormat.Png);
        }
        Assert.False(PrintDetector.IsPrint(path));
    }

    [Fact]
    public void File_stays_writable_and_deletable_after_the_check()
    {
        var path = WriteFixture(PrintName, border: 255);
        Assert.True(PrintDetector.IsPrint(path));
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    private static Action<byte[], int> DarkenTopRow(int count) => (pixels, stride) =>
    {
        for (var x = 0; x < count; x++)
            pixels.AsSpan(x * 3, 3).Fill(100); // row 0 is all border
    };

    /// <summary>
    /// Writes a 2048×1440 PNG: noise in the photo area and caption strip, and the border filled with grey level
    /// <paramref name="border"/> (null = noise there too). <paramref name="edit"/> can change the BGR pixels before encoding.
    /// </summary>
    private string WriteFixture(string name, int? border, Action<byte[], int>? edit = null)
    {
        const int width = PrintLayout.Width, height = PrintLayout.Height;
        var stride = width * 3;
        var pixels = new byte[stride * height];
        var random = new Random(42);
        random.NextBytes(pixels);
        if (border is { } level)
        {
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    if (PrintLayout.IsBorder(x, y))
                        pixels.AsSpan(y * stride + x * 3, 3).Fill((byte)level);
        }
        edit?.Invoke(pixels, stride);

        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (var y = 0; y < height; y++)
                Marshal.Copy(pixels, y * stride, data.Scan0 + y * data.Stride, stride);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
        var path = Path.Combine(_dir, name);
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }
}
