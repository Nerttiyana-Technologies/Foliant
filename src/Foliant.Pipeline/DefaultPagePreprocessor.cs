// Deterministic scanned-page cleanup — no models, no learning, fully reproducible.
//
//   1. Contrast normalization: percentile stretch when the luma histogram is "flat"
//      (faded scans, gray haze). Clean renders are untouched.
//   2. Despeckle: isolated dark single pixels (salt-and-pepper from scanners) are
//      whitened when their density says "scanner noise" rather than "ink".
//   3. Deskew: projection-profile method — search the rotation angle that maximizes
//      row-projection variance of ink pixels; rotate when |angle| ≥ 0.3°.
//
// Runs ONLY on pages routed to OCR; born-digital renders skip this stage entirely
// (see DocumentProcessor). All thresholds documented inline; tune against the
// degraded-corpus tests, not by eye.

using Foliant.Internal;
using SkiaSharp;

namespace Foliant.Pipeline;

public sealed class DefaultPagePreprocessor : IPagePreprocessor
{
    // Contrast: stretch when the 2–98 percentile luma range is narrower than this.
    private const int FlatHistogramRange = 170;

    // Despeckle: a page is "noisy" when isolated dark pixels exceed this fraction.
    private const double SpeckDensityThreshold = 0.0005;

    // Deskew: correct angles in [MinSkew, MaxSkew] degrees (smaller is noise,
    // larger is probably landscape orientation — a different problem).
    private const float MinSkew = 0.3f;
    private const float MaxSkew = 8f;

    public PreprocessedPage Process(PageImage page)
    {
        int w = page.Width, h = page.Height;
        var pixels = (byte[])page.PixelsBgra8888.Clone();

        // Luma working buffer (BT.601 on BGRA bytes)
        var luma = new byte[w * h];
        for (int i = 0, p = 0; i < luma.Length; i++, p += 4)
            luma[i] = (byte)((pixels[p] * 114 + pixels[p + 1] * 587 + pixels[p + 2] * 299) / 1000);

        bool contrast = StretchContrastIfFlat(pixels, luma);
        bool denoised = DespeckleIfNoisy(pixels, luma, w, h);
        float skew = EstimateSkewDegrees(luma, w, h);

        bool rotate = Math.Abs(skew) >= MinSkew && Math.Abs(skew) <= MaxSkew;
        PageImage image = rotate
            ? Rotate(new PageImage(w, h, page.Dpi, pixels), -skew)
            : (contrast || denoised ? new PageImage(w, h, page.Dpi, pixels) : page);

        return new PreprocessedPage(image, rotate ? skew : 0f, contrast, denoised);
    }

    // ── Contrast ─────────────────────────────────────────────────────────────
    private static bool StretchContrastIfFlat(byte[] pixels, byte[] luma)
    {
        Span<int> hist = stackalloc int[256];
        foreach (byte v in luma) hist[v]++;

        int total = luma.Length, lowCount = total / 50, highCount = total - total / 50;
        int p2 = 0, p98 = 255, cum = 0;
        for (int v = 0; v < 256; v++)
        {
            cum += hist[v];
            if (cum <= lowCount) p2 = v;
            if (cum < highCount) p98 = v;
        }

        int range = p98 - p2;
        if (range >= FlatHistogramRange || range < 8) return false;

        Span<byte> map = stackalloc byte[256];
        for (int v = 0; v < 256; v++)
            map[v] = (byte)Math.Clamp((v - p2) * 255 / range, 0, 255);

        for (int p = 0; p < pixels.Length; p += 4)
        {
            pixels[p] = map[pixels[p]];
            pixels[p + 1] = map[pixels[p + 1]];
            pixels[p + 2] = map[pixels[p + 2]];
        }
        for (int i = 0; i < luma.Length; i++) luma[i] = map[luma[i]];
        return true;
    }

    // ── Despeckle ────────────────────────────────────────────────────────────
    private static bool DespeckleIfNoisy(byte[] pixels, byte[] luma, int w, int h)
    {
        var specks = new List<int>();
        for (int y = 1; y < h - 1; y++)
        {
            int row = y * w;
            for (int x = 1; x < w - 1; x++)
            {
                int i = row + x;
                if (luma[i] >= 100) continue;                     // not dark
                if (luma[i - 1] > 180 && luma[i + 1] > 180 &&
                    luma[i - w] > 180 && luma[i + w] > 180)       // isolated
                    specks.Add(i);
            }
        }

        if (specks.Count < (long)w * h * SpeckDensityThreshold) return false;

        foreach (int i in specks)
        {
            int p = i * 4;
            pixels[p] = pixels[p + 1] = pixels[p + 2] = 255;
            luma[i] = 255;
        }
        return true;
    }

    // ── Deskew ───────────────────────────────────────────────────────────────
    /// <summary>Projection-profile skew estimate: the rotation that maximizes the
    /// variance of per-row ink counts is the one that aligns text lines with rows.</summary>
    private static float EstimateSkewDegrees(byte[] luma, int w, int h)
    {
        // Downscale ink sampling for speed; collect ink pixel coordinates.
        int step = Math.Max(1, Math.Max(w, h) / 1200);
        var xs = new List<short>();
        var ys = new List<short>();
        for (int y = 0; y < h; y += step)
        {
            int row = y * w;
            for (int x = 0; x < w; x += step)
                if (luma[row + x] < 160) { xs.Add((short)(x / step)); ys.Add((short)(y / step)); }
        }
        if (xs.Count < 500) return 0f;                            // not enough ink to judge

        int sh = h / step + 2;
        float best = 0f;
        double bestScore = double.MinValue;

        // Coarse pass ±8° @ 0.25°, then fine pass ±0.25° @ 0.05°
        for (float a = -MaxSkew; a <= MaxSkew; a += 0.25f)
            Score(a, ref best, ref bestScore);
        float coarse = best;
        for (float a = coarse - 0.25f; a <= coarse + 0.25f; a += 0.05f)
            Score(a, ref best, ref bestScore);

        return best;

        void Score(float angleDeg, ref float bestAngle, ref double bestS)
        {
            double tan = Math.Tan(angleDeg * Math.PI / 180.0);
            var bins = new int[sh + (int)(Math.Abs(tan) * (w / (double)step)) + 4];
            int offset = tan < 0 ? (int)(-tan * (w / (double)step)) + 1 : 1;

            for (int i = 0; i < xs.Count; i++)
            {
                int bin = (int)(ys[i] - xs[i] * tan) + offset;
                if (bin >= 0 && bin < bins.Length) bins[bin]++;
            }

            double score = 0;
            foreach (int b in bins) score += (double)b * b;       // variance proxy
            if (score > bestS) { bestS = score; bestAngle = angleDeg; }
        }
    }

    private static PageImage Rotate(PageImage page, float degrees)
    {
        using var src = SkiaInterop.ToBitmap(page);
        using var dst = new SKBitmap(new SKImageInfo(page.Width, page.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(dst))
        {
            canvas.Clear(SKColors.White);
            canvas.Translate(page.Width / 2f, page.Height / 2f);
            canvas.RotateDegrees(degrees);
            canvas.Translate(-page.Width / 2f, -page.Height / 2f);
            using var image = SKImage.FromBitmap(src);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        }
        return SkiaInterop.ToPageImage(dst, page.Dpi);
    }
}
