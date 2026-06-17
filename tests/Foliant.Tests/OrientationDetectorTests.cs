// OrientationDetector is model-free in its logic (it delegates "how readable is this?" to an
// IOcrEngine), so it's tested with a fake OCR whose confidence is the fraction of ink in the
// TOP half of the image it's handed. Upright = ink near the top scores best, so the detector
// should leave a top-heavy page alone and rotate a bottom-heavy (upside-down) page 180°.

using Foliant;
using Foliant.Pipeline;
using Xunit;

namespace Foliant.Tests;

public class OrientationDetectorTests
{
    // Fake OCR: one line whose Confidence = fraction of dark pixels in the top half, and whose
    // recognized-text length is `chars` (so tests can exercise the min-signal floor). Default
    // 200 chars clears the detector's default 100-char floor, isolating the vote/bias logic.
    private sealed class TopHeavyOcr(int chars = 200) : IOcrEngine
    {
        public IReadOnlyList<TextLine> Recognize(PageImage page)
        {
            int w = page.Width, h = page.Height, half = h / 2;
            var px = page.PixelsBgra8888;
            long darkTop = 0, darkAll = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = (y * w + x) * 4;
                    int luma = (px[p] * 114 + px[p + 1] * 587 + px[p + 2] * 299) / 1000;
                    if (luma < 128) { darkAll++; if (y < half) darkTop++; }
                }
            float frac = darkAll == 0 ? 0f : (float)darkTop / darkAll;
            return new[] { new TextLine(new BoundingBox(0, 0, 10, 10), new string('0', chars), frac, TextSource.Ocr) };
        }
        public void Dispose() { }
    }

    // Page with a black horizontal band; `bandTop`/`bandBottom` select which third(s) are inked.
    private static PageImage Banded(bool top, bool bottom, int w = 240, int h = 240)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = 255; px[i + 3] = 255; }
        void Ink(int y0, int y1)
        {
            for (int y = y0; y < y1; y++)
                for (int x = 40; x < w - 40; x++)
                {
                    int p = (y * w + x) * 4;
                    px[p] = px[p + 1] = px[p + 2] = 0;
                }
        }
        if (top) Ink(20, 60);
        if (bottom) Ink(h - 60, h - 20);
        return new PageImage(w, h, 300, px);
    }

    private static double TopInkFraction(PageImage page)
    {
        int w = page.Width, h = page.Height, half = h / 2;
        var px = page.PixelsBgra8888;
        long darkTop = 0, darkAll = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = (y * w + x) * 4;
                int luma = (px[p] * 114 + px[p + 1] * 587 + px[p + 2] * 299) / 1000;
                if (luma < 128) { darkAll++; if (y < half) darkTop++; }
            }
        return darkAll == 0 ? 0 : (double)darkTop / darkAll;
    }

    [Fact]
    public void UprightPage_IsLeftUnchanged()
    {
        var page = Banded(top: true, bottom: false);
        var (img, applied) = new OrientationDetector().Correct(page, new TopHeavyOcr());
        Assert.Equal(0, applied);
        Assert.Same(page, img); // 0° correction returns the input instance
    }

    [Fact]
    public void UpsideDownPage_IsRotated180_AndBecomesTopHeavy()
    {
        var page = Banded(top: false, bottom: true); // ink in the bottom → reads upside-down
        double before = TopInkFraction(page);

        var (img, applied) = new OrientationDetector().Correct(page, new TopHeavyOcr());

        Assert.Equal(180, applied);
        Assert.True(TopInkFraction(img) > before, "correction should move ink toward the top");
        Assert.True(TopInkFraction(img) > 0.5, "corrected page should be top-heavy");
    }

    [Fact]
    public void AmbiguousPage_StaysUpright_DueToBias()
    {
        // Symmetric ink (top and bottom equal) → no orientation clearly wins; upright bias keeps 0°.
        var page = Banded(top: true, bottom: true);
        var (_, applied) = new OrientationDetector().Correct(page, new TopHeavyOcr());
        Assert.Equal(0, applied);
    }

    [Fact]
    public void LowTextPage_IsNotRotated_EvenWhenBottomHeavy()
    {
        // A bottom-heavy page would normally flip 180°, but with too little recognized text
        // (below the min-signal floor) the vote isn't trusted — the page is left upright.
        // This is the real-data fix: illustration/plate pages must not be flipped on noise.
        var page = Banded(top: false, bottom: true);
        var (_, applied) = new OrientationDetector().Correct(page, new TopHeavyOcr(chars: 20)); // < default floor 100
        Assert.Equal(0, applied);
    }

    // Fake OCR returning a fixed TEXT at every orientation, with confidence = (scale × top-ink
    // fraction). The constant text lets us control diversity/confidence while the ink position
    // still decides which orientation wins the vote (upright moves ink to the top).
    private sealed class TextOcr(string text, float confScale = 1f) : IOcrEngine
    {
        public IReadOnlyList<TextLine> Recognize(PageImage page)
        {
            int w = page.Width, h = page.Height, half = h / 2;
            var px = page.PixelsBgra8888;
            long darkTop = 0, darkAll = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = (y * w + x) * 4;
                    int luma = (px[p] * 114 + px[p + 1] * 587 + px[p + 2] * 299) / 1000;
                    if (luma < 128) { darkAll++; if (y < half) darkTop++; }
                }
            float frac = darkAll == 0 ? 0f : (float)darkTop / darkAll;
            return new[] { new TextLine(new BoundingBox(0, 0, 10, 10), text, frac * confScale, TextSource.Ocr) };
        }
        public void Dispose() { }
    }

    private static readonly string RepeatToken = string.Join(" ", Enumerable.Repeat("seal", 40));        // 40 words, 1 distinct
    private static readonly string DiverseText = string.Join(" ", Enumerable.Range(0, 30).Select(i => $"word{i:00}")); // 30 distinct

    [Fact]
    public void DecorativeSealPage_StaysUpright_LowWordDiversity()
    {
        // A library-seal / patterned border OCRs as one token repeated: high char count, high
        // confidence, but near-zero diversity. The vote would flip it 180°; the diversity guard
        // must keep it upright.
        var page = Banded(top: false, bottom: true);              // would otherwise flip 180°
        var (_, applied) = new OrientationDetector().Correct(page, new TextOcr(RepeatToken));
        Assert.Equal(0, applied);
    }

    [Fact]
    public void LowConfidenceTexturePage_StaysUpright()
    {
        // Diverse but low-confidence "text" (texture/pattern hallucination). The mean-confidence
        // guard must keep it upright even though the vote favors a rotation.
        var page = Banded(top: false, bottom: true);
        var (_, applied) = new OrientationDetector().Correct(page, new TextOcr(DiverseText, confScale: 0.35f));
        Assert.Equal(0, applied);
    }

    [Fact]
    public void GenuineDiverseText_StillFlips()
    {
        // Positive control: real, diverse, confident text on an upside-down page must still flip,
        // proving the new guards don't over-suppress genuine rotations.
        var page = Banded(top: false, bottom: true);
        var (_, applied) = new OrientationDetector().Correct(page, new TextOcr(DiverseText));
        Assert.Equal(180, applied);
    }
}
