using Foliant;
using Foliant.Templates;
using Xunit;

namespace Foliant.Tests;

// Generator + per-page matcher tests. FilledFormPdf is a born-digital PDF with one /Tx widget at rect
// [10 70 90 90] on a 100x100-pt page → normalized centre (0.5, 0.2). The fill values are irrelevant here:
// the template/matcher work off widget GEOMETRY, which is identical between a blank and a filled copy.
public sealed class FormTemplateTests
{
    [Fact]
    public void Generate_ExtractsWidgetGeometryAndFingerprint()
    {
        var layout = FormLayoutGenerator.Generate(FilledFormPdf.Build(), "form-a", "Form A");

        var el = Assert.Single(layout.Elements);
        Assert.Equal(FormElementKind.Text, el.Kind);
        Assert.Equal(1, el.Page);
        Assert.Equal(0.5f, el.Rect.CenterX, 2);
        Assert.Equal(0.2f, el.Rect.CenterY, 2);
        Assert.False(string.IsNullOrEmpty(layout.Fingerprint));
    }

    [Fact]
    public void MatchPage_MatchesSameLayout()
    {
        var template = FormLayoutGenerator.Generate(FilledFormPdf.Build(), "form-a", "Form A");

        // Same widget layout (a different "fill" of the same form) → matches with score 1.
        var match = FormMatcher.MatchPage(FilledFormPdf.Build(), 1, new[] { template });

        Assert.NotNull(match);
        Assert.Equal("form-a", match!.Template.TemplateId);
        Assert.Equal(1, match.TemplatePage);
        Assert.True(match.Score >= 0.99, $"score was {match.Score}");
    }

    [Fact]
    public void MatchPage_RejectsDifferentLayout()
    {
        var template = FormLayoutGenerator.Generate(FilledFormPdf.Build(), "form-a", "Form A");

        // Widget at a different position → different signature → below threshold → no match (fall back).
        byte[] other = FilledFormPdf.Build(rectLeft: 5, rectBottom: 10, rectRight: 40, rectTop: 30);
        Assert.Null(FormMatcher.MatchPage(other, 1, new[] { template }));
    }

    [Fact]
    public void MatchPage_NoWidgets_ReturnsNull_ForFallback()
    {
        var template = FormLayoutGenerator.Generate(FilledFormPdf.Build(), "form-a", "Form A");

        // A page with no readable widgets (here, a non-form byte blob) → null → default processing.
        Assert.Null(FormMatcher.MatchPage(new byte[] { 0x25, 0x50, 0x44, 0x46 }, 1, new[] { template }));
    }

    [Fact]
    public void TemplateExtractor_UsesTemplateLabel_WithExactWidgetValue()
    {
        // A hand-authored template element at the FilledFormPdf widget position (normalized 0.1,0.1,0.9,0.3),
        // carrying the KNOWN semantic label. This is the human-reviewed template; geometry is not guessed.
        var template = new FormLayout("form-a", "Form A", new[]
        {
            new FormElement(FormElementKind.Text, 1, new NormalizedRect(0.1f, 0.1f, 0.9f, 0.3f), "SOLICITATION NUMBER"),
        });

        // The filled form carries /V "ABC123-25-R-00001" at that widget.
        var fields = TemplateExtractor.Extract(FilledFormPdf.Build(), pageNumber: 1, template, templatePage: 1);

        var f = Assert.Single(fields);
        Assert.Equal("SOLICITATION NUMBER", f.Name);   // label from the template, not from runtime geometry
        Assert.Equal("ABC123-25-R-00001", f.Value);    // value exact from the widget /V
        Assert.Equal(FieldKind.Text, f.Kind);
    }
}
