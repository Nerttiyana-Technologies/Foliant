// ClassicalScanUpscaler is pure and model-free, so it's tested directly: dimension/DPI
// invariants under upscaling, and the no-op guards for factors that don't enlarge.

using Foliant;
using Foliant.Pipeline;
using Xunit;

namespace Foliant.Tests;

public class ClassicalScanUpscalerTests
{
    private static PageImage Solid(int w, int h, int dpi = 300)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = 128; px[i + 1] = 128; px[i + 2] = 128; px[i + 3] = 255;
        }
        return new PageImage(w, h, dpi, px);
    }

    [Fact]
    public void Upscale_DoublesDimensions_PreservesDpi()
    {
        var upscaler = new ClassicalScanUpscaler();
        var src = Solid(50, 40, dpi: 300);

        var result = upscaler.Upscale(src, 2.0f);

        Assert.Equal(100, result.Width);
        Assert.Equal(80, result.Height);
        Assert.Equal(300, result.Dpi);                       // Dpi is nominal render DPI, unchanged
        Assert.Equal(100 * 80 * 4, result.PixelsBgra8888.Length);
    }

    [Fact]
    public void Upscale_NonIntegerFactor_RoundsDimensions()
    {
        var upscaler = new ClassicalScanUpscaler();
        var src = Solid(50, 50);

        var result = upscaler.Upscale(src, 1.5f);

        Assert.Equal(75, result.Width);
        Assert.Equal(75, result.Height);
    }

    [Theory]
    [InlineData(1.0f)]
    [InlineData(0.5f)]
    public void Upscale_FactorNotEnlarging_ReturnsOriginalInstance(float factor)
    {
        var upscaler = new ClassicalScanUpscaler();
        var src = Solid(50, 50);

        var result = upscaler.Upscale(src, factor);

        Assert.Same(src, result);                            // no-op guard: same object back
    }

    [Fact]
    public void Upscale_PreservesAFlatField()
    {
        // A uniform gray field must stay uniform gray after cubic resampling (no edge ringing
        // to introduce variation), so the upscale is content-faithful on flat regions.
        var upscaler = new ClassicalScanUpscaler();
        var src = Solid(40, 40);

        var result = upscaler.Upscale(src, 2.0f);

        var px = result.PixelsBgra8888;
        int mid = (result.Height / 2 * result.Width + result.Width / 2) * 4;
        Assert.InRange(px[mid], 126, 130);
        Assert.InRange(px[mid + 1], 126, 130);
        Assert.InRange(px[mid + 2], 126, 130);
    }
}
