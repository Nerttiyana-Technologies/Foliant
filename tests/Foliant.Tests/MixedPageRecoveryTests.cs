using Foliant.Pipeline;
using Xunit;

namespace Foliant.Tests;

// ADR-0004 addendum — MIXED PAGES (the customer-sample class): a born-digital page with a healthy
// text layer that carries its real content as an embedded image (scanned letter of authorization,
// price table pasted as a screenshot). 1.4.0 took the fast path, silently dropped the image's
// text, and reported recall 100% — recall is scored against the same image-less text layer, so it
// is structurally blind to pixels-only content. The fix: probe embedded-image coverage on
// fast-path pages, OCR-merge lines the layer doesn't cover, and flag pages where nothing could be
// recovered. These tests pin that contract.
public class MixedPageRecoveryTests
{
    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeRenderer : IPageRenderer
    {
        public int GetPageCount(byte[] pdf) => 1;
        public PageImage Render(byte[] pdf, int pageNumber, int dpi) =>
            new(100, 100, dpi, new byte[100 * 100 * 4]);
    }

    private sealed class EmptyLayout : ILayoutDetector
    {
        public IReadOnlyList<LayoutRegion> Detect(PageImage page) => Array.Empty<LayoutRegion>();
        public void Dispose() { }
    }

    private sealed class FakeTables : ITableExtractor
    {
        public TableExtraction Extract(PageImage page, LayoutRegion table, IReadOnlyList<TextLine> pageLines) =>
            new(null, Array.Empty<TextLine>());
        public void Dispose() { }
    }

    // The born-digital half of the mixed page: one healthy layer line at the top.
    private static readonly BoundingBox LayerBox = new(5, 5, 60, 15);

    private sealed class RichTextLayer : ITextLayerReader
    {
        public TextLayerPage? Read(byte[] pdf, int pageNumber, int dpi) =>
            new(new[]
            {
                new TextLine(LayerBox, "healthy layer text", 1f, TextSource.TextLayer),
            }, WordCount: 50);
    }

    private sealed class FixedOcr(IReadOnlyList<TextLine> lines) : IOcrEngine
    {
        public int Calls;
        public IReadOnlyList<TextLine> Recognize(PageImage page)
        {
            Calls++;
            return lines;
        }
        public void Dispose() { }
    }

    private static TextLine OverlappingOcrLine =>
        new(LayerBox, "healthy layer text", 0.9f, TextSource.Ocr);          // OCR re-read of layer text

    private static TextLine ImageAreaOcrLine =>
        new(new BoundingBox(10, 50, 90, 60), "text recovered from the embedded image", 0.9f, TextSource.Ocr);

    private static DocumentProcessor NewProcessor(IOcrEngine ocr) =>
        new(new FakeRenderer(), new EmptyLayout(), ocr, new FakeTables(),
            new XyCutReadingOrder(), new RichTextLayer());

    private static ProcessingOptions Opts(bool recover = true) => new()
    {
        Dpi = 72,
        Pages = new[] { 1 },
        DetectOrientation = false,
        Verify = false,
        RecoverEmbeddedImageText = recover,
    };

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task LargeEmbeddedImage_OcrLinesMergedAdditively()
    {
        var ocr = new FixedOcr(new[] { OverlappingOcrLine, ImageAreaOcrLine });
        using var processor = NewProcessor(ocr);

        var result = await processor.ProcessAsync(MixedPagePdf.Build(coverage: 0.6), Opts());

        var page = Assert.Single(result.Pages);
        Assert.Equal(TextSource.TextLayer, page.Source);         // still a fast-path page
        Assert.Equal(1, ocr.Calls);

        // Layer text verbatim + image text added; the OCR re-read of layer text deduplicated.
        Assert.Contains(page.Lines, l => l.Text == "healthy layer text" && l.Source == TextSource.TextLayer);
        Assert.Contains(page.Lines, l => l.Text.Contains("recovered from the embedded image"));
        Assert.Equal(2, page.Lines.Count);
        Assert.Contains("recovered from the embedded image", page.Markdown);

        Assert.False(page.NeedsReview);                          // content WAS recovered
        Assert.NotNull(page.Notice);
        Assert.Contains("Mixed page", page.Notice);
        Assert.Contains("1 line(s) recovered", page.Notice);
        Assert.Empty(result.PagesNeedingReview);
    }

    [Fact]
    public async Task LargeEmbeddedImage_NothingRecovered_FlagsNeedsReview()
    {
        // OCR sees only the text the layer already has — the image yields nothing readable.
        var ocr = new FixedOcr(new[] { OverlappingOcrLine });
        using var processor = NewProcessor(ocr);

        var result = await processor.ProcessAsync(MixedPagePdf.Build(coverage: 0.6), Opts());

        var page = Assert.Single(result.Pages);
        var line = Assert.Single(page.Lines);                    // layer line only, nothing merged
        Assert.Equal(TextSource.TextLayer, line.Source);
        Assert.True(page.NeedsReview);                           // "at least flag that page"
        Assert.NotNull(page.Notice);
        Assert.Contains("content may be missing", page.Notice);
        Assert.Equal(new[] { 1 }, result.PagesNeedingReview);
    }

    [Fact]
    public async Task SmallImage_BelowCoverageThreshold_NoProbe()
    {
        var ocr = new FixedOcr(new[] { ImageAreaOcrLine });
        using var processor = NewProcessor(ocr);

        // 5% logo-sized image < MinEmbeddedImageCoverage (0.2) → pure fast path, no OCR pass.
        var result = await processor.ProcessAsync(MixedPagePdf.Build(coverage: 0.05), Opts());

        var page = Assert.Single(result.Pages);
        Assert.Equal(0, ocr.Calls);
        Assert.Single(page.Lines);
        Assert.False(page.NeedsReview);
        Assert.Null(page.Notice);
    }

    [Fact]
    public async Task OptionOff_NoProbe_OldBehavior()
    {
        var ocr = new FixedOcr(new[] { ImageAreaOcrLine });
        using var processor = NewProcessor(ocr);

        var result = await processor.ProcessAsync(MixedPagePdf.Build(coverage: 0.6), Opts(recover: false));

        var page = Assert.Single(result.Pages);
        Assert.Equal(0, ocr.Calls);
        Assert.False(page.NeedsReview);
        Assert.Null(page.Notice);
    }

    [Fact]
    public async Task UnparseablePdf_ProbeNeverThrows()
    {
        // The probe is best-effort: junk bytes → coverage 0 → no trigger, no crash.
        var ocr = new FixedOcr(new[] { ImageAreaOcrLine });
        using var processor = NewProcessor(ocr);

        var result = await processor.ProcessAsync(new byte[] { 0x25, 0x50, 0x44, 0x46 }, Opts());

        var page = Assert.Single(result.Pages);
        Assert.Equal(0, ocr.Calls);
        Assert.Null(page.Notice);
    }
}
