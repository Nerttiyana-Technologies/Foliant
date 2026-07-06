// Truncated-source detection (2026-07-06, TD-41 quantification): ~7% of scanned-holdout
// field values are CLIPPED IN THE SOURCE IMAGE — the flattener/scanner cut the appearance
// at the cell border, so the page shows "26,320.0" where the field once held "$26,320.00"
// (verified visually; the same class is confirmed in production customer scans). No OCR or
// model can recover pixels that were never printed, so the correct behavior is honesty:
// transcribe what is visible and FLAG it (FormField.PossiblyTruncated), never suppress.
//
// Detectable signature (contact-sheet survey): the value's ink runs FLUSH into a vertical
// ruling line with no trailing whitespace gap. A complete value ends with clear space before
// the next cell border. KNOWN LIMIT: truncations that stop mid-word with room to spare
// (no adjacent ruling — one TD-41 family renders these) carry no geometric signature and are
// not flagged; that residue is measurable via Gate 3's TRUNCATED-SOURCE column.
//
// OPERATING POINT (2026-07-06, tuned on the TD-41 populations — 97 confirmed-truncated vs
// 476 confirmed-complete truth rects, parameter sweep): flush ≤ 0.2×glyph-height, ruling
// dark-fraction ≥ 0.9, search reach 0.5×glyph-height ⇒ recall 0.58 / false-flag 0.18 (the
// knee of the curve). The first-cut constants (0.35×h / 0.75 / 1.5×h) measured recall 0.72
// but false-flag ~0.41 (424 flagged among 724 corrects on Gate 3) — review-fatigue noise,
// not a signal. Geometry saturates near the knee: on blurry 96-DPI-source scans, complete
// values in dense ruled cells sit only a few px off the border, overlapping the truncated
// distribution. Raising recall past ~0.6 needs a second signal (value-shape/width model),
// not a looser gap.
//
// STATUS (2026-07-06): PARKED, default off at the call site (LiltFormFieldExtractor.
// FlagPossiblyTruncated). The knee holds only on TRUTH-RECT geometry, which production
// doesn't have: on the extractor's SplitWords value boxes the same constants measure
// 0.16 recall / 0.26 false-flag (Gate 3 validation: 10/63 caught, 186/724 over-flagged),
// and on raw det line boxes 0.27/0.25 (bench — det boxes merge label+value and unclip past
// rulings, so a line edge is not a value edge). The probe becomes shippable when the
// extractor carries ink-accurate word-level boxes; until then Gate 3's TRUNCATED-SOURCE
// column is the honesty mechanism for this class.

using Foliant;

namespace Foliant.Forms.Lilt;

/// <summary>
/// Detects the cell-border clipping signature on an extracted value's raster region:
/// a vertical ruling immediately at the value's edge with ink running flush into it.
/// </summary>
internal static class ValueTruncationProbe
{
    private const int InkLumaThreshold = 160;

    /// <summary>Column dark-fraction (over the extended band) required to call it a ruling.</summary>
    private const float RulingDarkFraction = 0.9f;

    /// <summary>
    /// True when the value's ink runs flush into a vertical ruling at the box's right or left
    /// edge. <paramref name="bounds"/> is the value's box in page raster coordinates.
    /// </summary>
    public static bool IsFlushAgainstRuling(PageImage page, BoundingBox bounds)
    {
        int y1 = Math.Clamp((int)bounds.Y1, 0, page.Height - 1);
        int y2 = Math.Clamp((int)bounds.Y2, y1 + 1, page.Height);
        int h = y2 - y1;
        if (h < 4) return false;

        // Extended band: a ruling is taller than the text line; a glyph stroke is not.
        int ey1 = Math.Max(0, y1 - h);
        int ey2 = Math.Min(page.Height, y2 + h);

        int x1 = Math.Clamp((int)bounds.X1, 0, page.Width - 1);
        int x2 = Math.Clamp((int)bounds.X2, x1 + 1, page.Width);

        // Flush gap: at most a fifth of the glyph height between the last ink and the ruling.
        int flushGap = Math.Max(1, (int)(0.2f * h));
        int search = Math.Max(4, (int)(0.5f * h));      // how far past the edge a ruling may sit

        // Nearest ruling to each edge: leftmost in the window past the right edge,
        // rightmost in the window before the left edge.
        int? rulingRight = FindRuling(page, from: Math.Max(0, x2 - 2), to: Math.Min(page.Width, x2 + search), ey1, ey2, nearestFromLow: true);
        if (rulingRight is int rx)
        {
            int? lastInk = LastInkColumn(page, x1, Math.Max(x1, rx - 2), y1, y2, rightmost: true);
            if (lastInk is int li && rx - li <= flushGap) return true;
        }

        int? rulingLeft = FindRuling(page, from: Math.Max(0, x1 - search), to: Math.Min(page.Width, x1 + 2), ey1, ey2, nearestFromLow: false);
        if (rulingLeft is int lx)
        {
            int? firstInk = LastInkColumn(page, Math.Min(page.Width - 1, lx + 2), x2, y1, y2, rightmost: false);
            if (firstInk is int fi && fi - lx <= flushGap) return true;
        }

        return false;
    }

    /// <summary>
    /// Column in [from, to) whose extended-band dark fraction marks a vertical ruling —
    /// the first found scanning ascending (<paramref name="nearestFromLow"/>) or descending.
    /// </summary>
    private static int? FindRuling(PageImage page, int from, int to, int ey1, int ey2, bool nearestFromLow)
    {
        int extH = Math.Max(1, ey2 - ey1);
        var range = nearestFromLow
            ? Enumerable.Range(from, Math.Max(0, to - from))
            : Enumerable.Range(from, Math.Max(0, to - from)).Reverse();
        foreach (int x in range)
        {
            int dark = 0;
            for (int y = ey1; y < ey2; y++)
                if (IsInk(page, x, y)) dark++;
            if (dark >= RulingDarkFraction * extH) return x;
        }
        return null;
    }

    /// <summary>Rightmost (or leftmost) column carrying ink within [x1, x2) × [y1, y2), else null.</summary>
    private static int? LastInkColumn(PageImage page, int x1, int x2, int y1, int y2, bool rightmost)
    {
        if (x2 <= x1) return null;
        var range = rightmost
            ? Enumerable.Range(x1, x2 - x1).Reverse()
            : Enumerable.Range(x1, x2 - x1);
        foreach (int x in range)
            for (int y = y1; y < y2; y++)
                if (IsInk(page, x, y)) return x;
        return null;
    }

    private static bool IsInk(PageImage page, int x, int y)
    {
        int i = (y * page.Width + x) * 4;                       // BGRA
        var px = page.PixelsBgra8888;
        double luma = px[i + 2] * 0.299 + px[i + 1] * 0.587 + px[i] * 0.114;
        return luma < InkLumaThreshold;
    }
}
