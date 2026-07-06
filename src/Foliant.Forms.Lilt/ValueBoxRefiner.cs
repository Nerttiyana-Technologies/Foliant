// Box fidelity (2026-07-07): the LiLT arm's word boxes are proportional x-slices of the OCR line
// (LiltFormFieldExtractor.SplitWords) — synthetic geometry that runs wide of the actual glyphs.
// A value box that spills past its cell boundary overlaps the neighbouring truth rect, which is
// the STRADDLE class of Gate-3 CROSS-FIELD errors ("box-fidelity fixable" in the scorer) and the
// reason the truncation probe measures 0.16/0.26 on these boxes versus 0.58/0.18 on true rects.
//
// This refiner trims a value box INWARD to the columns that actually carry ink. It is a MONOTONIC
// SHRINK bounded by the input box: it never grows toward the neighbour, so it cannot invent overlap
// or convert a clean prediction into a straddle. It is applied to OUTPUT geometry only (the emitted
// FormField.Bounds and the truncation probe) — the boxes fed to the model stay proportional so the
// train/inference featurization (prepare_scan_kv.py) remains aligned. Default OFF at the call site
// (LiltFormFieldExtractor.RefineWordBoxes); a measurement rig until Gate 3 confirms straddles drop
// with no regression, then promote.
//
// KNOWN LIMIT (first cut): pure ink-trim of the box's outer bounds. It resolves straddles caused by
// trailing/leading whitespace over-extension (the common case) but NOT a box that has captured the
// neighbour's ink through gross over-extension — that residue stays SOLID in the Gate-3 geometry
// line. Interior gap-splitting (keep the ink cluster anchored to the value's leading edge) is the
// documented follow-up if the straddle count does not clear.

using Foliant;

namespace Foliant.Forms.Lilt;

/// <summary>
/// Sharpens an extracted value's box to its real ink extent within its own bounds — a conservative,
/// monotonic horizontal shrink. See the file header for the box-fidelity rationale.
/// </summary>
internal static class ValueBoxRefiner
{
    /// <summary>Luma below this is ink. Matches <see cref="ValueTruncationProbe"/>.</summary>
    private const int InkLumaThreshold = 160;

    /// <summary>
    /// A column counts as ink-bearing only with at least this fraction of its height dark, so JPEG
    /// speckle on scanned pages cannot veto (or falsely extend) the trim. Floored at 2 px.
    /// </summary>
    private const float MinInkFraction = 0.08f;

    /// <summary>
    /// Returns <paramref name="box"/> trimmed horizontally to the first and last ink-bearing columns
    /// inside its own X range. The vertical band is preserved unchanged — the truncation probe
    /// extends it by ±height and relies on the full line height for ruling detection. Returned
    /// unchanged when the region holds no ink (blank/degenerate), so a real prediction is never
    /// collapsed to nothing.
    /// </summary>
    public static BoundingBox InkTrim(PageImage page, BoundingBox box)
    {
        int x1 = Math.Clamp((int)box.X1, 0, page.Width - 1);
        int x2 = Math.Clamp((int)box.X2, x1 + 1, page.Width);
        int y1 = Math.Clamp((int)box.Y1, 0, page.Height - 1);
        int y2 = Math.Clamp((int)box.Y2, y1 + 1, page.Height);

        int h = y2 - y1;
        int minColInk = Math.Max(2, (int)(MinInkFraction * h));

        int newX1 = -1, newX2 = -1;
        for (int x = x1; x < x2; x++)
        {
            int dark = 0;
            for (int y = y1; y < y2; y++)
                if (IsInk(page, x, y)) dark++;
            if (dark >= minColInk) { if (newX1 < 0) newX1 = x; newX2 = x; }
        }
        if (newX1 < 0) return box;   // no ink columns cleared the floor — keep the original box

        // Preserve the original vertical extent (floats) for the probe; only the X edges tighten.
        return new BoundingBox(newX1, box.Y1, newX2 + 1, box.Y2);
    }

    private static bool IsInk(PageImage page, int x, int y)
    {
        int i = (y * page.Width + x) * 4;   // BGRA
        var px = page.PixelsBgra8888;
        double luma = px[i + 2] * 0.299 + px[i + 1] * 0.587 + px[i] * 0.114;
        return luma < InkLumaThreshold;
    }
}
