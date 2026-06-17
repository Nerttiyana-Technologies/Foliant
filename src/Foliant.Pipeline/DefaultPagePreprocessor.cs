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

        bool watermark = SuppressWatermarkIfPresent(pixels, luma, w, h);
        bool contrast = StretchContrastIfFlat(pixels, luma);
        bool denoised = DespeckleIfNoisy(pixels, luma, w, h);
        float skew = EstimateSkewDegrees(luma, w, h);

        bool rotate = Math.Abs(skew) >= MinSkew && Math.Abs(skew) <= MaxSkew;
        PageImage image = rotate
            ? Rotate(new PageImage(w, h, page.Dpi, pixels), -skew)
            : (watermark || contrast || denoised ? new PageImage(w, h, page.Dpi, pixels) : page);

        return new PreprocessedPage(image, rotate ? skew : 0f, contrast, denoised, watermark);
    }

    // ── Watermark suppression ────────────────────────────────────────────────
    // Colored stamp overlays ("DRAFT" diagonals) measurably corrupt OCR underneath
    // (PWS corpus: 64.6% recall on affected pages, 2026-06-12). Signature: one
    // dominant saturated hue whose pixels are SPARSE STROKES SPREAD ACROSS A LARGE
    // AREA. Guards keep legitimate colored content: compact headers fail the bbox
    // test, solid figures/charts fail the density test, black text has no chroma.
    private const int WatermarkChromaMin = 55;
    private const double WatermarkMinPixelFraction = 0.01;   // < 1% = negligible
    private const double WatermarkMaxPixelFraction = 0.18;   // > 18% = real content
    private const double WatermarkMinBboxFraction = 0.30;    // must span the page
    private const double WatermarkMaxBboxDensity = 0.40;     // strokes, not fills
    // Stamps are DIAGONAL; colored text (hyperlinks, headers) lies in horizontal rows.
    // |corr(x,y)| of the hue pixels separates them — measured false positive 2026-06-12:
    // FAR clause pages full of scattered blue links matched the sparse+spanning signature
    // and lost up to 22 recall points. Links: corr ≈ 0. Diagonal stamp: corr ≥ ~0.5.
    private const double WatermarkMinDiagonalCorrelation = 0.35;

    private static bool SuppressWatermarkIfPresent(byte[] pixels, byte[] luma, int w, int h)
    {
        const int bins = 12;                                   // 30° hue buckets
        var count = new int[bins];
        var minX = new int[bins]; var minY = new int[bins];
        var maxX = new int[bins]; var maxY = new int[bins];
        var sx = new double[bins]; var sy = new double[bins];
        var sxx = new double[bins]; var syy = new double[bins]; var sxy = new double[bins];
        for (int b = 0; b < bins; b++) { minX[b] = minY[b] = int.MaxValue; maxX[b] = maxY[b] = -1; }

        // Pass 1, sampled: dominant saturated hue + its spatial extent + orientation moments
        const int step = 2;
        long sampled = 0;
        for (int y = 0; y < h; y += step)
        {
            int row = y * w;
            for (int x = 0; x < w; x += step)
            {
                sampled++;
                int p = (row + x) * 4;
                int bin = HueBin(pixels[p], pixels[p + 1], pixels[p + 2], WatermarkChromaMin);
                if (bin < 0) continue;
                count[bin]++;
                if (x < minX[bin]) minX[bin] = x;
                if (x > maxX[bin]) maxX[bin] = x;
                if (y < minY[bin]) minY[bin] = y;
                if (y > maxY[bin]) maxY[bin] = y;
                sx[bin] += x; sy[bin] += y;
                sxx[bin] += (double)x * x; syy[bin] += (double)y * y; sxy[bin] += (double)x * y;
            }
        }

        int best = 0;
        for (int b = 1; b < bins; b++) if (count[b] > count[best]) best = b;
        if (count[best] == 0) return false;

        double pixelFraction = (double)count[best] / sampled;
        if (pixelFraction < WatermarkMinPixelFraction || pixelFraction > WatermarkMaxPixelFraction)
            return false;

        double bw = maxX[best] - minX[best], bh = maxY[best] - minY[best];
        if (bw * bh / ((double)w * h) < WatermarkMinBboxFraction) return false;

        double cellsInBbox = (bw / step) * (bh / step);
        if (cellsInBbox <= 0 || count[best] / cellsInBbox > WatermarkMaxBboxDensity) return false;

        // Diagonal-orientation guard: covariance correlation of the hue pixels' coordinates
        double n = count[best];
        double mx = sx[best] / n, my = sy[best] / n;
        double cxx = sxx[best] / n - mx * mx;
        double cyy = syy[best] / n - my * my;
        double cxy = sxy[best] / n - mx * my;
        if (cxx <= 0 || cyy <= 0) return false;
        double corr = Math.Abs(cxy) / Math.Sqrt(cxx * cyy);
        if (corr < WatermarkMinDiagonalCorrelation) return false;

        // Pass 2, full resolution: whiten the dominant hue (±1 bin, relaxed chroma)
        for (int i = 0, p = 0; i < w * h; i++, p += 4)
        {
            int bin = HueBin(pixels[p], pixels[p + 1], pixels[p + 2], 40);
            if (bin < 0) continue;
            int d = Math.Abs(bin - best);
            if (d > 1 && d != bins - 1) continue;              // hue wheel wraps
            pixels[p] = pixels[p + 1] = pixels[p + 2] = 255;
            luma[i] = 255;
        }
        return true;
    }

    /// <summary>Coarse 30° hue bucket for a BGR pixel, or -1 when not saturated enough
    /// (low chroma = grayscale ink/paper) or too dark to be an overlay tint.</summary>
    private static int HueBin(byte bB, byte bG, byte bR, int chromaMin)
    {
        int r = bR, g = bG, b = bB;
        int max = Math.Max(r, Math.Max(g, b));
        int min = Math.Min(r, Math.Min(g, b));
        int chroma = max - min;
        if (chroma < chromaMin || max < 70) return -1;

        double hue;
        if (max == r) hue = 60.0 * (g - b) / chroma;
        else if (max == g) hue = 60.0 * (b - r) / chroma + 120.0;
        else hue = 60.0 * (r - g) / chroma + 240.0;
        if (hue < 0) hue += 360.0;
        return (int)(hue / 30.0) % 12;
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
