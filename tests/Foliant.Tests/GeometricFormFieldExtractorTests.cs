// The geometric extractor is pure (profile + text geometry in, fields out), so it's tested on
// synthetic pages that place labels and values at known coordinates. The cases mirror the real
// flattened-form patterns: inline ("LABEL  value"), value-to-the-right, value-below, and a
// checkbox mark on the label's row — plus the min-label-match guard that keeps it off wrong pages.

using Foliant;
using Foliant.Pipeline;
using Xunit;

namespace Foliant.Tests;

public class GeometricFormFieldExtractorTests
{
    private static readonly byte[] Pdf = { 0x25, 0x50, 0x44, 0x46 };
    private static readonly PageImage Img = new(10, 10, 300, new byte[10 * 10 * 4]);

    private static TextLine L(string text, float x1, float y1, float x2, float y2) =>
        new(new BoundingBox(x1, y1, x2, y2), text, 1f, TextSource.Ocr);

    private static readonly FormProfile Sf33 = new("SF-33 test", new[]
    {
        new FormFieldSpec("solicitation_number", "SOLICITATION NO", FieldKind.Text, ValueAnchor.RightThenBelow),
        new FormFieldSpec("date_issued",         "DATE ISSUED",     FieldKind.Text, ValueAnchor.Right),
        new FormFieldSpec("contracting_officer", "CONTRACTING OFFICER", FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("set_aside",           "SET ASIDE",       FieldKind.Checkbox, ValueAnchor.Mark),
    });

    private static FormField Field(IReadOnlyList<FormField> fields, string name) =>
        fields.Single(f => f.Name == name);

    [Fact]
    public void ExtractsInlineRightBelowAndCheckbox()
    {
        var lines = new[]
        {
            L("SOLICITATION NO. ABC123-25-R-00001", 10, 10, 300, 20),   // inline value
            L("DATE ISSUED", 10, 30, 90, 40),
            L("08/07/2025",  120, 30, 200, 40),                          // value to the right
            L("CONTRACTING OFFICER", 10, 50, 120, 60),
            L("Jane A. Doe", 10, 65, 150, 75),                    // value below
            L("X", 10, 85, 20, 95),                                      // checkbox mark
            L("SET ASIDE", 30, 85, 90, 95),
        };
        var extractor = new GeometricFormFieldExtractor(new[] { Sf33 });

        var fields = extractor.Extract(Pdf, 1, Img, lines);

        Assert.Equal("ABC123-25-R-00001", Field(fields, "solicitation_number").Value);
        Assert.Equal("08/07/2025", Field(fields, "date_issued").Value);
        Assert.Equal("Jane A. Doe", Field(fields, "contracting_officer").Value);
        var box = Field(fields, "set_aside");
        Assert.Equal(FieldKind.Checkbox, box.Kind);
        Assert.Equal("checked", box.Value);
        Assert.All(fields, f => Assert.Equal(FormFieldSource.Geometry, f.Source));
    }

    [Fact]
    public void Checkbox_WithNoMarkOnRow_IsUnchecked()
    {
        var lines = new[]
        {
            L("SOLICITATION NO. ABC123-25-R-00001", 10, 10, 300, 20),
            L("DATE ISSUED", 10, 30, 90, 40),
            L("08/07/2025", 120, 30, 200, 40),
            L("SET ASIDE", 30, 85, 90, 95),                              // no mark glyph on this row
        };
        var extractor = new GeometricFormFieldExtractor(new[] { Sf33 });

        var fields = extractor.Extract(Pdf, 1, Img, lines);

        Assert.Equal("unchecked", Field(fields, "set_aside").Value);
    }

    [Fact]
    public void BelowTheMinLabelMatches_ExtractsNothing()
    {
        // Only one of the profile's labels appears → not this form → no extraction.
        var lines = new[]
        {
            L("DATE ISSUED", 10, 30, 90, 40),
            L("08/07/2025", 120, 30, 200, 40),
            L("Some unrelated paragraph of body text", 10, 60, 400, 70),
        };
        var extractor = new GeometricFormFieldExtractor(new[] { Sf33 }, minLabelMatches: 2);

        Assert.Empty(extractor.Extract(Pdf, 1, Img, lines));
    }

    [Fact]
    public void Checkbox_MarkInOtherColumn_DoesNotCount()
    {
        // Two-column TOC: this label's own (left) checkbox is empty, but a mark sits on the same
        // visual row in the RIGHT column (after the label). It must NOT mark this field — the bug
        // that produced the lone Gate 3 fabrication (toc_A read "checked" from column I's mark).
        var lines = new[]
        {
            L("SOLICITATION NO. ABC123-25-R-00001", 10, 10, 300, 20),
            L("DATE ISSUED", 10, 30, 90, 40),
            L("08/07/2025", 120, 30, 200, 40),
            L("SOLICITATION/CONTRACT FORM", 30, 85, 200, 95),   // left-column item, no mark to its left
            L("X", 260, 85, 270, 95),                           // mark belongs to the RIGHT column
            L("CONTRACT CLAUSES", 280, 85, 380, 95),            // right-column item
        };
        var profile = new FormProfile("toc", new[]
        {
            new FormFieldSpec("toc_A", "SOLICITATION/CONTRACT FORM", FieldKind.Checkbox, ValueAnchor.Mark),
            new FormFieldSpec("solicitation_number", "SOLICITATION NO", FieldKind.Text, ValueAnchor.RightThenBelow),
            new FormFieldSpec("date_issued", "DATE ISSUED", FieldKind.Text, ValueAnchor.Right),
        });
        var extractor = new GeometricFormFieldExtractor(new[] { profile });

        var fields = extractor.Extract(Pdf, 1, Img, lines);

        Assert.Equal("unchecked", Field(fields, "toc_A").Value);
    }

    [Fact]
    public void Composite_PrefersFirstExtractorThatYields()
    {
        // A geometric-only composite: empty extractor first, geometric second → geometric wins.
        var lines = new[]
        {
            L("SOLICITATION NO. ABC123-25-R-00001", 10, 10, 300, 20),
            L("DATE ISSUED", 10, 30, 90, 40),
            L("08/07/2025", 120, 30, 200, 40),
        };
        var empty = new GeometricFormFieldExtractor(new[] { new FormProfile("none", System.Array.Empty<FormFieldSpec>()) });
        var geo = new GeometricFormFieldExtractor(new[] { Sf33 });
        var composite = new CompositeFormFieldExtractor(empty, geo);

        var fields = composite.Extract(Pdf, 1, Img, lines);

        Assert.Contains(fields, f => f.Name == "solicitation_number" && f.Value == "ABC123-25-R-00001");
    }
}
