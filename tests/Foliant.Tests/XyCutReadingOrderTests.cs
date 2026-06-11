using Foliant.Pipeline;
using Xunit;

namespace Foliant.Tests;

public class XyCutReadingOrderTests
{
    private static LayoutRegion R(string label, float x1, float y1, float x2, float y2, float conf = 0.9f) =>
        new(RegionType.Text, label, conf, new BoundingBox(x1, y1, x2, y2));

    [Fact]
    public void Order_SingleColumn_TopToBottom()
    {
        var regions = new[]
        {
            R("c", 0, 200, 100, 280),
            R("a", 0, 0, 100, 80),
            R("b", 0, 100, 100, 180),
        };

        var ordered = new XyCutReadingOrder().Order(regions);
        Assert.Equal(new[] { "a", "b", "c" }, ordered.Select(r => r.RawLabel));
    }

    [Fact]
    public void Order_TwoColumns_ReadsLeftColumnFirst()
    {
        // Two columns separated by a wide vertical gap. Blocks are vertically staggered so
        // no horizontal whitespace band spans the full page (the realistic two-column case —
        // when a full-width horizontal gap exists, row-major order is correct instead).
        var regions = new[]
        {
            R("right-top", 300, 20, 500, 190),
            R("left-bottom", 0, 230, 200, 400),
            R("left-top", 0, 0, 200, 210),
            R("right-bottom", 300, 210, 500, 420),
        };

        var ordered = new XyCutReadingOrder().Order(regions);
        Assert.Equal(
            new[] { "left-top", "left-bottom", "right-top", "right-bottom" },
            ordered.Select(r => r.RawLabel));
    }

    [Fact]
    public void Order_FullWidthHeaderThenColumns()
    {
        var regions = new[]
        {
            R("col-left", 0, 120, 200, 400),
            R("header", 0, 0, 500, 100),
            R("col-right", 300, 120, 500, 400),
        };

        var ordered = new XyCutReadingOrder().Order(regions);
        Assert.Equal(new[] { "header", "col-left", "col-right" }, ordered.Select(r => r.RawLabel));
    }

    [Fact]
    public void SuppressDuplicates_DropsLowerConfidenceOverlap()
    {
        var keep = R("table", 0, 0, 100, 100, conf: 0.95f);
        var dup = R("table", 2, 2, 98, 98, conf: 0.60f);
        var other = R("table", 200, 200, 300, 300, conf: 0.50f);

        var kept = XyCutReadingOrder.SuppressDuplicates(new[] { dup, keep, other });

        Assert.Contains(keep, kept);
        Assert.Contains(other, kept);
        Assert.DoesNotContain(dup, kept);
    }

    [Fact]
    public void SuppressDuplicates_KeepsDifferentLabelsEvenWhenOverlapping()
    {
        var a = R("table", 0, 0, 100, 100, conf: 0.95f);
        var b = new LayoutRegion(RegionType.Figure, "figure", 0.6f, new BoundingBox(2, 2, 98, 98));

        var kept = XyCutReadingOrder.SuppressDuplicates(new[] { a, b });
        Assert.Equal(2, kept.Count);
    }
}
