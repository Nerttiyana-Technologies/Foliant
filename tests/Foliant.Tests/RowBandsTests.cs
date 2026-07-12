// Merged-row det-box splitting (2026-07-06, TD-41 GARBLED diagnosis): the DB prob map on
// blurry upscaled scans bridges vertically adjacent text rows into one connected component;
// PaddleOcrEngine.RowBands finds the ink-projection valley so the rows can be recognized
// separately (keep-better vs the whole-box reading decides in RecognizeBox).
// NOTE: the feature is DEFAULT OFF — measured net-negative on the Gate 3 scanned-holdout
// ledger (2026-07-06: spurious 325→543 for garbled 95→94). It is kept as a measurement rig
// (harness --row-split); these tests pin the band detector's geometry contract so the rig
// stays trustworthy for future rec-model/tuning experiments.

using Foliant.Ocr.PaddleOcr;
using SkiaSharp;
using Xunit;

namespace Foliant.Tests;

public sealed class RowBandsTests
{
    /// <summary>Bitmap with black horizontal stripes at the given (top, height) row ranges.</summary>
    private static SKBitmap Stripes(int width, int height, params (int Top, int Rows)[] stripes)
    {
        var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black };
        foreach (var (top, rows) in stripes)
            canvas.DrawRect(0, top, width, rows, paint);
        return bmp;
    }

    [Fact]
    public void TwoRowsWithCleanGap_SplitIntoTwoBands()
    {
        using var bmp = Stripes(200, 60, (5, 15), (40, 15));
        var bands = PaddleOcrEngine.RowBands(bmp);

        Assert.Equal(2, bands.Count);
        Assert.True(bands[0].Top <= 5 && bands[0].Bottom >= 19 && bands[0].Bottom <= 40);
        Assert.True(bands[1].Top >= 20 && bands[1].Top <= 40 && bands[1].Bottom >= 54);
    }

    [Fact]
    public void SingleRow_NoSplit()
    {
        using var bmp = Stripes(200, 30, (8, 14));
        var bands = PaddleOcrEngine.RowBands(bmp);

        Assert.Single(bands);
        Assert.Equal((0, 30), bands[0]);
    }

    [Fact]
    public void NoisyGap_DottedLeaderInkBelowRelativeThreshold_StillSplits()
    {
        // Real inter-row gaps on TD-41 scans carry a few ink px (dotted leaders, JPEG noise):
        // the valley threshold is relative (5% of peak row ink), not literal zero.
        using var bmp = Stripes(200, 60, (5, 15), (40, 15));
        using (var canvas = new SKCanvas(bmp))
        using (var paint = new SKPaint { Color = SKColors.Black })
            for (int x = 0; x < 8; x++)                     // 8 noise px per gap row (< 5% of 200)
                canvas.DrawRect(x * 25, 25, 1, 8, paint);
        var bands = PaddleOcrEngine.RowBands(bmp);

        Assert.Equal(2, bands.Count);
    }

    [Fact]
    public void SolidBlock_NoInteriorValley_NoSplit()
    {
        using var bmp = Stripes(200, 60, (5, 50));
        var bands = PaddleOcrEngine.RowBands(bmp);

        Assert.Single(bands);
    }

    [Fact]
    public void SliverBelowMinBandHeight_IsDropped_NoSplit()
    {
        // A 4-row sliver (e.g. clipped descenders from the row above the det box) must not
        // count as a second text row.
        using var bmp = Stripes(200, 60, (2, 4), (30, 20));
        var bands = PaddleOcrEngine.RowBands(bmp);

        Assert.Single(bands);
    }

    [Fact]
    public void BlankBitmap_SingleFullHeightBand()
    {
        using var bmp = Stripes(200, 40);
        var bands = PaddleOcrEngine.RowBands(bmp);

        Assert.Single(bands);
        Assert.Equal((0, 40), bands[0]);
    }

    [Fact]
    public void ThreeRows_SplitIntoThreeBands()
    {
        using var bmp = Stripes(300, 100, (5, 18), (38, 18), (72, 18));
        var bands = PaddleOcrEngine.RowBands(bmp);

        Assert.Equal(3, bands.Count);
    }
}
