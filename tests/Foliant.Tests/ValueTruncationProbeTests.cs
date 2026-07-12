// PossiblyTruncated honesty flag (2026-07-06): values clipped at cell borders when the source
// was flattened/scanned ("26,320.0" where the field held "$26,320.00") must be flagged, never
// returned as confident complete extractions. These tests pin the probe's geometric contract:
// flush-into-ruling → flagged; clear gap before the ruling, or no ruling → not flagged.

using Foliant;
using Foliant.Forms.Lilt;
using Xunit;

namespace Foliant.Tests;

public sealed class ValueTruncationProbeTests
{
    private const int W = 300;
    private const int H = 120;

    /// <summary>White BGRA page with optional black vertical ruling and a text-ish ink run.</summary>
    private static PageImage Page(int? rulingX, int inkX1, int inkX2, int inkY1, int inkY2)
    {
        var px = new byte[W * H * 4];
        for (int i = 0; i < px.Length; i++) px[i] = 255;

        void Dark(int x, int y)
        {
            int i = (y * W + x) * 4;
            px[i] = px[i + 1] = px[i + 2] = 0;
        }

        if (rulingX is int rx)
            for (int y = 0; y < H; y++) Dark(rx, y);            // full-height cell border

        // "text": dashed ink run (glyph-ish, with intra-word gaps) on the value row
        for (int x = inkX1; x < inkX2; x++)
            if (x % 5 != 4)
                for (int y = inkY1; y < inkY2; y++) Dark(x, y);

        return new PageImage(W, H, 96, px);
    }

    // Value box rows 50..70, glyph height 20.
    private static readonly BoundingBox Box = new(40, 50, 200, 70);

    [Fact]
    public void InkRunsFlushIntoRuling_Flagged()
    {
        // ruling at 205, ink ends at 203 → gap 2 ≤ flush threshold (7)
        var page = Page(rulingX: 205, inkX1: 60, inkX2: 203, inkY1: 52, inkY2: 68);
        Assert.True(ValueTruncationProbe.IsFlushAgainstRuling(page, Box));
    }

    [Fact]
    public void ClearGapBeforeRuling_NotFlagged()
    {
        // ruling at 205, ink ends at 160 → gap 45 ≫ flush threshold
        var page = Page(rulingX: 205, inkX1: 60, inkX2: 160, inkY1: 52, inkY2: 68);
        Assert.False(ValueTruncationProbe.IsFlushAgainstRuling(page, Box));
    }

    [Fact]
    public void NoRulingAnywhere_NotFlagged()
    {
        var page = Page(rulingX: null, inkX1: 60, inkX2: 200, inkY1: 52, inkY2: 68);
        Assert.False(ValueTruncationProbe.IsFlushAgainstRuling(page, Box));
    }

    [Fact]
    public void InkStartsFlushAfterLeftRuling_Flagged()
    {
        // leading clip ("$49," lost at the left border): ruling at 38, ink starts at 40
        var page = Page(rulingX: 38, inkX1: 40, inkX2: 150, inkY1: 52, inkY2: 68);
        Assert.True(ValueTruncationProbe.IsFlushAgainstRuling(page, Box));
    }

    [Fact]
    public void GlyphStrokeIsNotARuling_NotFlagged()
    {
        // a text-height vertical stroke right at the box edge (e.g. the letter "l") must not
        // count as a cell border — rulings are taller than the line
        var page = Page(rulingX: null, inkX1: 60, inkX2: 200, inkY1: 52, inkY2: 68);
        var px = page.PixelsBgra8888;
        for (int y = 50; y < 70; y++)                            // stroke spans ONLY the text band
        {
            int i = (y * W + 205) * 4;
            px[i] = px[i + 1] = px[i + 2] = 0;
        }
        Assert.False(ValueTruncationProbe.IsFlushAgainstRuling(page, Box));
    }

    [Fact]
    public void TinyBox_NotFlagged()
    {
        var page = Page(rulingX: 205, inkX1: 60, inkX2: 203, inkY1: 52, inkY2: 68);
        Assert.False(ValueTruncationProbe.IsFlushAgainstRuling(page, new BoundingBox(40, 50, 200, 52)));
    }
}
