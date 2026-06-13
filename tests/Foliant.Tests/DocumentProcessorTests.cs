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

    private static DocumentProcessor NewProcessor(FakeOcr ocr, ITextLayerReader textLayer) =>
        new(new FakeRenderer(), new FakeLayout(), ocr, new FakeTables(),
            new XyCutReadingOrder(), textLayer);

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
    public async Task Auto_SparseTextLayer_FallsBackToOcr()
    {
        var ocr = new FakeOcr();
        using var processor = NewProcessor(ocr, new FakeTextLayer { WordCount = 3 });   // < MinTextLayerWords

        var result = await processor.ProcessAsync(FakePdf, new ProcessingOptions { Verify = false });

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

        var result = await processor.ProcessAsync(FakePdf, new ProcessingOptions { Verify = false });

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

        var result = await processor.ProcessAsync(FakePdf, new ProcessingOptions { Verify = false });

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
    public async Task Never_AlwaysOcrs()
    {
        var ocr = new FakeOcr();
        using var processor = NewProcessor(ocr, new FakeTextLayer { WordCount = 500 });

        var result = await processor.ProcessAsync(
            FakePdf, new ProcessingOptions { TextLayer = TextLayerMode.Never, Verify = false });

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
