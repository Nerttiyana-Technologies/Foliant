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
    private readonly double _minMeanConfidence;
    private readonly double _minDistinctWordRatio;

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
    /// <param name="minMeanConfidence">
    /// Minimum mean OCR confidence (Σ confidence·len / Σ len) at the winning orientation before a
    /// rotation is applied. Decorative front-matter (covers, blank endpapers, library-seal pages)
    /// can OCR into a page-worth of low-confidence garbage from repeating patterns and texture;
    /// requiring genuine confidence keeps those upright. Real body text clears this comfortably
    /// (PaddleOCR confidences on real text run well above this). Default 0.5.
    /// </param>
    /// <param name="minDistinctWordRatio">
    /// Minimum lexical diversity (distinct words / total words, alphanumeric-normalized) at the
    /// winning orientation before a rotation is applied. A library seal or patterned border OCRs as
    /// the SAME token repeated many times — high char count, high confidence, but near-zero
    /// diversity — which the count and confidence guards miss. Real prose is diverse and clears this
    /// easily. Default 0.30.
    /// </param>
    public OrientationDetector(
        int detectionMaxDim = 1000, double uprightBias = 1.15, int minDecisionChars = 100,
        double minMeanConfidence = 0.5, double minDistinctWordRatio = 0.30)
    {
        _detectionMaxDim = Math.Max(200, detectionMaxDim);
        _uprightBias = Math.Max(1.0, uprightBias);
        _minDecisionChars = Math.Max(0, minDecisionChars);
        _minMeanConfidence = Math.Clamp(minMeanConfidence, 0.0, 1.0);
        _minDistinctWordRatio = Math.Clamp(minDistinctWordRatio, 0.0, 1.0);
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
        IReadOnlyList<TextLine> bestLines = Array.Empty<TextLine>();

        foreach (int c in Candidates)
        {
            var candidate = c == 0 ? thumb : ScanDegrader.Rotate(c).Transform(thumb);
            var lines = ocr.Recognize(candidate);
            var (score, chars) = Measure(lines);
            if (c == 0) score0 = score;
            if (score > bestScore) { bestScore = score; best = c; bestChars = chars; bestLines = lines; }
        }

        // Guards before flipping a page (all must hold; otherwise the page is left upright):
        //  • ratio bias    — the winner must clearly beat the upright (0°) reading;
        //  • minimum signal — the winner must recognize enough text to be trusted at all;
        //  • mean confidence — the winning text must be confident, not low-conf texture garbage;
        //  • lexical diversity — the winning text must be varied, not one token repeated.
        // The last two close the decorative-front-matter hole: covers, blank endpapers and
        // library-seal pages OCR into a page of repeating/low-confidence "text" from patterns,
        // which clears the count and ratio guards but is not real text — and must stay upright.
        if (best != 0 &&
            (bestScore < score0 * _uprightBias
             || bestChars < _minDecisionChars
             || MeanConfidence(bestLines) < _minMeanConfidence
             || DistinctWordRatio(bestLines) < _minDistinctWordRatio))
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

    /// <summary>Char-length-weighted mean OCR confidence over recognized lines (0 when empty).</summary>
    private static double MeanConfidence(IReadOnlyList<TextLine> lines)
    {
        double s = 0;
        int chars = 0;
        foreach (var l in lines)
        {
            int len = l.Text?.Trim().Length ?? 0;
            if (len > 0) { s += (double)l.Confidence * len; chars += len; }
        }
        return chars == 0 ? 0 : s / chars;
    }

    /// <summary>
    /// Distinct words / total words over the recognized text, words normalized to lower-case
    /// alphanumerics. Near 1.0 for varied prose; near 0 for a single token repeated (a seal or
    /// patterned border). Returns 1.0 when there are no words so it never causes a false rejection
    /// on its own — the char-count and confidence guards handle the no-text case.
    /// </summary>
    private static double DistinctWordRatio(IReadOnlyList<TextLine> lines)
    {
        var words = new List<string>();
        foreach (var l in lines)
        {
            if (string.IsNullOrWhiteSpace(l.Text)) continue;
            foreach (var tok in l.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var norm = new string(tok.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
                if (norm.Length > 0) words.Add(norm);
            }
        }
        if (words.Count == 0) return 1.0;
        return (double)words.Distinct().Count() / words.Count;
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
