namespace Foliant;

/// <summary>
/// A rendered page image as a raw BGRA8888 pixel buffer (stride = Width * 4).
/// Keeps Foliant.Core free of any imaging-library dependency; backends convert
/// to their preferred representation.
/// </summary>
public sealed class PageImage
{
    public PageImage(int width, int height, int dpi, byte[] pixelsBgra8888)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(dpi, 1);
        ArgumentNullException.ThrowIfNull(pixelsBgra8888);
        if (pixelsBgra8888.Length != (long)width * height * 4)
            throw new ArgumentException(
                $"Pixel buffer length {pixelsBgra8888.Length} does not match {width}x{height}x4.",
                nameof(pixelsBgra8888));

        Width = width;
        Height = height;
        Dpi = dpi;
        PixelsBgra8888 = pixelsBgra8888;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The DPI the page was rendered at; maps raster pixels back to PDF points (72/inch).</summary>
    public int Dpi { get; }

    /// <summary>Row-major BGRA bytes, 4 per pixel, no row padding.</summary>
    public byte[] PixelsBgra8888 { get; }
}
