// Page-level coarse orientation detection (0/90/180/270°) by OCR-confidence vote.
//
// Gate 7 measured the gap this closes: on born-digital pages forced through OCR, a 180°
// rotation dropped word recall from ~99% to ~3%, 90° to ~58%, 270° to ~77% — bulk scanners
// and phone captures routinely produce exactly these misorientations. PaddleOCR's per-LINE
// orientation classifier salvages some sideways text but cannot fix a whole page read in the
// wrong order, which is why 180° is near-total loss.
//
// Approach (pure, no new model, no license question — reuses the OCR engine already in the
// pipeline): render the page at each of the four cardinal rotations, OCR a downscaled thumbnail
// of each, and score by Σ(confidence × recognized-text-length). Upright text yields more text
// AND higher confidence, so it wins decisively. A margin biases toward "leave it alone" so an
// already-upright page is never flipped on OCR noise. The winning rotation is then applied once
// to the full-resolution page, which also gives layout detection an upright image.
//
// Cost: four thumbnail OCR passes per OCR-routed page (never runs on text-layer fast-path
// pages). Detection runs on a downscaled copy to keep that cheap; the real extraction OCR
// still runs once on the full page.

using Foliant.Internal;
using SkiaSharp;

namespace Foliant.Pipeline;

/// <summary>
/// Detects and corrects coarse page orientation (0/90/180/270°) using an OCR-confidence vote.
/// Stateless and reusable across pages and threads.
/// </summary>
public sealed class OrientationDetector
{
    private static readonly int[] Candidates = { 0, 90, 180, 270 };
    private static readonly SKSamplingOptions Linear = new(SKFilterMode.Linear, SKMipmapMode.None);

    private readonly int _detectionMaxDim;
    private readonly double _uprightBias;
    private readonly int _minDecisionChars;

    /// <param name="detectionMaxDim">
    /// Longest edge (px) of the thumbnail OCR'd during detection. Smaller is faster but loses
    /// small text; 1000 keeps body text legible at 300-DPI letter size.
    /// </param>
    /// <param name="uprightBias">
    /// A non-zero rotation is only chosen when its score beats the upright (0°) score by this
    /// factor. &gt;1 guards against flipping an already-upright page on OCR noise. 1.15 = needs a
    /// clear 15% win.
    /// </param>
    /// <param name="minDecisionChars">
    /// Minimum recognized characters at the winning orientation before any rotation is applied.
    /// The ratio bias is meaningless when the page has almost no text (a near-zero upright score
    /// makes any noise "beat" it), so a low-text / illustration / near-blank page is left upright
    /// rather than flipped on noise. Real rotated text pages clear this easily once corrected.
    /// </param>
    public OrientationDetector(int detectionMaxDim = 1000, double uprightBias = 1.15, int minDecisionChars = 100)
    {
        _detectionMaxDim = Math.Max(200, detectionMaxDim);
        _uprightBias = Math.Max(1.0, uprightBias);
        _minDecisionChars = Math.Max(0, minDecisionChars);
    }

    /// <summary>
    /// Returns the page rotated upright together with the correction applied (0/90/180/270°,
    /// clockwise). When the page already reads best upright, returns the input unchanged with
    /// <c>AppliedDegrees == 0</c>.
    /// </summary>
    public (PageImage Image, int AppliedDegrees) Correct(PageImage page, IOcrEngine ocr)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(ocr);

        var thumb = Thumbnail(page, _detectionMaxDim);

        double score0 = 0;
        int best = 0;
        double bestScore = double.NegativeInfinity;
        int bestChars = 0;

        foreach (int c in Candidates)
        {
            var candidate = c == 0 ? thumb : ScanDegrader.Rotate(c).Transform(thumb);
            var (score, chars) = Measure(ocr.Recognize(candidate));
            if (c == 0) score0 = score;
            if (score > bestScore) { bestScore = score; best = c; bestChars = chars; }
        }

        // Two guards before flipping a page:
        //  • ratio bias — the winner must clearly beat the upright (0°) reading; and
        //  • minimum signal — the winner must actually recognize enough text to be trusted.
        // The second matters because on a near-textless page score0 ≈ 0, so the ratio alone lets
        // OCR noise "win"; requiring real recognized text keeps illustration/plate pages upright.
        if (best != 0 && (bestScore < score0 * _uprightBias || bestChars < _minDecisionChars))
            best = 0;

        return best == 0 ? (page, 0) : (ScanDegrader.Rotate(best).Transform(page), best);
    }

    /// <summary>
    /// (Σ confidence × trimmed-text-length, Σ trimmed-text-length) over recognized lines — the
    /// score rewards more text at higher confidence; the char count gauges how much signal exists.
    /// </summary>
    private static (double Score, int Chars) Measure(IReadOnlyList<TextLine> lines)
    {
        double s = 0;
        int chars = 0;
        foreach (var l in lines)
        {
            int len = l.Text?.Trim().Length ?? 0;
            if (len > 0) { s += (double)l.Confidence * len; chars += len; }
        }
        return (s, chars);
    }

    /// <summary>Downscale so the longest edge is at most <paramref name="maxDim"/>; returns a copy unchanged if already small.</summary>
    private static PageImage Thumbnail(PageImage page, int maxDim)
    {
        int longest = Math.Max(page.Width, page.Height);
        if (longest <= maxDim)
            return new PageImage(page.Width, page.Height, page.Dpi, (byte[])page.PixelsBgra8888.Clone());

        double scale = (double)maxDim / longest;
        int tw = Math.Max(1, (int)Math.Round(page.Width * scale));
        int th = Math.Max(1, (int)Math.Round(page.Height * scale));

        using var src = SkiaInterop.ToBitmap(page);
        using var small = src.Resize(new SKImageInfo(tw, th, SKColorType.Bgra8888, SKAlphaType.Opaque), Linear)
            ?? throw new InvalidOperationException("Orientation thumbnail resize failed.");
        // Reported DPI is scaled too, so the thumbnail's pixel/point mapping stays consistent.
        int thumbDpi = Math.Max(1, (int)Math.Round(page.Dpi * scale));
        return SkiaInterop.ToPageImage(small, thumbDpi);
    }
}
