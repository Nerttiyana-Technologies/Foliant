using Foliant;
using Foliant.Forms.Lilt;
using Xunit;

namespace Foliant.Tests;

/// <summary>
/// Pure-geometry tests for the learned extractor's word splitting (train/inference featurization
/// alignment) — no ONNX model involved. End-to-end extraction is exercised by the harness
/// (--lilt-extract / --lilt-only) against a real checkpoint.
/// </summary>
public class LiltFormFieldExtractorTests
{
    [Fact]
    public void SplitWords_ProportionalSlices_CoverLineAndStayOrdered()
    {
        var line = new TextLine(new BoundingBox(100, 50, 400, 70), "NAME AND TITLE", 0.9f, TextSource.Ocr);
        var (words, boxes) = LiltFormFieldExtractor.SplitWords(new[] { line });

        Assert.Equal(new[] { "NAME", "AND", "TITLE" }, words);
        Assert.Equal(3, boxes.Count);
        Assert.Equal(100, boxes[0].X1, 1e-3);
        Assert.True(boxes[0].X2 < boxes[1].X1, "words must not overlap");
        Assert.True(boxes[1].X2 < boxes[2].X1, "words must not overlap");
        Assert.True(boxes[2].X2 <= 400 + 1e-3, "last word must not exceed the line box");
        Assert.All(boxes, b => { Assert.Equal(50, b.Y1, 1e-3); Assert.Equal(70, b.Y2, 1e-3); });
        // longer words get wider slices
        Assert.True(boxes[2].Width > boxes[1].Width);
    }

    [Fact]
    public void SplitWords_SkipsEmptyAndWhitespaceLines()
    {
        var lines = new[]
        {
            new TextLine(new BoundingBox(0, 0, 10, 10), "   ", 0.9f, TextSource.Ocr),
            new TextLine(new BoundingBox(0, 20, 50, 30), "SSN:", 0.9f, TextSource.Ocr),
        };
        var (words, boxes) = LiltFormFieldExtractor.SplitWords(lines);
        Assert.Equal(new[] { "SSN:" }, words);
        Assert.Single(boxes);
    }

}
