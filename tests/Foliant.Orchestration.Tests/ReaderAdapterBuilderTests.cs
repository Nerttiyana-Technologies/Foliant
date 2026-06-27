using Xunit;
using ZD = ZeroDep.Abstractions;
using static Foliant.Orchestration.Tests.TestData;

namespace Foliant.Orchestration.Tests;

public sealed class ZeroDepClassificationReaderTests
{
    private readonly ZeroDepClassificationReader _reader = new();

    [Fact]
    public void Rejected_document_stops_all_pages()
    {
        var analysis = Analysis(pages: Array.Empty<ZD.PageClassification>(),
            status: ZD.DocumentStatus.Rejected, pageCount: 3);

        var inputs = _reader.Read(analysis);

        Assert.Equal(3, inputs.Count);
        Assert.All(inputs, i => Assert.Equal(PageKind.Unprocessable, i.Kind));
    }

    [Fact]
    public void Xfa_document_forces_every_page_to_escalate()
    {
        var analysis = Analysis(
            pages: new[] { PC(0, ZD.PageContentClass.DigitalText), PC(1, ZD.PageContentClass.DigitalText) },
            xfa: true);

        var inputs = _reader.Read(analysis);

        Assert.Equal(2, inputs.Count);
        Assert.All(inputs, i => Assert.Equal(PageKind.Mixed, i.Kind)); // Mixed -> heavy lane
    }

    [Fact]
    public void Maps_pages_to_one_based_inputs_with_confidence()
    {
        var analysis = Analysis(new[]
        {
            PC(0, ZD.PageContentClass.FormPage, 0.90),
            PC(1, ZD.PageContentClass.ScannedImageOnly, 0.70),
        });

        var inputs = _reader.Read(analysis);

        Assert.Equal(new[] { 1, 2 }, inputs.Select(i => i.PageNumber));
        Assert.Equal(PageKind.FormPage, inputs[0].Kind);
        Assert.Equal(PageKind.ScannedImageOnly, inputs[1].Kind);
        Assert.Equal(0.90, inputs[0].Confidence, 3);
    }
}

public sealed class ZeroDepTypeAdapterTests
{
    private readonly ZeroDepTypeAdapter _adapter = new();

    [Fact]
    public void Embedded_run_maps_to_text_layer()
    {
        var line = _adapter.ToTextLine(Run(0, "hello", x: 72, y: 700, width: 50, source: ZD.TextSource.Embedded));
        Assert.Equal("hello", line.Text);
        Assert.Equal(TextSource.TextLayer, line.Source);
        Assert.Equal(72f, line.Bounds.X1, 3);
        Assert.Equal(122f, line.Bounds.X2, 3);
    }

    [Fact]
    public void Ocr_generated_run_maps_to_ocr()
    {
        var line = _adapter.ToTextLine(Run(0, "scan", source: ZD.TextSource.OcrGenerated));
        Assert.Equal(TextSource.Ocr, line.Source);
    }

    [Fact]
    public void Text_field_maps_to_text_form_field()
    {
        var field = _adapter.ToFormField(Field(0, "applicant.name", "Jane Doe"));
        Assert.NotNull(field);
        Assert.Equal("applicant.name", field!.Name);
        Assert.Equal("Jane Doe", field.Value);
        Assert.Equal(FieldKind.Text, field.Kind);
        Assert.Equal(FormFieldSource.AcroForm, field.Source);
        Assert.Equal(1f, field.Confidence);
    }

    [Fact]
    public void Checked_button_maps_to_checked_checkbox()
    {
        var field = _adapter.ToFormField(Field(0, "agree", value: null, type: "Btn", isChecked: true));
        Assert.NotNull(field);
        Assert.Equal(FieldKind.Checkbox, field!.Kind);
        Assert.Equal("checked", field.Value);
    }

    [Fact]
    public void Empty_text_field_is_skipped()
        => Assert.Null(_adapter.ToFormField(Field(0, "blank", value: "  ")));
}

public sealed class FastLanePageBuilderTests
{
    private readonly FastLanePageBuilder _builder = new(new ZeroDepTypeAdapter());

    [Fact]
    public void Form_page_emits_fields_and_field_markdown()
    {
        var page = _builder.Build(
            pageNumber: 1,
            kind: PageKind.FormPage,
            pageRuns: Array.Empty<ZD.TextRunInfo>(),
            pageFields: new[]
            {
                Field(0, "applicant.name", "Jane Doe"),
                Field(0, "agree", value: null, type: "Btn", isChecked: true),
            });

        Assert.NotNull(page.FormFields);
        Assert.Equal(2, page.FormFields!.Count);
        Assert.Contains("applicant.name", page.Markdown);
        Assert.Contains("[x]", page.Markdown);
        Assert.Equal(TextSource.TextLayer, page.Source);
        Assert.True(page.Verification.CoverageHolds);
    }

    [Fact]
    public void Digital_text_page_assembles_prose_in_reading_order()
    {
        // Two runs on one visual line (same Y), then a lower line.
        var runs = new[]
        {
            Run(1, "Hello", x: 72, y: 700, width: 30),
            Run(1, "world", x: 110, y: 700, width: 30),   // gap from "Hello" >= a space → joined as "Hello world"
            Run(1, "Second line", x: 72, y: 680, width: 60),
        };

        var page = _builder.Build(2, PageKind.DigitalText, runs, Array.Empty<ZD.FormFieldInfo>());

        Assert.Contains("Hello world", page.Markdown);
        Assert.Contains("Second line", page.Markdown);
        Assert.Equal(3, page.Lines.Count);
        Assert.Null(page.FormFields);
        Assert.True(page.Verification.CoverageHolds);
    }

    [Fact]
    public void Glyph_level_runs_are_reassembled_into_words_by_x_gap()
    {
        // ZeroDep often emits near-glyph-level runs. Adjacent glyphs (no gap) must join into one word;
        // a real inter-word gap inserts a single space. (Regression: a blind space-join shattered words.)
        var runs = new[]
        {
            Run(1, "S", x: 72, y: 700, width: 6), Run(1, "H", x: 78, y: 700, width: 6),
            Run(1, "O", x: 84, y: 700, width: 6), Run(1, "R", x: 90, y: 700, width: 6),
            Run(1, "T", x: 96, y: 700, width: 6),
            Run(1, "W", x: 120, y: 700, width: 6), Run(1, "o", x: 126, y: 700, width: 6),
            Run(1, "r", x: 132, y: 700, width: 6), Run(1, "d", x: 138, y: 700, width: 6),
        };

        var page = _builder.Build(1, PageKind.DigitalText, runs, Array.Empty<ZD.FormFieldInfo>());

        Assert.Contains("SHORT Word", page.Markdown);
        Assert.DoesNotContain("S H O R T", page.Markdown);
    }
}
