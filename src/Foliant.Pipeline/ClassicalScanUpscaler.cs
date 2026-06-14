// Classical (bicubic) upscaling for low-resolution scanned pages, applied before OCR.
//
// This cannot invent detail absent from the source — that needs an ML super-resolution model. It
// presents the existing glyphs at a larger pixel scale with smooth edges, which can nudge the
// recognizer on borderline scans. It is deliberately the cheap, dependency-free first cut: the
// IScanUpscaler seam lets an ML backend replace it later without any pipeline change. Whether it
// ships on by default is decided by the Gate scorecard, not by assumption.

using Foliant.Internal;
using SkiaSharp;

namespace Foliant.Pipeline;

public sealed class ClassicalScanUpscaler : IScanUpscaler
{
    // Catmull-Rom (Keys cubic, b=0 c=0.5): sharper than Mitchell, which suits text edges better
    // than a softening filter. SkiaSharp has no true Lanczos; this is its closest high-quality cubic.
    private static readonly SKSamplingOptions Cubic = new(SKCubicResampler.CatmullRom);

    public PageImage Upscale(PageImage image, float factor)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (factor <= 1f) return image;

        int w = (int)Math.Round(image.Width * (double)factor);
        int h = (int)Math.Round(image.Height * (double)factor);
        if (w <= image.Width || h <= image.Height) return image;

        using var src = SkiaInterop.ToBitmap(image);
        using var dst = src.Resize(
            new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque), Cubic)
            ?? throw new InvalidOperationException("Upscale resize failed.");

        // Dpi is preserved: it is the nominal render DPI, and downstream geometry lives in the
        // (now larger) pixel space regardless. EffectiveDpi already records the true source quality.
        return SkiaInterop.ToPageImage(dst, image.Dpi);
    }
}
