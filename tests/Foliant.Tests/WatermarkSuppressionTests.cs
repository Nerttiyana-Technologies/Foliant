// Watermark suppression is deterministic, so it is tested with synthetic pages:
// a sparse diagonal red overlay (DRAFT-stamp signature) must be whitened with the
// black text underneath intact; legitimate colored content — compact headers,
// dense figures — must survive untouched.

using Foliant.Pipeline;
using Xunit;

namespace Foliant.Tests;

public class WatermarkSuppressionTests
{
    private const int W = 800;
    private const int H = 1000;

    private static byte[] BlankPage()
    {
        var px = new byte[W * H * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = px[i + 1] = px[i + 2] = 255;
            px[i + 3] = 255;
        }
        return px;
    }

    private static void Set(byte[] px, int x, int y, byte b, byte g, byte r)
    {
        int p = (y * W + x) * 4;
        px[p] = b; px[p + 1] = g; px[p + 2] = r;
    }

    private static void AddTextStripes(byte[] px)
    {
        for (int line = 0; line < 20; line++)
        for (int x = 80; x < 720; x++)
        for (int y = 100 + line * 40; y < 108 + line * 40; y++)
            Set(px, x, y, 0, 0, 0);
    }

    /// <summary>Sparse diagonal red strokes spanning most of the page — the stamp signature.</summary>
    private static void AddRedDiagonalWatermark(byte[] px)
    {
        for (int d = 0; d < 14; d++)                    // 14 diagonal strokes
        for (int t = 0; t < W + H; t += 2)
        for (int wdt = 0; wdt < 3; wdt++)               // 3px wide → ~3% of page
        {
            int x = 60 + (int)(t * 0.55) + d * 12 + wdt;
            int y = 60 + (int)(t * 0.62);
            if (x is >= 0 and < W && y is >= 0 and < H)
                Set(px, x, y, 60, 60, 220);             // saturated red (BGR)
        }
    }

    private static byte Red(PageImage img, int x, int y) =>
        img.PixelsBgra8888[(y * img.Width + x) * 4 + 2];

    private static byte LumaAt(PageImage img, int x, int y)
    {
        int p = (y * img.Width + x) * 4;
        var px = img.PixelsBgra8888;
        return (byte)((px[p] * 114 + px[p + 1] * 587 + px[p + 2] * 299) / 1000);
    }

    [Fact]
    public void RedDiagonalStamp_IsSuppressed_TextSurvives()
    {
        var px = BlankPage();
        AddTextStripes(px);
        AddRedDiagonalWatermark(px);

        var result = new DefaultPagePreprocessor().Process(new PageImage(W, H, 300, px));

        Assert.True(result.WatermarkSuppressed);

        // Watermark pixels whitened: sample where strokes ran but text didn't (y=300 band gap)
        var img = result.Image;
        int redLeft = 0;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x += 7)
        {
            int p = (y * img.Width + x) * 4;
            var pix = img.PixelsBgra8888;
            if (pix[p + 2] > 180 && pix[p] < 120 && pix[p + 1] < 120) redLeft++;
        }
        Assert.True(redLeft < 20, $"red watermark pixels remain: {redLeft}");

        // Black text intact
        Assert.True(LumaAt(img, 400, 104) < 60, "text ink must survive watermark removal");
    }

    [Fact]
    public void CompactColoredHeader_IsPreserved()
    {
        var px = BlankPage();
        AddTextStripes(px);
        // Legitimate content: a solid blue header band (compact bbox, dense fill)
        for (int y = 30; y < 70; y++)
        for (int x = 80; x < 720; x++)
            Set(px, x, y, 200, 120, 30);                // blue-ish (BGR)

        var result = new DefaultPagePreprocessor().Process(new PageImage(W, H, 300, px));

        Assert.False(result.WatermarkSuppressed);
        int p = (50 * W + 400) * 4;
        Assert.True(result.Image.PixelsBgra8888[p] > 150, "header band must be preserved");
    }

    [Fact]
    public void DenseColoredFigure_IsPreserved()
    {
        var px = BlankPage();
        AddTextStripes(px);
        // A large but DENSE green chart area — fails the strokes-not-fills density guard
        for (int y = 250; y < 750; y++)
        for (int x = 150 ; x < 650; x++)
            if (((x + y) / 4) % 2 == 0) Set(px, x, y, 60, 190, 60);

        var result = new DefaultPagePreprocessor().Process(new PageImage(W, H, 300, px));

        Assert.False(result.WatermarkSuppressed);
    }

    [Fact]
    public void ScatteredHyperlinks_ArePreserved()
    {
        // The measured false positive: blue link text scattered over the whole page
        // (sparse, page-spanning, low density — but HORIZONTAL, so corr(x,y) ≈ 0).
        var px = BlankPage();
        AddTextStripes(px);
        var rng = new Random(7);
        for (int link = 0; link < 120; link++)
        {
            int lx = rng.Next(60, 600), ly = 90 + rng.Next(0, 23) * 38;
            for (int x = lx; x < lx + 90 && x < W; x++)
            for (int y = ly; y < ly + 6; y++)
                Set(px, x, y, 200, 80, 20);             // saturated blue (BGR)
        }

        var result = new DefaultPagePreprocessor().Process(new PageImage(W, H, 300, px));

        Assert.False(result.WatermarkSuppressed,
            "horizontal hyperlink text must not be mistaken for a diagonal stamp");
    }

    [Fact]
    public void CleanPage_Unchanged()
    {
        var px = BlankPage();
        AddTextStripes(px);
        var page = new PageImage(W, H, 300, px);
        var result = new DefaultPagePreprocessor().Process(page);

        Assert.False(result.WatermarkSuppressed);
        Assert.False(result.Changed);
        Assert.Same(page, result.Image);
    }
}
