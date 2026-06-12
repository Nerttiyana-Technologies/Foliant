// DefaultPagePreprocessor is deterministic by design, so it is tested with synthetic
// pages: known skew in → measured correction out; flat histogram in → stretched out;
// salt-and-pepper in → whitened out; and the do-no-harm cases (clean pages untouched).

using Foliant.Pipeline;
using Xunit;

namespace Foliant.Tests;

public class DefaultPagePreprocessorTests
{
    private const int W = 800;
    private const int H = 1000;

    /// <summary>White page with horizontal "text line" stripes, optionally skewed.</summary>
    private static PageImage SyntheticPage(
        float skewDegrees = 0f, byte ink = 0, byte paper = 255, int speckCount = 0)
    {
        var px = new byte[W * H * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = px[i + 1] = px[i + 2] = paper;
            px[i + 3] = 255;
        }

        double tan = Math.Tan(skewDegrees * Math.PI / 180.0);
        // 20 stripes of "text": 8px tall lines every 40px, x ∈ [80, 720]
        for (int line = 0; line < 20; line++)
        {
            int baseY = 100 + line * 40;
            for (int x = 80; x < 720; x++)
            {
                int yTop = baseY + (int)(x * tan);
                for (int y = yTop; y < yTop + 8; y++)
                {
                    if (y < 0 || y >= H) continue;
                    int p = (y * W + x) * 4;
                    px[p] = px[p + 1] = px[p + 2] = ink;
                }
            }
        }

        var rng = new Random(42);
        for (int s = 0; s < speckCount; s++)
        {
            int x = rng.Next(2, W - 2), y = rng.Next(2, H - 2);
            int p = (y * W + x) * 4;
            // only place isolated specks on paper
            px[p] = px[p + 1] = px[p + 2] = 10;
        }

        return new PageImage(W, H, 300, px);
    }

    private static byte LumaAt(PageImage img, int x, int y)
    {
        int p = (y * img.Width + x) * 4;
        var px = img.PixelsBgra8888;
        return (byte)((px[p] * 114 + px[p + 1] * 587 + px[p + 2] * 299) / 1000);
    }

    [Theory]
    [InlineData(2.0f)]
    [InlineData(-3.5f)]
    [InlineData(1.0f)]
    public void Deskew_CorrectsKnownSkew(float skew)
    {
        var result = new DefaultPagePreprocessor().Process(SyntheticPage(skewDegrees: skew));

        Assert.Equal(skew, result.SkewCorrectedDegrees, 0.3f);

        // After rotation the stripes must be horizontal again: a stripe row probed at
        // two distant columns should be dark on the SAME y (within stripe thickness).
        // Probe WELL INSIDE the stripes (x ∈ [80,720]): rotation about the page center
        // also shifts content horizontally near the top (≈26px at 3.5°), so columns
        // near a stripe end can fall off the stripe and hit the next one instead.
        var img = result.Image;
        int yLeft = FindFirstInkY(img, 250), yRight = FindFirstInkY(img, 550);
        Assert.True(Math.Abs(yLeft - yRight) <= 6,
            $"stripes still skewed after correction: y(250)={yLeft} vs y(550)={yRight}");
    }

    [Fact]
    public void Deskew_LeavesStraightPagesAlone()
    {
        var result = new DefaultPagePreprocessor().Process(SyntheticPage(skewDegrees: 0f));
        Assert.Equal(0f, result.SkewCorrectedDegrees);
    }

    [Fact]
    public void Contrast_StretchesFlatHistogram()
    {
        // Faded scan: gray text (120) on light-gray paper (200)
        var result = new DefaultPagePreprocessor().Process(SyntheticPage(ink: 120, paper: 200));

        Assert.True(result.ContrastStretched);
        Assert.True(LumaAt(result.Image, 10, 10) > 230, "paper should be near-white after stretch");
        Assert.True(LumaAt(result.Image, 100, 104) < 60, "ink should be near-black after stretch");
    }

    [Fact]
    public void Contrast_LeavesCleanPagesAlone()
    {
        var result = new DefaultPagePreprocessor().Process(SyntheticPage());
        Assert.False(result.ContrastStretched);
    }

    [Fact]
    public void Despeckle_RemovesSaltAndPepper()
    {
        // 0.2% speck density — well above the 0.05% threshold
        var noisy = SyntheticPage(speckCount: (int)(W * H * 0.002));
        var result = new DefaultPagePreprocessor().Process(noisy);

        Assert.True(result.Denoised);

        // The text stripes must survive despeckling
        Assert.True(LumaAt(result.Image, 400, 104) < 60, "text ink must not be despeckled away");
    }

    [Fact]
    public void CleanPage_PassesThroughUnchanged()
    {
        var page = SyntheticPage();
        var result = new DefaultPagePreprocessor().Process(page);

        Assert.False(result.Changed);
        Assert.Same(page, result.Image);   // no needless buffer copy on the clean path
    }

    private static int FindFirstInkY(PageImage img, int x)
    {
        for (int y = 0; y < img.Height; y++)
            if (LumaAt(img, x, y) < 100) return y;
        return -1;
    }
}
