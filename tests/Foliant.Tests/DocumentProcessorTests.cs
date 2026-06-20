using Foliant.Pipeline;
using Xunit;

namespace Foliant.Tests;

public class DocumentProcessorTests
{
    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeRenderer : IPageRenderer
    {
        public int PageCount { get; init; } = 2;
        public int GetPageCount(byte[] pdf) => PageCount;
        public PageImage Render(byte[] pdf, int pageNumber, int dpi) =>
            new(100, 100, dpi, new byte[100 * 100 * 4]);
    }

    private sealed class FakeLayout : ILayoutDetector
    {
        public IReadOnlyList<LayoutRegion> Detect(PageImage page) => new[]
        {
            new LayoutRegion(RegionType.Text, "plain text", 0.9f, new BoundingBox(0, 0, 100, 100)),
        };
        public void Dispose() { }
    }

    private sealed class FakeOcr : IOcrEngine
    {
        public int Calls;
        public IReadOnlyList<TextLine> Recognize(PageImage page)
        {
            Calls++;
            return new[]
            {
                new TextLine(new BoundingBox(5, 5, 60, 15), "ocr text line", 0.95f, TextSource.Ocr),
            };
        }
        public void Dispose() { }
    }

    private sealed class FakeTables : ITableExtractor
    {
        public TableExtraction Extract(PageImage page, LayoutRegion table, IReadOnlyList<TextLine> pageLines) =>
            new(null, Array.Empty<TextLine>());
        public void Dispose() { }
    }

    private sealed class FakeTextLayer : ITextLayerReader
    {
        public int WordCount { get; init; } = 50;
        public float DroppedCharFraction { get; init; } = 0f;
        public float UndecodableCharFraction { get; init; } = 0f;
        public TextLayerPage? Read(byte[] pdf, int pageNumber, int dpi)
        {
            if (WordCount <= 0) return null;
            return new TextLayerPage(
                new[]
                {
                    new TextLine(new BoundingBox(5, 5, 60, 15), "embedded layer text", 1f, TextSource.TextLayer),
                },
                WordCount,
                DroppedCharFraction,
                UndecodableCharFraction);
        }
    }

    private sealed class ResizeTransform : IPageImageTransform
    {
        public int Calls;
        public PageImage Transform(PageImage image)
        {
            Calls++;
            return new PageImage(50, 50, image.Dpi, new byte[50 * 50 * 4]);
        }
    }

    private sealed class FakeScanResolution : IScanResolutionEstimator
    {
        public int? Dpi { get; init; }
        public int Calls;
        public int? EstimateEffectiveDpi(byte[] pdf, int pageNumber)
        {
            Calls++;
            return Dpi;
        }
    }

    private static DocumentProcessor NewProcessor(FakeOcr ocr, ITextLayerReader textLayer) =>
        new(new FakeRenderer(), new FakeLayout(), ocr, new FakeTables(),
            new XyCutReadingOrder(), textLayer);

    private static DocumentProcessor NewProcessor(
        FakeOcr ocr, ITextLayerReader textLayer, IScanResolutionEstimator scanResolution) =>
        new(new FakeRenderer(), new FakeLayout(), ocr, new FakeTables(),
            new XyCutReadingOrder(), textLayer, scanResolution: scanResolution);

    private sealed class FakeUpscaler : IScanUpscaler
    {
        public int Calls;
        public float LastFactor;
        public PageImage Upscale(PageImage image, float factor)
        {
            Calls++;
            LastFactor = factor;
            int w = (int)(image.Width * factor), h = (int)(image.Height * factor);
            return new PageImage(w, h, image.Dpi, new byte[w * h * 4]);
        }
    }

    private static DocumentProcessor NewProcessor(
        FakeOcr ocr, ITextLayerReader textLayer,
        IScanResolutionEstimator scanResolution, IScanUpscaler scanUpscaler) =>
        new(new FakeRenderer(), new FakeLayout(), ocr, new FakeTables(),
            new XyCutReadingOrder(), textLayer,
            scanResolution: scanResolution, scanUpscaler: scanUpscaler);

    private sealed class FakeFormFields : IFormFieldExtractor
    {
        public int Calls;
        public IReadOnlyList<FormField> Extract(
            byte[] pdf, int pageNumber, PageImage image, IReadOnlyList<TextLine> lines)
        {
            Calls++;
            return new[] { new FormField("solicitation_number", "ABC123-25-R-00001", FieldKind.Text) };
        }
    }

    private static DocumentProcessor NewProcessor(
        FakeOcr ocr, ITextLayerReader textLayer, IFormFieldExtractor formFields) =>
        new(new FakeRenderer(), new FakeLayout(), ocr, new FakeTables(),
            new XyCutReadingOrder(), textLayer, formFields: formFields);

    private static readonly byte[] FakePdf = { 0x25, 0x50, 0x44, 0x46 };   // "%PDF" — fakes ignore it

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Auto_RichTextLayer_UsesFastPathAndSkipsOcr()
    {
        var ocr = new FakeOcr();
        using var processor = NewProcessor(ocr, new FakeTextLayer { WordCount = 50 });

        var result = await processor.ProcessAsync(FakePdf, new ProcessingOptions { Verify = false });

        Assert.All(result.Pages, p => Assert.Equal(TextSource.TextLayer, p.Source));
        Assert.Equal(0, ocr.Calls);
        Assert.Contains("embedded layer text", result.Markdown);
    }

    [Fact]
    public async Task ImageTransform_IsAppliedToEachPage_BeforeProcessing()
    {
        var ocr = new FakeOcr();
        using var processor = NewProcessor(ocr, new FakeTextLayer { WordCount = 50 });
        var transform = new ResizeTransform();

        var result = await processor.ProcessAsync(FakePdf, new ProcessingOptions
        {
            Verify = false,
            TextLayer = TextLayerMode.Never, // force OCR so the transformed pixels are what gets read
            ImageTransform = transform,
        });

        Assert.Equal(2, transform.Calls); // FakeRenderer reports 2 pages → one transform call each
        // PageResult dimensions come from the image the pipeline actually used downstream.
        Assert.All(result.Pages, p => Assert.Equal(50, p.WidthPx));
        Assert.All(result.Pages, p => Assert.Equal(50, p.HeightPx));
    }

    [Fact]
    public async Task Auto_SparseTextLayer_FallsBackToOcr()
    {
        var ocr = new FakeOcr();
        using var processor = NewProcessor(ocr, new FakeTextLayer { WordCount = 3 });   // < MinTextLayerWords

        // Orientation off: isolate the text-layer-vs-OCR decision (it would add thumbnail OCR calls).
        var result = await processor.ProcessAsync(FakePdf, new ProcessingOptions { Verify = false, DetectOrientation = false });

        Assert.All(result.Pages, p => Assert.Equal(TextSource.Ocr, p.Source));
        Assert.Equal(2, ocr.Calls);
    }

    [Fact]
    public async Task Auto_UntrustworthyTextLayer_FallsBackToOcr()
    {
        // The formmsd class: plenty of words in the layer, but most characters belonged
        // to words discarded for degenerate geometry (non-embedded fonts). Word count
        // alone says "fast path"; the dropped-char guard must say OCR.
        var ocr = new FakeOcr();
        using var processor = NewProcessor(
            ocr, new FakeTextLayer { WordCount = 100, DroppedCharFraction = 0.9f });

        var result = await processor.ProcessAsync(FakePdf, new ProcessingOptions { Verify = false, DetectOrientation = false });

        Assert.All(result.Pages, p => Assert.Equal(TextSource.Ocr, p.Source));
        Assert.Equal(2, ocr.Calls);
    }

    [Fact]
    public async Task Auto_UndecodableCidTextLayer_FallsBackToOcr()
    {
        // The CID-magazine class: valid word geometry (DroppedCharFraction = 0) but the
        // glyphs are control codes with no ToUnicode map. The undecodable guard must fire.
        var ocr = new FakeOcr();
        using var processor = NewProcessor(
            ocr, new FakeTextLayer { WordCount = 100, UndecodableCharFraction = 0.85f });

        var result = await processor.ProcessAsync(FakePdf, new ProcessingOptions { Verify = false, DetectOrientation = false });

        Assert.All(result.Pages, p => Assert.Equal(TextSource.Ocr, p.Source));
        Assert.Equal(2, ocr.Calls);
    }

    [Fact]
    public async Task Auto_UndecodableFractionAtThreshold_StaysOnFastPath()
    {
        var ocr = new FakeOcr();
        using var processor = NewProcessor(
            ocr, new FakeTextLayer { WordCount = 100, UndecodableCharFraction = 0.2f });

        var result = await processor.ProcessAsync(FakePdf, new ProcessingOptions { Verify = false });

        Assert.All(result.Pages, p => Assert.Equal(TextSource.TextLayer, p.Source));
        Assert.Equal(0, ocr.Calls);
    }

    [Fact]
    public async Task Auto_DroppedFractionAtThreshold_StaysOnFastPath()
    {
        var ocr = new FakeOcr();
        using var processor = NewProcessor(
            ocr, new FakeTextLayer { WordCount = 100, DroppedCharFraction = 0.3f });

        var result = await processor.ProcessAsync(FakePdf, new ProcessingOptions { Verify = false });

        Assert.All(result.Pages, p => Assert.Equal(TextSource.TextLayer, p.Source));
        Assert.Equal(0, ocr.Calls);
    }

    [Fact]
    public async Task Always_IgnoresDroppedCharGuard()
    {
        // Always is an explicit user override: any words at all → use the layer.
        var ocr = new FakeOcr();
        using var processor = NewProcessor(
            ocr, new FakeTextLayer { WordCount = 100, DroppedCharFraction = 0.9f });

        var result = await processor.ProcessAsync(
            FakePdf, new ProcessingOptions { TextLayer = TextLayerMode.Always, Verify = false });

        Assert.All(result.Pages, p => Assert.Equal(TextSource.TextLayer, p.Source));
        Assert.Equal(0, ocr.Calls);
    }

    private sealed class XfaPlaceholderTextLayer : ITextLayerReader
    {
        public TextLayerPage? Read(byte[] pdf, int pageNumber, int dpi) =>
            new(new[]
            {
                new TextLine(new BoundingBox(5, 5, 200, 20), "Please wait...", 1f, TextSource.TextLayer),
                new TextLine(new BoundingBox(5, 25, 400, 40),
                    "If this message is not eventually replaced by the proper contents of the document",
                    1f, TextSource.TextLayer),
            }, WordCount: 14);
    }

    [Fact]
    public async Task DynamicXfaPlaceholder_FlaggedAndSuppressed_NotOcrd()
    {
        var ocr = new FakeOcr();
        using var processor = NewProcessor(ocr, new XfaPlaceholderTextLayer());

        var result = await processor.ProcessAsync(FakePdf, new ProcessingOptions { Verify = false });

        Assert.All(result.Pages, p =>
        {
            Assert.NotNull(p.Notice);
            Assert.Contains("XFA", p.Notice);
            // The placeholder BODY must not leak through as content (the notice itself may
            // quote the words "Please wait", so assert on the unique placeholder sentence).
            Assert.DoesNotContain("If this message is not eventually replaced", p.Markdown);
            Assert.Empty(p.Lines);
        });
        Assert.Equal(0, ocr.Calls);   // OCR can't help — don't waste it
    }

    [Fact]
    public async Task ScannedPage_BelowMinScanDpi_IsFlaggedLowResolution()
    {
        // Sparse text layer → OCR route → estimator runs. 120 DPI < default MinScanDpi (150).
        var ocr = new FakeOcr();
        var estimator = new FakeScanResolution { Dpi = 120 };
        using var processor = NewProcessor(ocr, new FakeTextLayer { WordCount = 0 }, estimator);

        var result = await processor.ProcessAsync(
            FakePdf, new ProcessingOptions { Verify = false, DetectOrientation = false });

        Assert.All(result.Pages, p =>
        {
            Assert.Equal(TextSource.Ocr, p.Source);
            Assert.Equal(120, p.EffectiveDpi);
            Assert.True(p.LowResolution);
        });
    }

    [Fact]
    public async Task ScannedPage_AtOrAboveMinScanDpi_IsNotFlagged()
    {
        var ocr = new FakeOcr();
        var estimator = new FakeScanResolution { Dpi = 150 };   // exactly the floor → not "below"
        using var processor = NewProcessor(ocr, new FakeTextLayer { WordCount = 0 }, estimator);

        var result = await processor.ProcessAsync(
            FakePdf, new ProcessingOptions { Verify = false, DetectOrientation = false });

        Assert.All(result.Pages, p =>
        {
            Assert.Equal(150, p.EffectiveDpi);
            Assert.False(p.LowResolution);
        });
    }

    [Fact]
    public async Task ScannedPage_UnknownEffectiveDpi_IsNotFlagged()
    {
        // Estimator returns null (no page-covering image): no DPI, no warning.
        var ocr = new FakeOcr();
        var estimator = new FakeScanResolution { Dpi = null };
        using var processor = NewProcessor(ocr, new FakeTextLayer { WordCount = 0 }, estimator);

        var result = await processor.ProcessAsync(
            FakePdf, new ProcessingOptions { Verify = false, DetectOrientation = false });

        Assert.All(result.Pages, p =>
        {
            Assert.Null(p.EffectiveDpi);
            Assert.False(p.LowResolution);
        });
    }

    [Fact]
    public async Task TextLayerPage_NeverEstimatesResolution()
    {
        // Born-digital fast path: the estimator must not run, and the page is never flagged.
        var ocr = new FakeOcr();
        var estimator = new FakeScanResolution { Dpi = 50 };   // would flag if it ran
        using var processor = NewProcessor(ocr, new FakeTextLayer { WordCount = 50 }, estimator);

        var result = await processor.ProcessAsync(FakePdf, new ProcessingOptions { Verify = false });

        Assert.Equal(0, estimator.Calls);
        Assert.All(result.Pages, p =>
        {
            Assert.Equal(TextSource.TextLayer, p.Source);
            Assert.Null(p.EffectiveDpi);
            Assert.False(p.LowResolution);
        });
    }

    [Fact]
    public async Task CustomMinScanDpi_IsHonored()
    {
        // 250 DPI scan flagged only because the caller raised the floor to 300.
        var ocr = new FakeOcr();
        var estimator = new FakeScanResolution { Dpi = 250 };
        using var processor = NewProcessor(ocr, new FakeTextLayer { WordCount = 0 }, estimator);

        var result = await processor.ProcessAsync(
            FakePdf, new ProcessingOptions { Verify = false, DetectOrientation = false, MinScanDpi = 300 });

        Assert.All(result.Pages, p => Assert.True(p.LowResolution));
    }

    [Fact]
    public async Task LowResolutionPage_Upscaled_WhenOptionOn()
    {
        var ocr = new FakeOcr();
        var estimator = new FakeScanResolution { Dpi = 120 };       // below 150 → flagged
        var upscaler = new FakeUpscaler();
        using var processor = NewProcessor(ocr, new FakeTextLayer { WordCount = 0 }, estimator, upscaler);

        var result = await processor.ProcessAsync(FakePdf, new ProcessingOptions
        {
            Verify = false,
            DetectOrientation = false,
            UpscaleLowResolutionScans = true,
            LowResolutionUpscaleFactor = 2.0f,
        });

        Assert.Equal(2, upscaler.Calls);            // one per OCR-routed page (FakeRenderer = 2 pages)
        Assert.Equal(2.0f, upscaler.LastFactor);
        // Downstream dimensions reflect the upscaled raster (FakeRenderer renders 100×100 → 200×200).
        Assert.All(result.Pages, p => Assert.Equal(200, p.WidthPx));
        // The advisory fields still describe the ORIGINAL source scan, not the upscale.
        Assert.All(result.Pages, p => Assert.Equal(120, p.EffectiveDpi));
        Assert.All(result.Pages, p => Assert.True(p.LowResolution));
    }

    [Fact]
    public async Task LowResolutionPage_NotUpscaled_WhenOptionOff()
    {
        var ocr = new FakeOcr();
        var estimator = new FakeScanResolution { Dpi = 120 };
        var upscaler = new FakeUpscaler();
        using var processor = NewProcessor(ocr, new FakeTextLayer { WordCount = 0 }, estimator, upscaler);

        // UpscaleLowResolutionScans defaults to false.
        var result = await processor.ProcessAsync(
            FakePdf, new ProcessingOptions { Verify = false, DetectOrientation = false });

        Assert.Equal(0, upscaler.Calls);
        Assert.All(result.Pages, p => Assert.Equal(100, p.WidthPx));   // unchanged
    }

    [Fact]
    public async Task AdequateResolutionPage_NotUpscaled_EvenWhenOptionOn()
    {
        var ocr = new FakeOcr();
        var estimator = new FakeScanResolution { Dpi = 300 };          // not low-res
        var upscaler = new FakeUpscaler();
        using var processor = NewProcessor(ocr, new FakeTextLayer { WordCount = 0 }, estimator, upscaler);

        var result = await processor.ProcessAsync(FakePdf, new ProcessingOptions
        {
            Verify = false,
            DetectOrientation = false,
            UpscaleLowResolutionScans = true,
        });

        Assert.Equal(0, upscaler.Calls);
        Assert.All(result.Pages, p => Assert.False(p.LowResolution));
    }

    [Fact]
    public async Task FormFields_Extracted_WhenOptionOnAndExtractorWired()
    {
        var ocr = new FakeOcr();
        var extractor = new FakeFormFields();
        using var processor = NewProcessor(ocr, new FakeTextLayer { WordCount = 50 }, extractor);

        var result = await processor.ProcessAsync(
            FakePdf, new ProcessingOptions { Verify = false, ExtractFormFields = true });

        Assert.Equal(2, extractor.Calls);   // one per page
        Assert.All(result.Pages, p =>
        {
            Assert.NotNull(p.FormFields);
            var f = Assert.Single(p.FormFields!);
            Assert.Equal("solicitation_number", f.Name);
            Assert.Equal("ABC123-25-R-00001", f.Value);
            Assert.Equal(FieldKind.Text, f.Kind);
        });
    }

    [Fact]
    public async Task FormFields_Null_WhenOptionOff()
    {
        var ocr = new FakeOcr();
        var extractor = new FakeFormFields();
        using var processor = NewProcessor(ocr, new FakeTextLayer { WordCount = 50 }, extractor);

        // ExtractFormFields defaults to false.
        var result = await processor.ProcessAsync(FakePdf, new ProcessingOptions { Verify = false });

        Assert.Equal(0, extractor.Calls);
        Assert.All(result.Pages, p => Assert.Null(p.FormFields));
    }

    [Fact]
    public async Task FormFields_Null_WhenNoExtractorWired()
    {
        using var processor = NewProcessor(new FakeOcr(), new FakeTextLayer { WordCount = 50 });

        var result = await processor.ProcessAsync(
            FakePdf, new ProcessingOptions { Verify = false, ExtractFormFields = true });

        Assert.All(result.Pages, p => Assert.Null(p.FormFields));   // option on, but no extractor → no-op
    }

    // ── AcroForm/XFA filled-value recovery (regression guard for the 1.1.1 bug, #12) ──────────
    //    The bug: on the born-digital fast path, filled field VALUES live in the widget /V, not in
    //    the content-stream text layer, so they were silently dropped from the output (and recall,
    //    measured against the same value-less layer, still scored 100%). These two tests pin the
    //    fix: AcroFormValueLines must recover /V, and the fast path must carry it into the output.

    [Fact]
    public void AcroFormValueLines_RecoversFilledWidgetValue_AsPositionedLine()
    {
        // 100x100 pt page; one /Tx widget filled with /V, rect [10 70 90 90] (PDF points).
        byte[] pdf = FilledFormPdf.Build();

        var lines = DocumentProcessor.AcroFormValueLines(pdf, pageNumber: 1, dpi: 72);

        var line = Assert.Single(lines);
        Assert.Equal(FilledFormPdf.DefaultValue, line.Text);
        Assert.Equal(TextSource.TextLayer, line.Source);   // recovered as fast-path text, not OCR
        Assert.True(line.Bounds.Width > 0 && line.Bounds.Height > 0, "value line must have a positive-area box");
        // dpi 72 → scale 1; rect maps to a top-left box (10,10)-(90,30) on the 100-tall page.
        Assert.True(Math.Abs(line.Bounds.X1 - 10f) < 0.5f, $"X1 was {line.Bounds.X1}");
        Assert.True(Math.Abs(line.Bounds.Y1 - 10f) < 0.5f, $"Y1 was {line.Bounds.Y1}");
        Assert.True(Math.Abs(line.Bounds.X2 - 90f) < 0.5f, $"X2 was {line.Bounds.X2}");
        Assert.True(Math.Abs(line.Bounds.Y2 - 30f) < 0.5f, $"Y2 was {line.Bounds.Y2}");
    }

    [Fact]
    public void AcroFormValueLines_IgnoresHiddenWidget()
    {
        // /F 2 = Hidden → the value never renders, so it must not be injected either.
        // The replace is length-preserving, so the cross-reference offsets stay valid.
        byte[] pdf = FilledFormPdf.Build();
        byte[] hidden = System.Text.Encoding.Latin1.GetBytes(
            System.Text.Encoding.Latin1.GetString(pdf).Replace("/F 4 >>", "/F 2 >>"));

        var lines = DocumentProcessor.AcroFormValueLines(hidden, pageNumber: 1, dpi: 72);

        Assert.Empty(lines);
    }

    [Fact]
    public async Task FastPath_RecoversAcroFormFieldValues_IntoOutput()
    {
        var ocr = new FakeOcr();
        using var processor = NewProcessor(ocr, new FakeTextLayer { WordCount = 50 });
        byte[] pdf = FilledFormPdf.Build();   // value box lands in the 100x100 raster at 72 dpi

        var result = await processor.ProcessAsync(pdf, new ProcessingOptions
        {
            Verify = false,
            Dpi = 72,                     // scale = dpi/72 = 1 → widget rect maps into FakeRenderer's 100x100
            Pages = new[] { 1 },          // FakeRenderer reports 2 pages; the real PDF has 1
            DetectOrientation = false,
        });

        Assert.Equal(0, ocr.Calls);       // stayed on the born-digital fast path
        var page = Assert.Single(result.Pages);
        Assert.Equal(TextSource.TextLayer, page.Source);
        // The recovered value is present as a fast-path line AND survives composition into the output.
        Assert.Contains(page.Lines, l => l.Text == FilledFormPdf.DefaultValue && l.Source == TextSource.TextLayer);
        Assert.Contains(FilledFormPdf.DefaultValue, page.Markdown);
        Assert.Contains(FilledFormPdf.DefaultValue, result.Markdown);
    }

    [Fact]
    public async Task Never_AlwaysOcrs()
    {
        var ocr = new FakeOcr();
        using var processor = NewProcessor(ocr, new FakeTextLayer { WordCount = 500 });

        var result = await processor.ProcessAsync(
            FakePdf, new ProcessingOptions { TextLayer = TextLayerMode.Never, Verify = false, DetectOrientation = false });

        Assert.All(result.Pages, p => Assert.Equal(TextSource.Ocr, p.Source));
        Assert.Equal(2, ocr.Calls);
    }

    [Fact]
    public async Task Always_NoLayerAtAll_StillOcrs()
    {
        var ocr = new FakeOcr();
        using var processor = NewProcessor(ocr, new FakeTextLayer { WordCount = 0 });

        var result = await processor.ProcessAsync(
            FakePdf, new ProcessingOptions { TextLayer = TextLayerMode.Always, Verify = false });

        Assert.All(result.Pages, p => Assert.Equal(TextSource.Ocr, p.Source));
    }

    [Fact]
    public async Task CoverageInvariant_HoldsOnEveryPage()
    {
        using var processor = NewProcessor(new FakeOcr(), new FakeTextLayer());

        var result = await processor.ProcessAsync(FakePdf);

        Assert.All(result.Pages, p => Assert.True(p.Verification.CoverageHolds));
    }

    [Fact]
    public async Task PagesOption_FiltersAndOrders()
    {
        using var processor = NewProcessor(new FakeOcr(), new FakeTextLayer());

        var result = await processor.ProcessAsync(
            FakePdf, new ProcessingOptions { Pages = new[] { 2, 99, 2 }, Verify = false });

        Assert.Single(result.Pages);
        Assert.Equal(2, result.Pages[0].PageNumber);
    }

    [Fact]
    public async Task DocumentMarkdown_CarriesPageMarkers()
    {
        using var processor = NewProcessor(new FakeOcr(), new FakeTextLayer());

        var result = await processor.ProcessAsync(FakePdf, new ProcessingOptions { Verify = false });

        Assert.Contains("<!-- page 1 -->", result.Markdown);
        Assert.Contains("<!-- page 2 -->", result.Markdown);
    }

    [Fact]
    public async Task ToJson_RoundTripsWithoutError()
    {
        using var processor = NewProcessor(new FakeOcr(), new FakeTextLayer());
        var result = await processor.ProcessAsync(FakePdf, new ProcessingOptions { Verify = false });

        string json = result.ToJson(indented: true);

        Assert.Contains("\"pageNumber\"", json);
        Assert.Contains("\"textLayer\"", json);   // enum serialized as camelCase string
    }
}
