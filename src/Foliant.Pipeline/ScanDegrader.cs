// Deterministic, scan-like degradations as IPageImageTransform factories. These exist to
// MEASURE robustness, not to improve it: the verification harness (Gate 7) renders a
// born-digital page (whose embedded text layer is exact ground truth), applies one of these
// degradations, forces OCR, and scores word recall against the text layer. The recall drop
// from baseline is the cost of that degradation — the ledger the 0.4.0 scanned-doc work is
// measured against (e.g. coarse-rotation recall stays low until orientation detection lands).
//
// Everything here is pure and reproducible: same input image + same parameters → same output,
// independent of page order or thread. Built on SkiaSharp (already a pipeline dependency).

using Foliant.Internal;
using SkiaSharp;

namespace Foliant.Pipeline;

/// <summary>
/// Factory for deterministic <see cref="IPageImageTransform"/> degradations that approximate
/// real scanner/camera artifacts. Use with <see cref="ProcessingOptions.ImageTransform"/> and
/// <see cref="TextLayerMode.Never"/> to measure OCR robustness.
/// </summary>
public static class ScanDegrader
{
    private static readonly SKSamplingOptions Linear = new(SKFilterMode.Linear, SKMipmapMode.None);

    /// <summary>A no-op transform — the Gate 7 baseline.</summary>
    public static IPageImageTransform Identity { get; } = new DelegateTransform(static p => p);

    /// <summary>
    /// Rotate the page by <paramref name="degrees"/> (clockwise positive) about its center,
    /// expanding the canvas so nothing is clipped and filling exposed corners with white.
    /// Models both fine scanner skew (e.g. ±1–7°) and coarse misorientation (90/180/270°).
    /// </summary>
    public static IPageImageTransform Rotate(double degrees) =>
        new DelegateTransform(page =>
        {
            double norm = ((degrees % 360) + 360) % 360;
            if (norm < 1e-9) return page; // 0° / 360° is a no-op

            using var src = SkiaInterop.ToBitmap(page);
            double rad = degrees * Math.PI / 180.0;
            double cos = Math.Abs(Math.Cos(rad)), sin = Math.Abs(Math.Sin(rad));
            int w = page.Width, h = page.Height;
            int nw = Math.Max(1, (int)Math.Round(w * cos + h * sin));
            int nh = Math.Max(1, (int)Math.Round(w * sin + h * cos));

            using var dst = new SKBitmap(new SKImageInfo(nw, nh, SKColorType.Bgra8888, SKAlphaType.Opaque));
            using (var canvas = new SKCanvas(dst))
            using (var img = SKImage.FromBitmap(src))
            {
                canvas.Clear(SKColors.White);
                canvas.Translate(nw / 2f, nh / 2f);
                canvas.RotateDegrees((float)degrees);
                canvas.Translate(-w / 2f, -h / 2f);
                canvas.DrawImage(img, 0, 0, Linear, paint: null);
            }
            return SkiaInterop.ToPageImage(dst, page.Dpi);
        });

    /// <summary>
    /// Re-encode the page as JPEG at <paramref name="quality"/> (0–100) and decode it back,
    /// stamping in blocking/ringing artifacts. 75 is mild, 40 visible, 20 severe.
    /// </summary>
    public static IPageImageTransform JpegRecompress(int quality) =>
        new DelegateTransform(page =>
        {
            int q = Math.Clamp(quality, 1, 100);
            using var src = SkiaInterop.ToBitmap(page);
            using var img = SKImage.FromBitmap(src);
            using var data = img.Encode(SKEncodedImageFormat.Jpeg, q)
                ?? throw new InvalidOperationException("JPEG encode failed.");
            using var decoded = SKBitmap.Decode(data)
                ?? throw new InvalidOperationException("JPEG decode failed.");
            return SkiaInterop.ToPageImage(decoded, page.Dpi);
        });

    /// <summary>
    /// Add zero-mean Gaussian noise with standard deviation <paramref name="sigma"/> (in 0–255
    /// luma units) independently to each color channel. Deterministic: the RNG is seeded from
    /// <paramref name="seed"/> and the page dimensions, so the same page yields the same noise.
    /// </summary>
    public static IPageImageTransform GaussianNoise(double sigma, int seed = 1234) =>
        new DelegateTransform(page =>
        {
            if (sigma <= 0) return page;
            var px = (byte[])page.PixelsBgra8888.Clone();
            var rng = new Random(unchecked(seed ^ (page.Width * 73856093) ^ (page.Height * 19349663)));
            for (int i = 0; i < px.Length; i += 4)
            {
                // One Box–Muller draw shared across B,G,R keeps noise achromatic (scanner-like).
                double u1 = 1.0 - rng.NextDouble(), u2 = 1.0 - rng.NextDouble();
                double g = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2) * sigma;
                px[i] = ClampByte(px[i] + g);
                px[i + 1] = ClampByte(px[i + 1] + g);
                px[i + 2] = ClampByte(px[i + 2] + g);
                // alpha (i+3) untouched
            }
            return new PageImage(page.Width, page.Height, page.Dpi, px);
        });

    /// <summary>
    /// Gaussian blur with the given <paramref name="sigma"/> in pixels (soft/out-of-focus scan).
    /// ~1.0 is mild, ~2.5 heavy at 300 DPI.
    /// </summary>
    public static IPageImageTransform GaussianBlur(float sigma) =>
        new DelegateTransform(page =>
        {
            if (sigma <= 0) return page;
            using var src = SkiaInterop.ToBitmap(page);
            using var dst = new SKBitmap(new SKImageInfo(page.Width, page.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
            using (var canvas = new SKCanvas(dst))
            using (var img = SKImage.FromBitmap(src))
            using (var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma) })
            {
                canvas.Clear(SKColors.White);
                canvas.DrawImage(img, 0, 0, Linear, paint);
            }
            return SkiaInterop.ToPageImage(dst, page.Dpi);
        });

    /// <summary>
    /// Simulate a low-resolution scan: downsample as if the page had been captured at
    /// <paramref name="targetDpi"/>, then scale back to the original pixel dimensions. Detail
    /// below the target sampling rate is lost irreversibly. The reported <see cref="PageImage.Dpi"/>
    /// is preserved so downstream geometry is unaffected; only sharpness degrades.
    /// </summary>
    public static IPageImageTransform Downscale(int targetDpi) =>
        new DelegateTransform(page =>
        {
            if (targetDpi <= 0 || targetDpi >= page.Dpi) return page;
            int w = page.Width, h = page.Height;
            int tw = Math.Max(1, (int)Math.Round(w * (double)targetDpi / page.Dpi));
            int th = Math.Max(1, (int)Math.Round(h * (double)targetDpi / page.Dpi));

            using var src = SkiaInterop.ToBitmap(page);
            using var small = src.Resize(new SKImageInfo(tw, th, SKColorType.Bgra8888, SKAlphaType.Opaque), Linear)
                ?? throw new InvalidOperationException("Downscale resize failed.");
            using var back = small.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque), Linear)
                ?? throw new InvalidOperationException("Upscale resize failed.");
            return SkiaInterop.ToPageImage(back, page.Dpi);
        });

    /// <summary>
    /// Flatten contrast toward a light-gray midpoint (faded / low-ink scan). <paramref name="keep"/>
    /// in (0,1] is the retained contrast fraction: 1 = unchanged, 0.4 = strongly faded. The narrowed,
    /// lifted luma band is exactly what the preprocessor's contrast-stretch stage is meant to recover.
    /// </summary>
    public static IPageImageTransform FadeContrast(double keep) =>
        new DelegateTransform(page =>
        {
            if (keep >= 1.0) return page;
            double k = Math.Clamp(keep, 0.0, 1.0);
            const double Mid = 160.0; // light-gray center: blacks lift, whites dim toward paper-gray
            var px = (byte[])page.PixelsBgra8888.Clone();
            for (int i = 0; i < px.Length; i += 4)
            {
                px[i] = ClampByte(Mid + (px[i] - Mid) * k);
                px[i + 1] = ClampByte(Mid + (px[i + 1] - Mid) * k);
                px[i + 2] = ClampByte(Mid + (px[i + 2] - Mid) * k);
            }
            return new PageImage(page.Width, page.Height, page.Dpi, px);
        });

    /// <summary>Apply transforms left-to-right as a single transform.</summary>
    public static IPageImageTransform Compose(params IPageImageTransform[] transforms) =>
        new DelegateTransform(page =>
        {
            foreach (var t in transforms) page = t.Transform(page);
            return page;
        });

    private static byte ClampByte(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

    private sealed class DelegateTransform(Func<PageImage, PageImage> fn) : IPageImageTransform
    {
        public PageImage Transform(PageImage image) => fn(image);
    }
}
