using Foliant;
using Foliant.Pipeline;
using Xunit;

namespace Foliant.Tests;

// Tests for the no-profile widget+geometry form-field extractor (the deterministic structured-form path).
// Reuses FilledFormPdf (a real 100x100-pt page with one /Tx widget, /V "ABC123-25-R-00001", rect [10 70 90 90]).
// At 72 dpi the widget maps to the raster box (10,10)-(90,30), so labels are placed relative to that.
public sealed class WidgetFormFieldExtractorTests
{
    private static PageImage Img() => new(100, 100, 72, new byte[100 * 100 * 4]);

    [Fact]
    public void PairsFilledValueWithLabelToTheLeft()
    {
        byte[] pdf = FilledFormPdf.Build();
        var lines = new[]
        {
            new TextLine(new BoundingBox(0, 12, 9, 28), "SOLICITATION NUMBER", 1f, TextSource.TextLayer),
        };

        var f = Assert.Single(new WidgetFormFieldExtractor().Extract(pdf, 1, Img(), lines));
        Assert.Equal("ABC123-25-R-00001", f.Value);
        Assert.Equal("SOLICITATION NUMBER", f.Name);
        Assert.Equal(FieldKind.Text, f.Kind);
        Assert.Equal(FormFieldSource.Geometry, f.Source);
    }

    [Fact]
    public void PairsWithLabelAbove_WhenNoneToLeft()
    {
        byte[] pdf = FilledFormPdf.Build();
        var lines = new[]
        {
            new TextLine(new BoundingBox(10, 0, 90, 9), "AWARD DATE", 1f, TextSource.TextLayer),
        };

        Assert.Equal("AWARD DATE", Assert.Single(new WidgetFormFieldExtractor().Extract(pdf, 1, Img(), lines)).Name);
    }

    [Fact]
    public void DoesNotChooseTheValueLineItselfAsTheLabel()
    {
        byte[] pdf = FilledFormPdf.Build();
        var lines = new[]
        {
            // The injected value line sits ON the widget — must be excluded as a label candidate.
            new TextLine(new BoundingBox(12, 12, 88, 28), "ABC123-25-R-00001", 1f, TextSource.TextLayer),
            new TextLine(new BoundingBox(0, 12, 9, 28), "SOLICITATION NUMBER", 1f, TextSource.TextLayer),
        };

        Assert.Equal("SOLICITATION NUMBER",
            Assert.Single(new WidgetFormFieldExtractor().Extract(pdf, 1, Img(), lines)).Name);
    }

    [Fact]
    public void NonFormPdf_ReturnsEmpty()
    {
        var fields = new WidgetFormFieldExtractor().Extract(
            new byte[] { 0x25, 0x50, 0x44, 0x46 }, 1, Img(), Array.Empty<TextLine>());
        Assert.Empty(fields);
    }
}
