using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace VRChatPhotoSync;

/// <summary>
/// The layout of a VRChat print (a polaroid-style image), taken from <c>docs/print_mask.png</c>.
/// Only the photo area and the caption strip can hold pixels other than white; everything else is the border.
/// If VRChat changes the layout, these constants are the only thing to update.
/// </summary>
public static class PrintLayout
{
    /// <summary>Width of every print, in pixels.</summary>
    public const int Width = 2048;

    /// <summary>Height of every print, in pixels.</summary>
    public const int Height = 1440;

    /// <summary>The photo area: (64,69)–(1983,1148) inclusive, 1920×1080.</summary>
    public static readonly Rectangle Photo = FromInclusive(64, 69, 1983, 1148);

    /// <summary>The caption strip ("Captured By …", date, world): (0,1159)–(2047,1435) inclusive.</summary>
    public static readonly Rectangle Caption = FromInclusive(0, 1159, 2047, 1435);

    /// <summary>The areas that are not border.</summary>
    public static IReadOnlyList<Rectangle> NonBorder { get; } = [Photo, Caption];

    /// <summary>Number of border pixels (everything outside <see cref="NonBorder"/>; the areas don't overlap): 308,224.</summary>
    public static int BorderPixelCount { get; } = Width * Height - Photo.Width * Photo.Height - Caption.Width * Caption.Height;

    /// <summary>Whether (x, y) is part of the white border.</summary>
    public static bool IsBorder(int x, int y)
    {
        if ((uint)x >= Width || (uint)y >= Height)
            return false;
        foreach (var area in NonBorder)
        {
            if (area.Contains(x, y))
                return false;
        }
        return true;
    }

    private static Rectangle FromInclusive(int left, int top, int right, int bottom) =>
        Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
}

/// <summary>Decides whether an image is a VRChat print, so prints can be skipped.</summary>
public static class PrintDetector
{
    /// <summary>A border pixel counts as white when every colour channel is at least this.</summary>
    public const int WhiteThreshold = 240;

    /// <summary>The share of border pixels that must be white for the image to be a print.</summary>
    public const double RequiredWhiteFraction = 0.999;

    /// <summary>How many border pixels may be non-white before the image stops being a print (308 of 308,224).</summary>
    public static int MaxNonWhiteBorderPixels { get; } =
        (int)Math.Floor(PrintLayout.BorderPixelCount * (1 - RequiredWhiteFraction) + 1e-9);

    /// <summary>
    /// True when the file is a print: it is 2048×1440 and at least 99.9% of its border pixels have every channel ≥ 240.
    /// The size comes from the name (<c>_2048x1440</c>) or, if the name has none, from the PNG header; any other size
    /// returns false without decoding the image. Only a 2048×1440 candidate is decoded.
    /// </summary>
    /// <exception cref="IOException">The file can't be read.</exception>
    /// <exception cref="ArgumentException">The image can't be decoded.</exception>
    public static bool IsPrint(string path)
    {
        if (!HasPrintSize(path))
            return false;

        // Decode from memory, so the file isn't held open (Bitmap keeps its stream) and VRCX can keep rewriting it.
        byte[] bytes;
        using (var stream = PngChunks.OpenShared(path))
        {
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        using var memory = new MemoryStream(bytes, writable: false);
        using var bitmap = new Bitmap(memory);
        return IsPrint(bitmap);
    }

    /// <summary>The pixel check on an already decoded image (must be exactly 2048×1440).</summary>
    public static bool IsPrint(Bitmap bitmap)
    {
        if (bitmap.Width != PrintLayout.Width || bitmap.Height != PrintLayout.Height)
            return false;

        var data = bitmap.LockBits(new Rectangle(0, 0, PrintLayout.Width, PrintLayout.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[PrintLayout.Width * 4];
            var nonWhite = 0;
            var limit = MaxNonWhiteBorderPixels;
            var gaps = new List<(int Start, int End)>(3);
            for (var y = 0; y < PrintLayout.Height; y++)
            {
                BorderRuns(y, gaps);
                if (gaps.Count == 0)
                    continue;
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                foreach (var (start, end) in gaps)
                {
                    for (var x = start; x < end; x++)
                    {
                        var i = x * 4; // B, G, R, A
                        if (row[i] < WhiteThreshold || row[i + 1] < WhiteThreshold || row[i + 2] < WhiteThreshold)
                        {
                            if (++nonWhite > limit)
                                return false; // early exit: can no longer reach 99.9%
                        }
                    }
                }
            }
            return true;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    /// <summary>True when the name (or, if the name has no size, the PNG header) says 2048×1440.</summary>
    internal static bool HasPrintSize(string path)
    {
        if (VrcFileName.TryParseSize(path, out var width, out var height) ||
            ImageSize.TryRead(path, out width, out height))
            return width == PrintLayout.Width && height == PrintLayout.Height;
        return false;
    }

    /// <summary>The half-open x ranges of border pixels in row <paramref name="y"/>.</summary>
    private static void BorderRuns(int y, List<(int Start, int End)> runs)
    {
        runs.Clear();
        var x = 0;
        foreach (var area in PrintLayout.NonBorder.Where(a => y >= a.Top && y < a.Bottom).OrderBy(a => a.Left))
        {
            if (area.Left > x)
                runs.Add((x, area.Left));
            x = Math.Max(x, area.Right);
        }
        if (x < PrintLayout.Width)
            runs.Add((x, PrintLayout.Width));
    }
}
