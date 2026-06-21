using Foliant;

namespace Foliant.Templates;

/// <summary>
/// Decides whether a checkbox at a KNOWN normalized position is marked, purely from pixels — no OCR. This is
/// what makes scanned/flattened federal forms reliable: a checkbox's selection (27a/27b, set-aside, amendment
/// type) is the Q&amp;A-critical signal, and "is there a dark mark in this box" survives OCR garbling entirely.
///
/// Measures the fraction of dark pixels in the box INTERIOR — inset from the edges so the printed border (which
/// is always dark) doesn't count. An empty box interior is near-white; a checked one carries an X/✓ stroke.
/// Thresholds are conservative starting points; tune on a real scanned-form sample.
/// </summary>
public static class CheckboxPixelDetector
{
    /// <param name="image">Rendered page (BGRA).</param>
    /// <param name="box">Checkbox position in normalized (0..1) page coordinates.</param>
    /// <param name="inset">Fraction trimmed from each side to skip the printed border (0.22 = 22%).</param>
    /// <param name="darkLuma">Luma below this (0..255) counts as "ink".</param>
    /// <param name="markFraction">Interior ink fraction above this ⇒ checked.</param>
    public static bool IsChecked(
        PageImage image, NormalizedRect box,
        float inset = 0.22f, int darkLuma = 140, float markFraction = 0.06f)
    {
        ArgumentNullException.ThrowIfNull(image);
        int W = image.Width, H = image.Height;

        int x1 = (int)(box.X1 * W), y1 = (int)(box.Y1 * H);
        int x2 = (int)(box.X2 * W), y2 = (int)(box.Y2 * H);
        int bw = x2 - x1, bh = y2 - y1;
        if (bw <= 2 || bh <= 2) return false;

        int ax1 = Math.Max(0, x1 + (int)(bw * inset));
        int ay1 = Math.Max(0, y1 + (int)(bh * inset));
        int ax2 = Math.Min(W, x2 - (int)(bw * inset));
        int ay2 = Math.Min(H, y2 - (int)(bh * inset));
        if (ax2 <= ax1 || ay2 <= ay1) return false;

        byte[] px = image.PixelsBgra8888;
        int stride = W * 4;
        long dark = 0, total = 0;
        for (int y = ay1; y < ay2; y++)
        {
            int row = y * stride;
            for (int x = ax1; x < ax2; x++)
            {
                int p = row + x * 4;
                int luma = (px[p + 2] * 30 + px[p + 1] * 59 + px[p + 0] * 11) / 100;   // BGRA → weighted luma
                if (luma < darkLuma) dark++;
                total++;
            }
        }
        return total > 0 && (double)dark / total > markFraction;
    }
}
