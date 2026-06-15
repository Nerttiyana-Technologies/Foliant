using Foliant.Pipeline;
using Xunit;

namespace Foliant.Tests;

public class MarkdownComposerTests
{
    private static readonly PageImage Page = new(100, 100, 300, new byte[100 * 100 * 4]);

    private static TextLine L(string text, float x1, float y1, float x2, float y2) =>
        new(new BoundingBox(x1, y1, x2, y2), text, 1f, TextSource.Ocr);

    private sealed class StubTables : ITableExtractor
    {
        public TableExtraction? Result { get; set; }
        public TableExtraction Extract(PageImage page, LayoutRegion table, IReadOnlyList<TextLine> pageLines) =>
            Result ?? new TableExtraction(null, pageLines.Where(l => table.Bounds.ContainsCenterOf(l.Bounds)).ToList());
        public void Dispose() { }
    }

    private static MarkdownComposer NewComposer(StubTables? tables = null) =>
        new(new XyCutReadingOrder(), tables ?? new StubTables());

    [Fact]
    public void Compose_TitleGetsHeadingPrefix()
    {
        var regions = new[]
        {
            new LayoutRegion(RegionType.Title, "title", 0.9f, new BoundingBox(0, 0, 100, 10)),
        };
        var lines = new[] { L("Section C", 1, 1, 50, 9) };

        var composed = NewComposer().Compose(Page, regions, lines);

        Assert.Contains("## Section C", composed.Markdown);
        Assert.Single(composed.Regions);
        Assert.Equal(RegionType.Title, composed.Regions[0].Type);
    }

    [Fact]
    public void Compose_PageFurnitureExcludedFromMarkdownButPreserved()
    {
        var regions = new[]
        {
            new LayoutRegion(RegionType.PageFurniture, "abandon", 0.9f, new BoundingBox(0, 0, 100, 10)),
            new LayoutRegion(RegionType.Text, "plain text", 0.9f, new BoundingBox(0, 20, 100, 90)),
        };
        var lines = new[]
        {
            L("RFP-ABC123-25-R-00001", 1, 1, 60, 9),     // header
            L("Body paragraph", 1, 30, 60, 40),
        };

        var composed = NewComposer().Compose(Page, regions, lines);

        Assert.DoesNotContain("RFP-ABC123", composed.Markdown);
        Assert.Contains("Body paragraph", composed.Markdown);
        Assert.Single(composed.PageFurniture);
        Assert.Equal("RFP-ABC123-25-R-00001", composed.PageFurniture[0].Text);

        // Coverage invariant: furniture is intentional, body is in markdown → nothing lost.
        Assert.Equal(0, ExtractionVerifier.CountLostLines(composed.Markdown, lines, composed.PageFurniture));
    }

    [Fact]
    public void Compose_OrphanLinesAreInsertedByVerticalPosition()
    {
        var regions = new[]
        {
            new LayoutRegion(RegionType.Text, "plain text", 0.9f, new BoundingBox(0, 0, 100, 20)),
            new LayoutRegion(RegionType.Text, "plain text", 0.9f, new BoundingBox(0, 60, 100, 90)),
        };
        var lines = new[]
        {
            L("top region", 1, 5, 50, 15),
            L("orphan in the middle", 1, 35, 70, 45),    // outside both regions
            L("bottom region", 1, 65, 50, 80),
        };

        var composed = NewComposer().Compose(Page, regions, lines);

        int top = composed.Markdown.IndexOf("top region", StringComparison.Ordinal);
        int orphan = composed.Markdown.IndexOf("orphan in the middle", StringComparison.Ordinal);
        int bottom = composed.Markdown.IndexOf("bottom region", StringComparison.Ordinal);

        Assert.True(top >= 0 && orphan > top && bottom > orphan,
            $"expected top < orphan < bottom, got {top}/{orphan}/{bottom}");
        Assert.Equal(0, ExtractionVerifier.CountLostLines(composed.Markdown, lines, composed.PageFurniture));
        Assert.Contains(composed.Regions, r => r.RawLabel == "unassigned");
    }

    [Fact]
    public void Compose_EnumeratorOrder_ReordersNumberedRegions_OnlyWhenEnabled()
    {
        // Three stacked regions whose printed numbers are out of geometric order: top-to-bottom
        // they read 2,1,3. Geometry alone keeps 2,1,3; the enumerator pass must yield 1,2,3.
        var regions = new[]
        {
            new LayoutRegion(RegionType.Text, "plain text", 0.9f, new BoundingBox(0, 0, 100, 20)),
            new LayoutRegion(RegionType.Text, "plain text", 0.9f, new BoundingBox(0, 30, 100, 50)),
            new LayoutRegion(RegionType.Text, "plain text", 0.9f, new BoundingBox(0, 60, 100, 80)),
        };
        var lines = new[]
        {
            L("2. two",   1,  5, 50, 15),
            L("1. one",   1, 35, 50, 45),
            L("3. three", 1, 65, 50, 75),
        };

        var off = NewComposer().Compose(Page, regions, lines, enumeratorReadingOrder: false);
        var on  = NewComposer().Compose(Page, regions, lines, enumeratorReadingOrder: true);

        // Off: geometry preserved → "2. two" precedes "1. one".
        Assert.True(off.Markdown.IndexOf("2. two", StringComparison.Ordinal)
                  < off.Markdown.IndexOf("1. one", StringComparison.Ordinal));

        // On: numeric order → 1, 2, 3.
        int i1 = on.Markdown.IndexOf("1. one", StringComparison.Ordinal);
        int i2 = on.Markdown.IndexOf("2. two", StringComparison.Ordinal);
        int i3 = on.Markdown.IndexOf("3. three", StringComparison.Ordinal);
        Assert.True(i1 >= 0 && i1 < i2 && i2 < i3, $"expected 1<2<3, got {i1}/{i2}/{i3}");

        // No text lost either way.
        Assert.Equal(0, ExtractionVerifier.CountLostLines(on.Markdown, lines, on.PageFurniture));
    }

    [Fact]
    public void RenderTable_EscapesPipesAndEmitsHeaderSeparator()
    {
        var cells = new List<TableCell>
        {
            new(0, 0, "CLIN", new BoundingBox(0, 0, 10, 10)),
            new(0, 1, "Price | Unit", new BoundingBox(10, 0, 20, 10)),
            new(1, 0, "0001", new BoundingBox(0, 10, 10, 20)),
            new(1, 1, "$100", new BoundingBox(10, 10, 20, 20)),
        };
        var extraction = new TableExtraction(new TableStructure(2, 2, cells), Array.Empty<TextLine>());

        string md = MarkdownComposer.RenderTable(extraction);

        Assert.Contains("| CLIN | Price \\| Unit |", md);
        Assert.Contains("|---|---|", md);
        Assert.Contains("| 0001 | $100 |", md);
    }

    [Fact]
    public void RenderTable_NoGrid_FallsBackToParagraphWithAllLines()
    {
        var lines = new[] { L("alpha", 0, 0, 10, 5), L("beta", 0, 10, 10, 15) };
        var extraction = new TableExtraction(null, lines);

        string md = MarkdownComposer.RenderTable(extraction);

        Assert.Contains("alpha", md);
        Assert.Contains("beta", md);
        Assert.DoesNotContain("|---", md);
    }

    [Fact]
    public void RenderTable_UnassignedLinesAlwaysEmitted()
    {
        var cells = new List<TableCell> { new(0, 0, "only cell", new BoundingBox(0, 0, 10, 10)) };
        var leftover = new[] { L("stray note", 0, 50, 30, 60) };
        var extraction = new TableExtraction(new TableStructure(1, 1, cells), leftover);

        string md = MarkdownComposer.RenderTable(extraction);

        Assert.Contains("only cell", md);
        Assert.Contains("stray note", md);
    }
}
