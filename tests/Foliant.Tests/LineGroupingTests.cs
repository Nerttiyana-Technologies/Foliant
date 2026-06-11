using Foliant.Pipeline;
using Xunit;

namespace Foliant.Tests;

public class LineGroupingTests
{
    private static TextLine L(string text, float x1, float y1, float x2, float y2) =>
        new(new BoundingBox(x1, y1, x2, y2), text, 1f, TextSource.Ocr);

    [Fact]
    public void GroupIntoVisualLines_JoinsSameBaselineLeftToRight()
    {
        var lines = new[]
        {
            L("world", 200, 100, 300, 120),
            L("hello", 0, 102, 100, 122),
        };

        var rows = LineGrouping.GroupIntoVisualLines(lines);

        Assert.Single(rows);
        Assert.Equal("hello  world", rows[0].Text);
    }

    [Fact]
    public void GroupIntoVisualLines_SeparatesDistinctBaselines()
    {
        var lines = new[]
        {
            L("second", 0, 200, 100, 220),
            L("first", 0, 100, 100, 120),
        };

        var rows = LineGrouping.GroupIntoVisualLines(lines);

        Assert.Equal(2, rows.Count);
        Assert.Equal("first", rows[0].Text);
        Assert.Equal("second", rows[1].Text);
    }

    [Fact]
    public void GroupLines_EmptyInput_NoGroups()
    {
        Assert.Empty(LineGrouping.GroupLines(Array.Empty<TextLine>()));
    }
}
