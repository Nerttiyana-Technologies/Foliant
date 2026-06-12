using Foliant.Pipeline;
using Xunit;

namespace Foliant.Tests;

public class XyCutPlusPlusReadingOrderTests
{
    private static LayoutRegion R(
        string label, float x1, float y1, float x2, float y2,
        RegionType type = RegionType.Text, float conf = 0.9f) =>
        new(type, label, conf, new BoundingBox(x1, y1, x2, y2));

    // ── Parity with plain XY-cut on the easy cases ───────────────────────────

    [Fact]
    public void Order_SingleColumn_TopToBottom()
    {
        var regions = new[]
        {
            R("c", 0, 200, 100, 280),
            R("a", 0, 0, 100, 80),
            R("b", 0, 100, 100, 180),
        };

        var ordered = new XyCutPlusPlusReadingOrder().Order(regions);
        Assert.Equal(new[] { "a", "b", "c" }, ordered.Select(r => r.RawLabel));
    }

    [Fact]
    public void Order_TwoColumnsStaggered_ReadsLeftColumnFirst()
    {
        var regions = new[]
        {
            R("right-top", 300, 20, 500, 190),
            R("left-bottom", 0, 230, 200, 400),
            R("left-top", 0, 0, 200, 210),
            R("right-bottom", 300, 210, 500, 420),
        };

        var ordered = new XyCutPlusPlusReadingOrder().Order(regions);
        Assert.Equal(
            new[] { "left-top", "left-bottom", "right-top", "right-bottom" },
            ordered.Select(r => r.RawLabel));
    }

    // ── The cases plain XY-cut gets wrong ────────────────────────────────────

    [Fact]
    public void Order_HeaderOverAlignedColumns_ReadsColumnMajor()
    {
        // Row-aligned column blocks: a horizontal whitespace band spans the full page
        // between the rows, so plain XY-cut (horizontal-first) interleaves the columns
        // (L1, R1, L2, R2). The full-width title is masked as a cross-layout element and
        // the 100 px column gutter out-weighs the 20 px row gap → column-major.
        var regions = new[]
        {
            R("header", 0, 0, 500, 40, RegionType.Title),
            R("L1", 0, 60, 200, 200),
            R("R1", 300, 60, 500, 200),
            R("L2", 0, 220, 200, 360),
            R("R2", 300, 220, 500, 360),
        };

        var ordered = new XyCutPlusPlusReadingOrder().Order(regions);
        Assert.Equal(
            new[] { "header", "L1", "L2", "R1", "R2" },
            ordered.Select(r => r.RawLabel));

        // Document the differentiator: plain XY-cut interleaves this layout.
        var plain = new XyCutReadingOrder().Order(regions);
        Assert.Equal(
            new[] { "header", "L1", "R1", "L2", "R2" },
            plain.Select(r => r.RawLabel));
    }

    [Fact]
    public void Order_MidPageFullWidthTable_SeparatesBands()
    {
        // Columns above AND below a full-width table. The table is masked and acts as a
        // band separator: upper band column-major, table, lower band column-major.
        var regions = new[]
        {
            R("upper-L", 0, 0, 200, 150),
            R("upper-R", 300, 0, 500, 150),
            R("table", 0, 170, 500, 300, RegionType.Table),
            R("lower-L", 0, 320, 200, 470),
            R("lower-R", 300, 320, 500, 470),
        };

        var ordered = new XyCutPlusPlusReadingOrder().Order(regions);
        Assert.Equal(
            new[] { "upper-L", "upper-R", "table", "lower-L", "lower-R" },
            ordered.Select(r => r.RawLabel));
    }

    [Fact]
    public void Order_DenseForm_ReadsRowMajor()
    {
        // Form-style grid: the 40 px row band beats the 20 px cell gutter → row-major,
        // which is the correct reading for forms (label/value cells flow across).
        var regions = new[]
        {
            R("a", 0, 0, 240, 100),
            R("b", 260, 0, 500, 100),
            R("c", 0, 140, 240, 240),
            R("d", 260, 140, 500, 240),
        };

        var ordered = new XyCutPlusPlusReadingOrder().Order(regions);
        Assert.Equal(new[] { "a", "b", "c", "d" }, ordered.Select(r => r.RawLabel));
    }

    // ── Masking guards ───────────────────────────────────────────────────────

    [Fact]
    public void Order_FewRegions_NoMaskingStillCorrect()
    {
        // Below the 4-region minimum the median is meaningless; falls back to pure cuts.
        var regions = new[]
        {
            R("header", 0, 0, 500, 100, RegionType.Title),
            R("col-left", 0, 120, 200, 400),
            R("col-right", 300, 120, 500, 400),
        };

        var ordered = new XyCutPlusPlusReadingOrder().Order(regions);
        Assert.Equal(new[] { "header", "col-left", "col-right" }, ordered.Select(r => r.RawLabel));
    }

    [Fact]
    public void Order_WideTextRegion_IsNotMasked()
    {
        // Only Title/Table/Figure are cross-layout candidates: a wide paragraph (e.g. a
        // full-width intro above columns) must not be torn out of the flow.
        var regions = new[]
        {
            R("intro", 0, 0, 500, 80, RegionType.Text),
            R("L1", 0, 100, 200, 240),
            R("R1", 300, 100, 500, 240),
            R("L2", 0, 260, 200, 400),
            R("R2", 300, 260, 500, 400),
        };

        var ordered = new XyCutPlusPlusReadingOrder().Order(regions);
        Assert.Equal("intro", ordered[0].RawLabel);
    }

    [Fact]
    public void Order_SingleColumnWithTitle_NothingMasked_OrderUnchanged()
    {
        // Single column: median width ≈ column width, so the β threshold never fires
        // and the result matches plain XY-cut exactly.
        var regions = new[]
        {
            R("title", 0, 0, 480, 50, RegionType.Title),
            R("p1", 0, 70, 500, 200),
            R("p2", 0, 220, 490, 350),
            R("p3", 0, 370, 500, 500),
        };

        var ordered = new XyCutPlusPlusReadingOrder().Order(regions);
        var plain = new XyCutReadingOrder().Order(regions);
        Assert.Equal(plain.Select(r => r.RawLabel), ordered.Select(r => r.RawLabel));
    }

    [Fact]
    public void Order_EmptyAndSingle_Degenerate()
    {
        var ro = new XyCutPlusPlusReadingOrder();
        Assert.Empty(ro.Order(Array.Empty<LayoutRegion>()));

        var one = new[] { R("only", 0, 0, 100, 100) };
        Assert.Single(ro.Order(one));
    }
}
