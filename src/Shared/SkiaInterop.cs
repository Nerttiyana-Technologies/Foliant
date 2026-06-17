// Shared source file, compile-linked into each package that needs SkiaSharp interop.
// Internal on purpose: SKBitmap never appears on any public Foliant surface.

using System.Runtime.InteropServices;
using SkiaSharp;

namespace Foliant.Internal;

internal static class SkiaInterop
{
    /// <summary>Copies a <see cref="PageImage"/> into a BGRA8888 <see cref="SKBitmap"/>. Caller disposes.</summary>
    public static SKBitmap ToBitmap(PageImage page)
    {
        var info = new SKImageInfo(page.Width, page.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var bitmap = new SKBitmap(info);
        nint dst = bitmap.GetPixels();
        int srcRowBytes = page.Width * 4;
        int dstRowBytes = bitmap.RowBytes;

        if (dstRowBytes == srcRowBytes)
        {
            Marshal.Copy(page.PixelsBgra8888, 0, dst, srcRowBytes * page.Height);
        }
        else
        {
            for (int y = 0; y < page.Height; y++)
                Marshal.Copy(page.PixelsBgra8888, y * srcRowBytes, dst + (nint)y * dstRowBytes, srcRowBytes);
        }

        bitmap.NotifyPixelsChanged();
        return bitmap;
    }

    /// <summary>Copies an <see cref="SKBitmap"/> into a <see cref="PageImage"/> (converting to BGRA8888 if needed).</summary>
    public static PageImage ToPageImage(SKBitmap bitmap, int dpi)
    {
        SKBitmap source = bitmap;
        SKBitmap? converted = null;
        try
        {
            if (bitmap.ColorType != SKColorType.Bgra8888)
            {
                converted = bitmap.Copy(SKColorType.Bgra8888)
                    ?? throw new InvalidOperationException(
                        $"Cannot convert bitmap from {bitmap.ColorType} to BGRA8888.");
                source = converted;
            }

            int width = source.Width, height = source.Height;
            var buffer = new byte[width * height * 4];
            nint src = source.GetPixels();
            int srcRowBytes = source.RowBytes;
            int dstRowBytes = width * 4;

            if (srcRowBytes == dstRowBytes)
            {
                Marshal.Copy(src, buffer, 0, buffer.Length);
            }
            else
            {
                for (int y = 0; y < height; y++)
                    Marshal.Copy(src + (nint)y * srcRowBytes, buffer, y * dstRowBytes, dstRowBytes);
            }

            return new PageImage(width, height, dpi, buffer);
        }
        finally
        {
            converted?.Dispose();
        }
    }
}
