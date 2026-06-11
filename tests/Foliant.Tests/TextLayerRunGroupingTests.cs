using Foliant.Pipeline;
using Xunit;

namespace Foliant.Tests;

public class TextLayerRunGroupingTests
{
    private static (BoundingBox, string) W(string text, float x1, float y1, float x2, float y2) =>
        (new BoundingBox(x1, y1, x2, y2), text);

    [Fact]
    public void AdjacentWordsOnSameBaseline_FormOneRun()
    {
        // 20px-tall words, 8px gaps (≪ 1.5×height) — prose spacing.
        var words = new List<(BoundingBox Box, string Text)>
        {
            W("quick", 60, 0, 110, 20),
            W("The", 0, 0, 52, 20),
            W("fox", 118, 0, 150, 20),
        };

        var lines = PdfTextLayerReader.GroupWordsIntoRuns(words);

        Assert.Single(lines);
        Assert.Equal("The quick fox", lines[0].Text);
        Assert.Equal(TextSource.TextLayer, lines[0].Source);
        Assert.Equal(0, lines[0].Bounds.X1);
        Assert.Equal(150, lines[0].Bounds.X2);
    }

    [Fact]
    public void WideGapOnSameBaseline_SplitsIntoSeparateRuns()
    {
        // Two form fields on one row separated by 100px (> 1.5×20px height).
        var words = new List<(BoundingBox Box, string Text)>
        {
            W("NAME:", 0, 0, 60, 20),
            W("Smith", 65, 0, 120, 20),
            W("DATE:", 220, 0, 280, 20),
            W("2026-06-11", 285, 0, 380, 20),
        };

        var lines = PdfTextLayerReader.GroupWordsIntoRuns(words);

        Assert.Equal(2, lines.Count);
        Assert.Equal("NAME: Smith", lines[0].Text);
        Assert.Equal("DATE: 2026-06-11", lines[1].Text);
    }

    [Fact]
    public void DifferentBaselines_SeparateLines()
    {
        var words = new List<(BoundingBox Box, string Text)>
        {
            W("below", 0, 50, 60, 70),
            W("above", 0, 0, 60, 20),
        };

        var lines = PdfTextLayerReader.GroupWordsIntoRuns(words);

        Assert.Equal(2, lines.Count);
        Assert.Equal("above", lines[0].Text);
        Assert.Equal("below", lines[1].Text);
    }
}
