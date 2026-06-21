using Foliant;
using Foliant.Pipeline;
using Xunit;

namespace Foliant.Tests;

// The federal-only table renderer: splits a collapsed multi-row schedule (SF1449 blocks 19-24) into one
// row per vertical band, but leaves a normal single-row data table untouched.
public sealed class FederalFormTableRendererTests
{
    private static TextLine L(string s, float x1, float y) =>
        new(new BoundingBox(x1, y, x1 + 10, y + 8), s, 1f, TextSource.TextLayer);

    [Fact]
    public void Render_SplitsCollapsedMultiRowSchedule()
    {
        // Header + ONE predicted data row whose cells each span three vertically-separated items.
        var cells = new[]
        {
            new TableCell(0, 0, "Item", new BoundingBox(0, 0, 50, 4)),
            new TableCell(0, 1, "Desc", new BoundingBox(50, 0, 100, 4)),
            new TableCell(1, 0, "1 2 3", new BoundingBox(0, 5, 50, 55)),
            new TableCell(1, 1, "A B C", new BoundingBox(50, 5, 100, 55)),
        };
        var t = new TableStructure(2, 2, cells);
        var regionLines = new[]
        {
            L("1", 5, 8),  L("A", 60, 8),
            L("2", 5, 28), L("B", 60, 28),
            L("3", 5, 48), L("C", 60, 48),
        };

        string md = FederalFormTableRenderer.Render(new TableExtraction(t, Array.Empty<TextLine>()), regionLines);

        Assert.Contains("| 1 | A |", md);
        Assert.Contains("| 2 | B |", md);
        Assert.Contains("| 3 | C |", md);
        Assert.DoesNotContain("1 2 3", md);   // the collapsed mega-cell is gone
    }

    [Fact]
    public void Render_LeavesNormalSingleRowTableUntouched()
    {
        var cells = new[]
        {
            new TableCell(0, 0, "Name", new BoundingBox(0, 0, 50, 8)),
            new TableCell(0, 1, "Qty",  new BoundingBox(50, 0, 100, 8)),
            new TableCell(1, 0, "Widget", new BoundingBox(0, 10, 50, 18)),
            new TableCell(1, 1, "5",      new BoundingBox(50, 10, 100, 18)),
        };
        var t = new TableStructure(2, 2, cells);
        var regionLines = new[]
        {
            new TextLine(new BoundingBox(2, 11, 40, 17), "Widget", 1f, TextSource.TextLayer),
            new TextLine(new BoundingBox(52, 11, 60, 17), "5", 1f, TextSource.TextLayer),
        };

        string md = FederalFormTableRenderer.Render(new TableExtraction(t, Array.Empty<TextLine>()), regionLines);

        Assert.Contains("| Widget | 5 |", md);
    }
}
