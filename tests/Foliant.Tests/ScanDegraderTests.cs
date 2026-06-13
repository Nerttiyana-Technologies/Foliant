// ScanDegrader transforms are pure and model-free, so they're tested directly on synthetic
// pages: dimension/DPI invariants, determinism, and the qualitative effect of each degradation
// (180° flips corners, blur softens a hard edge, fade compresses the luma range, etc.).

using Foliant;
using Foliant.Pipeline;
using Xunit;

namespace Foliant.Tests;

public class ScanDegraderTests
{
    private static PageImage Img(int w, int h, Func<int, int, (byte b, byte g, byte r)> f, int dpi = 300)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int p = (y * w + x) * 4;
                var (b, g, r) = f(x, y);
                px[p] = b; px[p + 1] = g; px[p + 2] = r; px[p + 3] = 255;
            }
        return new PageImage(w, h, dpi, px);
    }

    private static (byte b, byte g, byte r) At(PageImage im, int x, int y)
    {
        int p = (y * im.Width + x) * 4;
        var q = im.PixelsBgra8888;
        return (q[p], q[p + 1], q[p + 2]);
    }

    private static int Luma(PageImage im, int x, int y)
    {
        var (b, g, r) = At(im, x, y);
        return (b * 114 + g * 587 + r * 299) / 1000;
    }

    // Left half black, right half white — a single hard vertical edge at x = w/2.
    private static PageImage SplitPage(int w = 200, int h = 200) =>
        Img(w, h, (x, _) => x < w / 2 ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255));

    [Fact]
    public void Identity_LeavesPixelsUnchanged()
    {
        var p = SplitPage();
        var outp = ScanDegrader.Identity.Transform(p);
        Assert.Equal(p.Width, outp.Width);
        Assert.Equal(p.Height, outp.Height);
        Assert.Equal(p.PixelsBgra8888, outp.PixelsBgra8888);
    }

    [Fact]
    public void Rotate180_PreservesDimensions_AndFlipsCorners()
    {
        // Black top-left quadrant, white elsewhere.
        var p = Img(160, 120, (x, y) => (x < 80 && y < 60) ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255));
        var r = ScanDegrader.Rotate(180).Transform(p);

        Assert.Equal(p.Width, r.Width);
        Assert.Equal(p.Height, r.Height);
        Assert.Equal(p.Dpi, r.Dpi);
        // The black block should now sit in the bottom-right quadrant.
        Assert.True(Luma(r, 120, 90) < 40, "expected dark in bottom-right after 180° rotation");
        Assert.True(Luma(r, 40, 30) > 215, "expected light in top-left after 180° rotation");
    }

    [Fact]
    public void Rotate90_SwapsDimensions()
    {
        var p = SplitPage(200, 120);
        var r = ScanDegrader.Rotate(90).Transform(p);
        Assert.Equal(p.Height, r.Width);
        Assert.Equal(p.Width, r.Height);
        Assert.Equal(p.Dpi, r.Dpi);
    }

    [Fact]
    public void RotateSkew_ExpandsCanvas_PreservesDpi()
    {
        var p = SplitPage(200, 200);
        var r = ScanDegrader.Rotate(7).Transform(p);
        Assert.True(r.Width >= p.Width && r.Height >= p.Height, "small skew should expand the canvas");
        Assert.Equal(p.Dpi, r.Dpi);
    }

    [Fact]
    public void Jpeg_PreservesDimensionsAndDpi()
    {
        var p = SplitPage();
        var j = ScanDegrader.JpegRecompress(20).Transform(p);
        Assert.Equal(p.Width, j.Width);
        Assert.Equal(p.Height, j.Height);
        Assert.Equal(p.Dpi, j.Dpi);
        Assert.Equal(p.PixelsBgra8888.Length, j.PixelsBgra8888.Length);
    }

    [Fact]
    public void GaussianNoise_IsDeterministic_PreservesAlpha_AndPerturbsPixels()
    {
        var p = Img(64, 64, (_, _) => (128, 128, 128));
        var a = ScanDegrader.GaussianNoise(20).Transform(p);
        var b = ScanDegrader.GaussianNoise(20).Transform(p);

        Assert.Equal(a.PixelsBgra8888, b.PixelsBgra8888);          // deterministic
        Assert.NotEqual(p.PixelsBgra8888, a.PixelsBgra8888);       // actually perturbs
        for (int i = 3; i < a.PixelsBgra8888.Length; i += 4)
            Assert.Equal(255, a.PixelsBgra8888[i]);                // alpha untouched
    }

    [Fact]
    public void Blur_SoftensHardEdge()
    {
        var p = SplitPage(200, 40);
        var b = ScanDegrader.GaussianBlur(2.5f).Transform(p);
        Assert.Equal(p.Width, b.Width);
        Assert.Equal(p.Height, b.Height);
        // At the original hard boundary the luma should now be a mid value, not pure 0/255.
        int edge = Luma(b, 100, 20);
        Assert.InRange(edge, 20, 235);
    }

    [Fact]
    public void Downscale_PreservesDimensions_AndAltersHighFrequencyDetail()
    {
        // 1px checkerboard: maximal high-frequency content.
        var p = Img(120, 120, (x, y) => ((x + y) % 2 == 0) ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255));
        var d = ScanDegrader.Downscale(72).Transform(p);
        Assert.Equal(p.Width, d.Width);
        Assert.Equal(p.Height, d.Height);
        Assert.Equal(p.Dpi, d.Dpi);
        Assert.NotEqual(p.PixelsBgra8888, d.PixelsBgra8888); // detail below target DPI is lost
    }

    [Fact]
    public void Downscale_NoOp_WhenTargetAtOrAboveSourceDpi()
    {
        var p = SplitPage();
        var d = ScanDegrader.Downscale(300).Transform(p); // equal to source dpi → unchanged
        Assert.Equal(p.PixelsBgra8888, d.PixelsBgra8888);
    }

    [Fact]
    public void FadeContrast_CompressesLumaRange()
    {
        var p = SplitPage(100, 20);
        var f = ScanDegrader.FadeContrast(0.4).Transform(p);
        int dark = Luma(f, 10, 10);   // was 0
        int light = Luma(f, 90, 10);  // was 255
        Assert.True(dark > 0, "blacks should lift toward gray");
        Assert.True(light < 255, "whites should dim toward gray");
        Assert.True(light - dark < 255, "overall range should be compressed");
    }

    [Fact]
    public void FadeContrast_NoOp_WhenKeepIsOne()
    {
        var p = SplitPage();
        var f = ScanDegrader.FadeContrast(1.0).Transform(p);
        Assert.Equal(p.PixelsBgra8888, f.PixelsBgra8888);
    }

    [Fact]
    public void Compose_AppliesTransformsInOrder()
    {
        var p = SplitPage(200, 120);
        var composed = ScanDegrader.Compose(ScanDegrader.Rotate(90), ScanDegrader.FadeContrast(0.5)).Transform(p);
        // Rotate(90) swaps dims; fade keeps dims — so the net is the swapped dimensions.
        Assert.Equal(p.Height, composed.Width);
        Assert.Equal(p.Width, composed.Height);
    }
}
