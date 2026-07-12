// Box fidelity (2026-07-07): ValueBoxRefiner trims an emitted value box inward to its real ink
// extent so over-wide proportional-slice boxes stop spilling into the neighbouring cell (the
// STRADDLE class of Gate-3 CROSS-FIELD errors). These tests pin the contract: the trim finds the
// ink edges, is a MONOTONIC SHRINK bounded by the input box, preserves the vertical band, ignores
// speckle, and leaves a blank region untouched.

using Foliant;
using Foliant.Forms.Lilt;
using Xunit;

namespace Foliant.Tests;

public sealed class ValueBoxRefinerTests
{
    private const int W = 300;
    private const int H = 120;

    /// <summary>White BGRA page with one solid black ink block, plus optional extra pixel edits.</summary>
    private static PageImage Page(int inkX1, int inkX2, int inkY1 = 52, int inkY2 = 68, System.Action<byte[]>? extra = null)
    {
        var px = new byte[W * H * 4];
        for (int i = 0; i < px.Length; i++) px[i] = 255;
        for (int x = inkX1; x < inkX2; x++)
            for (int y = inkY1; y < inkY2; y++)
            {
                int i = (y * W + x) * 4;
                px[i] = px[i + 1] = px[i + 2] = 0;
            }
        extra?.Invoke(px);
        return new PageImage(W, H, 96, px);
    }

    // Over-wide value box (40..200) around ink that only occupies part of it — the proportional slice.
    private static readonly BoundingBox WideBox = new(40, 50, 200, 70);

    [Fact]
    public void TrimsTrailingWhitespace()
    {
        // ink 60..120 inside a box that runs to 200 → right edge pulls back to the ink
        var r = ValueBoxRefiner.InkTrim(Page(60, 120), WideBox);
        Assert.Equal(60f, r.X1);
        Assert.Equal(120f, r.X2);
    }

    [Fact]
    public void TrimsLeadingWhitespace()
    {
        // ink 100..160 → left edge pulls in from 40 to 100
        var r = ValueBoxRefiner.InkTrim(Page(100, 160), WideBox);
        Assert.Equal(100f, r.X1);
        Assert.Equal(160f, r.X2);
    }

    [Fact]
    public void PreservesVerticalBand()
    {
        // the probe relies on the full line height — Y edges must not move
        var r = ValueBoxRefiner.InkTrim(Page(60, 120), WideBox);
        Assert.Equal(WideBox.Y1, r.Y1);
        Assert.Equal(WideBox.Y2, r.Y2);
    }

    [Fact]
    public void NeverGrowsBeyondInputBox()
    {
        // ink wider than the box (10..290) → the trim clamps to the box, never exceeds it
        var r = ValueBoxRefiner.InkTrim(Page(10, 290), WideBox);
        Assert.True(r.X1 >= WideBox.X1);
        Assert.True(r.X2 <= WideBox.X2);
    }

    [Fact]
    public void BlankRegion_ReturnsInputUnchanged()
    {
        // no ink anywhere in the box → keep the original prediction rather than collapse to nothing
        var r = ValueBoxRefiner.InkTrim(Page(0, 0), WideBox);
        Assert.Equal(WideBox.X1, r.X1);
        Assert.Equal(WideBox.X2, r.X2);
        Assert.Equal(WideBox.Y1, r.Y1);
        Assert.Equal(WideBox.Y2, r.Y2);
    }

    [Fact]
    public void IgnoresSpeckle()
    {
        // a lone dark pixel far to the right (JPEG speckle) must not re-extend the trimmed box
        var page = Page(60, 120, extra: px =>
        {
            int i = (55 * W + 190) * 4;
            px[i] = px[i + 1] = px[i + 2] = 0;
        });
        var r = ValueBoxRefiner.InkTrim(page, WideBox);
        Assert.Equal(120f, r.X2);   // stays at the real ink edge, not the speckle at 190
    }
}
