using Foliant.Tables.TableTransformer;
using SkiaSharp;
using Xunit;

namespace Foliant.Tests;

public class RulingGridTests
{
    private static SKBitmap Blank(int w, int h)
    {
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        return bmp;
    }

    private static void HLine(SKBitmap bmp, int y, int x1, int x2)
    {
        using var canvas = new SKCanvas(bmp);
        using var paint = new SKPaint { Color = SKColors.Black, StrokeWidth = 2 };
        canvas.DrawLine(x1, y, x2, y, paint);
    }

    private static void VLine(SKBitmap bmp, int x, int y1, int y2)
    {
        using var canvas = new SKCanvas(bmp);
        using var paint = new SKPaint { Color = SKColors.Black, StrokeWidth = 2 };
        canvas.DrawLine(x, y1, x, y2, paint);
    }

    [Fact]
    public void DetectCells_SimpleGrid_FindsFourLeaves()
    {
        using var bmp = Blank(300, 200);
        HLine(bmp, 100, 0, 300);     // full-width
        VLine(bmp, 150, 0, 200);     // full-height

        var cells = RulingGrid.DetectCells(bmp, SKRect.Create(0, 0, 300, 200));

        Assert.NotNull(cells);
        Assert.Equal(4, cells!.Count);
    }

    [Fact]
    public void DetectCells_HierarchicalForm_FindsSectionLocalColumns()
    {
        // The SF-33 scenario: a full-width line splits two sections; each section has its own
        // column lines that span ONLY that section. A single whole-region grid cannot see
        // them; the recursive decomposition must.
        using var bmp = Blank(400, 300);
        HLine(bmp, 150, 0, 400);     // section divider (full width)
        VLine(bmp, 200, 0, 150);     // top section: 1 column line → 2 cells
        VLine(bmp, 120, 150, 300);   // bottom section: 2 column lines → 3 cells
        VLine(bmp, 260, 150, 300);

        var cells = RulingGrid.DetectCells(bmp, SKRect.Create(0, 0, 400, 300));

        Assert.NotNull(cells);
        Assert.Equal(5, cells!.Count);

        // Top section cells end at the divider; bottom section's narrow column exists.
        Assert.Contains(cells, c => c.Top < 10 && c.Bottom is > 140 and < 160 && c.Left < 10);
        Assert.Contains(cells, c => c.Top is > 140 and < 160 && c.Left is > 110 and < 130);
    }

    [Fact]
    public void DetectCells_UnruledRegion_ReturnsNull()
    {
        using var bmp = Blank(300, 200);
        Assert.Null(RulingGrid.DetectCells(bmp, SKRect.Create(0, 0, 300, 200)));
    }

    [Fact]
    public void DetectCells_HorizontalLinesOnly_TooFewLeaves_ReturnsNull()
    {
        // Underlined prose: one full-width line → only 2 leaves → not a grid.
        using var bmp = Blank(300, 200);
        HLine(bmp, 100, 0, 300);
        Assert.Null(RulingGrid.DetectCells(bmp, SKRect.Create(0, 0, 300, 200)));
    }

    [Fact]
    public void DetectCells_MapsLeavesIntoPageCoordinates()
    {
        using var bmp = Blank(300, 200);
        HLine(bmp, 100, 0, 300);
        VLine(bmp, 150, 0, 200);

        var cells = RulingGrid.DetectCells(bmp, SKRect.Create(500, 1000, 300, 200));

        Assert.NotNull(cells);
        Assert.All(cells!, c =>
        {
            Assert.InRange(c.Left, 500, 800);
            Assert.InRange(c.Top, 1000, 1200);
        });
    }

    [Fact]
    public void FindCuts_MergesThickLines_AndIgnoresBelowPredicate()
    {
        // Thick (3px) line at 50-52, thin line at 90; predicate true only there.
        var hits = new HashSet<int> { 50, 51, 52, 90 };
        var cuts = RulingGrid.FindCuts(0, 120, i => hits.Contains(i));

        Assert.Equal(2, cuts.Count);
        Assert.InRange(cuts[0], 50, 52);
        Assert.Equal(90, cuts[1]);
    }
}
