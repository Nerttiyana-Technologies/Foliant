using Foliant;
using Foliant.Templates;
using Xunit;

namespace Foliant.Tests;

// By-identity value extraction over a scanned/flattened form: each OCR line lands in exactly one field,
// the printed label is not echoed into its own value, and junk marks are dropped.
public sealed class ScannedFormExtractorTests
{
    private const int W = 1000, H = 1000;
    private static readonly PageImage Img = new(W, H, 150, new byte[W * H * 4]);

    private static TextLine L(string text, float cxNorm, float cyNorm)
    {
        float x = cxNorm * W, y = cyNorm * H;
        return new(new BoundingBox(x - 8, y - 4, x + 8, y + 4), text, 1f, TextSource.Ocr);
    }

    private static FormElement T(string label, float x1, float y1, float x2, float y2) =>
        new(FormElementKind.Text, 1, new NormalizedRect(x1, y1, x2, y2), label);

    [Fact]
    public void LineInOverlappingRects_GoesToNearestElementOnly_NoDuplicateValue()
    {
        var template = new FormLayout("T", "T", new[]
        {
            T("5. SOLICITATION NUMBER", 0.10f, 0.10f, 0.40f, 0.16f), // centre 0.25
            T("6. ISSUED BY",           0.30f, 0.10f, 0.60f, 0.16f), // centre 0.45
        });
        // Line sits in BOTH rects (x 0.32) but nearer the first element's centre → assigned only there.
        var lines = new[] { L("FA469024Q0027", 0.32f, 0.13f) };

        var fields = ScannedFormExtractor.Extract(Img, lines, template, 1);

        Assert.Single(fields, f => f.Value == "FA469024Q0027");
        Assert.Equal("5. SOLICITATION NUMBER", fields.Single(f => f.Value == "FA469024Q0027").Name);
    }

    [Fact]
    public void PrintedLabelInsideRect_IsNotEchoedIntoValue()
    {
        var template = new FormLayout("T", "T", new[]
        {
            T("9B. DATED (SEE ITEM 11)", 0.10f, 0.30f, 0.40f, 0.36f),
        });
        var lines = new[]
        {
            L("DATED", 0.25f, 0.325f),       // echo of the label's own token → dropped
            L("05/23/2024", 0.25f, 0.335f),  // the real value → kept
        };

        var fields = ScannedFormExtractor.Extract(Img, lines, template, 1);

        var f = Assert.Single(fields);
        Assert.Equal("05/23/2024", f.Value);
    }

    [Fact]
    public void LoneMarkValue_IsDropped()
    {
        var template = new FormLayout("T", "T", new[] { T("13A. CHANGE ORDER", 0.60f, 0.30f, 0.90f, 0.36f) });
        var fields = ScannedFormExtractor.Extract(Img, new[] { L("x", 0.75f, 0.33f) }, template, 1);
        Assert.Empty(fields);
    }

    [Fact]
    public void IdenticalLabelAndValue_IsDeduplicated()
    {
        var template = new FormLayout("T", "T", new[]
        {
            T("CODE", 0.10f, 0.50f, 0.30f, 0.56f),
            T("CODE", 0.40f, 0.50f, 0.60f, 0.56f),
        });
        var lines = new[] { L("See Schedule", 0.20f, 0.53f), L("See Schedule", 0.50f, 0.53f) };

        var fields = ScannedFormExtractor.Extract(Img, lines, template, 1);

        Assert.Single(fields, f => f.Name == "CODE" && f.Value == "See Schedule");
    }
}
