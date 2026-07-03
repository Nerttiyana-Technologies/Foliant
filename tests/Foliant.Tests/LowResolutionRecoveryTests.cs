using Foliant.Pipeline;
using Xunit;

namespace Foliant.Tests;

// ADR-0004 — low-resolution page recovery + honest verification (1.5.0).
//
// History: the original task-1 REPRO here pinned the 1.4.0 failure — a low-res scanned page whose
// OCR yielded nothing produced a structurally valid, EMPTY PageResult on which every safety metric
// passed vacuously (LinesLost = 0 of 0 lines; RecallPercent = null → invisible to aggregates → the
// customer's "100% recall on an empty document") and no Notice was set. These tests are that repro
// FLIPPED to the fixed contract, and are the Gate 9c honesty guard:
//
//   * retry ladder: LowResolution && < LowResolutionRetryMinWords words → rung 1 (upscaler) →
//     rung 2 (re-render ≤ 600 DPI), keep-better (more words wins; ties keep the first pass);
//   * honesty: an OCR page with ~no words and no text-layer truth carries NeedsReview + Notice,
//     and surfaces in DocumentResult.PagesNeedingReview.
public class LowResolutionRecoveryTests
{
    // ── Fakes (mirroring DocumentProcessorTests'; those are private to that class) ──────────

    private sealed class FakeRenderer : IPageRenderer
    {
        public int RenderCalls;
        public int LastRenderDpi;
        public int GetPageCount(byte[] pdf) => 1;
        public PageImage Render(byte[] pdf, int pageNumber, int dpi)
        {
            RenderCalls++;
            LastRenderDpi = dpi;
            return new PageImage(100, 100, dpi, new byte[100 * 100 * 4]);
        }
    }

    // Layout confidence collapses on upsampled low-DPI mush (DocLayoutNetDetector) → no regions.
    private sealed class EmptyLayout : ILayoutDetector
    {
        public IReadOnlyList<LayoutRegion> Detect(PageImage page) => Array.Empty<LayoutRegion>();
        public void Dispose() { }
    }

    // Scripted OCR: returns responses[call] (last response repeats). Models PaddleOCR's sub-3×3
    // detection filter dropping everything on the first pass, then a retry rung seeing more.
    private sealed class SeqOcr(params IReadOnlyList<TextLine>[] responses) : IOcrEngine
    {
        public int Calls;
        public IReadOnlyList<TextLine> Recognize(PageImage page) =>
            responses[Math.Min(Calls++, responses.Length - 1)];
        public void Dispose() { }
    }

    private static IReadOnlyList<TextLine> NoLines => Array.Empty<TextLine>();

    private static IReadOnlyList<TextLine> Lines(string text) =>
        new[] { new TextLine(new BoundingBox(5, 5, 90, 15), text, 0.9f, TextSource.Ocr) };

    private sealed class FakeTables : ITableExtractor
    {
        public TableExtraction Extract(PageImage page, LayoutRegion table, IReadOnlyList<TextLine> pageLines) =>
            new(null, Array.Empty<TextLine>());
        public void Dispose() { }
    }

    // A scanned page: no text layer at all.
    private sealed class NoTextLayer : ITextLayerReader
    {
        public TextLayerPage? Read(byte[] pdf, int pageNumber, int dpi) => null;
    }

    private sealed class FakeScanResolution(int? dpi) : IScanResolutionEstimator
    {
        public int? EstimateEffectiveDpi(byte[] pdf, int pageNumber) => dpi;
    }

    private sealed class FakeUpscaler : IScanUpscaler
    {
        public int Calls;
        public PageImage Upscale(PageImage image, float factor)
        {
            Calls++;
            int w = (int)(image.Width * factor), h = (int)(image.Height * factor);
            return new PageImage(w, h, image.Dpi, new byte[w * h * 4]);
        }
    }

    private static DocumentProcessor NewProcessor(
        FakeRenderer renderer, IOcrEngine ocr, IScanResolutionEstimator estimator,
        IScanUpscaler? upscaler = null) =>
        new(renderer, new EmptyLayout(), ocr, new FakeTables(),
            new XyCutReadingOrder(), new NoTextLayer(),
            scanResolution: estimator, scanUpscaler: upscaler);

    // A real, parseable PDF with no content-stream text and an EMPTY widget value, so the
    // verifier finds zero truth words — exactly a scanned page's shape (RecallPercent null).
    private static byte[] ScannedShapePdf() => FilledFormPdf.Build(value: "");

    private static ProcessingOptions Opts(int dpi = 72, bool retry = true) => new()
    {
        Dpi = dpi,
        Pages = new[] { 1 },
        DetectOrientation = false,   // isolate the retry/honesty logic (no thumbnail OCR)
        RetryLowResolutionPages = retry,
        // Verify stays ON (default): the honesty contract is precisely about verification
        // passing vacuously on these pages.
    };

    // ── Honesty (Gate 9c): unrecovered pages must be impossible to miss ─────────────────────

    [Fact]
    public async Task EmptyLowResPage_RetriesLadder_ThenFlagsNeedsReview()
    {
        var renderer = new FakeRenderer();
        var ocr = new SeqOcr(NoLines);   // OCR yields nothing, every pass
        using var processor = NewProcessor(renderer, ocr, new FakeScanResolution(72));

        var result = await processor.ProcessAsync(ScannedShapePdf(), Opts());

        var page = Assert.Single(result.Pages);
        // The pipeline knows the page is a low-res scan, and the page is still empty…
        Assert.Equal(TextSource.Ocr, page.Source);
        Assert.True(page.LowResolution);
        Assert.Empty(page.Lines);
        Assert.True(page.Verification.CoverageHolds);           // still vacuously green
        Assert.Null(page.Verification.RecallPercent);           // still invisible to aggregates

        // …but 1.5.0 tried the ladder (no upscaler wired → rung 2 re-render at 2×72=144 DPI)…
        Assert.Equal(2, ocr.Calls);                              // first pass + rung 2
        Assert.Equal(2, renderer.RenderCalls);
        Assert.Equal(144, renderer.LastRenderDpi);
        Assert.Equal(72, page.Dpi);                              // tie → first pass kept

        // …and the failure is LOUD: flagged, explained, and listed at document level.
        Assert.True(page.NeedsReview);
        Assert.NotNull(page.Notice);
        Assert.Contains("manual review", page.Notice);
        Assert.Contains("after retry", page.Notice);
        Assert.Contains("~72 DPI", page.Notice);
        Assert.Equal(new[] { 1 }, result.PagesNeedingReview);
    }

    [Fact]
    public async Task EmptyPage_AtHealthyDpi_NoRetry_ButStillFlagged()
    {
        var renderer = new FakeRenderer();
        var ocr = new SeqOcr(NoLines);
        using var processor = NewProcessor(renderer, ocr, new FakeScanResolution(300));

        var result = await processor.ProcessAsync(ScannedShapePdf(), Opts(dpi: 300));

        var page = Assert.Single(result.Pages);
        Assert.False(page.LowResolution);
        Assert.Equal(1, ocr.Calls);                              // trigger requires LowResolution
        Assert.True(page.NeedsReview);                           // honesty is unconditional
        Assert.NotNull(page.Notice);
        Assert.Contains("no extractable text", page.Notice);
        Assert.DoesNotContain("Low-resolution", page.Notice);    // don't blame DPI at 300
    }

    [Fact]
    public async Task RetryDisabled_NoRetry_StillFlagged()
    {
        var renderer = new FakeRenderer();
        var ocr = new SeqOcr(NoLines);
        using var processor = NewProcessor(renderer, ocr, new FakeScanResolution(72));

        var result = await processor.ProcessAsync(ScannedShapePdf(), Opts(retry: false));

        var page = Assert.Single(result.Pages);
        Assert.Equal(1, ocr.Calls);
        Assert.True(page.NeedsReview);
        Assert.NotNull(page.Notice);
        Assert.DoesNotContain("after retry", page.Notice);       // no retry ran; don't claim one
    }

    [Fact]
    public async Task TextLayerPage_IsNeverFlagged()
    {
        // Born-digital fast path: NeedsReview must never fire, whatever the word count.
        var ocr = new SeqOcr(NoLines);
        using var processor = new DocumentProcessor(
            new FakeRenderer(), new EmptyLayout(), ocr, new FakeTables(),
            new XyCutReadingOrder(), new SparseTextLayer(),
            scanResolution: new FakeScanResolution(72));

        var result = await processor.ProcessAsync(ScannedShapePdf(), new ProcessingOptions
        {
            Dpi = 72, Pages = new[] { 1 }, DetectOrientation = false,
            TextLayer = TextLayerMode.Always, Verify = false,
        });

        var page = Assert.Single(result.Pages);
        Assert.Equal(TextSource.TextLayer, page.Source);
        Assert.False(page.NeedsReview);
        Assert.Null(page.Notice);
        Assert.Empty(result.PagesNeedingReview);
    }

    private sealed class SparseTextLayer : ITextLayerReader
    {
        public TextLayerPage? Read(byte[] pdf, int pageNumber, int dpi) =>
            new(new[]
            {
                new TextLine(new BoundingBox(5, 5, 60, 15), "hi", 1f, TextSource.TextLayer),
            }, WordCount: 1);
    }

    // ── Recovery (retry ladder mechanics) ────────────────────────────────────────────────────

    [Fact]
    public async Task Rung1_UpscaleRecovers_NoticeSaysRecovered_NotFlagged()
    {
        var renderer = new FakeRenderer();
        var ocr = new SeqOcr(NoLines, Lines("recovered text from upscaled raster"));
        var upscaler = new FakeUpscaler();
        using var processor = NewProcessor(renderer, ocr, new FakeScanResolution(100), upscaler);

        var result = await processor.ProcessAsync(ScannedShapePdf(), Opts(dpi: 300));

        var page = Assert.Single(result.Pages);
        Assert.Equal(2, ocr.Calls);                              // first pass + rung 1 only
        Assert.Equal(1, upscaler.Calls);
        Assert.Equal(1, renderer.RenderCalls);                   // rung 2 never needed
        Assert.Contains(page.Lines, l => l.Text.Contains("recovered"));
        Assert.False(page.NeedsReview);
        Assert.NotNull(page.Notice);
        Assert.Contains("recovered via 2× upscale retry", page.Notice);
        Assert.Empty(result.PagesNeedingReview);
    }

    [Fact]
    public async Task Rung2_RerenderRecovers_GeometryDpiIsTheRetryRender()
    {
        var renderer = new FakeRenderer();
        // First pass empty; rung 1 (upscale) still empty; rung 2 (re-render) recovers.
        var ocr = new SeqOcr(NoLines, NoLines, Lines("recovered text from rerendered page"));
        var upscaler = new FakeUpscaler();
        using var processor = NewProcessor(renderer, ocr, new FakeScanResolution(72), upscaler);

        var result = await processor.ProcessAsync(ScannedShapePdf(), Opts(dpi: 200));

        var page = Assert.Single(result.Pages);
        Assert.Equal(3, ocr.Calls);
        Assert.Equal(2, renderer.RenderCalls);
        Assert.Equal(400, renderer.LastRenderDpi);               // min(2×200, 600)
        Assert.Equal(400, page.Dpi);                             // geometry lives in retry space
        Assert.False(page.NeedsReview);
        Assert.NotNull(page.Notice);
        Assert.Contains("recovered via re-render at 400 DPI retry", page.Notice);
    }

    [Fact]
    public async Task KeepBetter_RetryCanNeverLoseWords()
    {
        var renderer = new FakeRenderer();
        // First pass: 1 word (under the 3-word trigger). Retries: nothing. Keep the 1 word.
        var ocr = new SeqOcr(Lines("survivor"), NoLines, NoLines);
        var upscaler = new FakeUpscaler();
        using var processor = NewProcessor(renderer, ocr, new FakeScanResolution(72), upscaler);

        var result = await processor.ProcessAsync(ScannedShapePdf(), Opts());

        var page = Assert.Single(result.Pages);
        var line = Assert.Single(page.Lines);                    // first pass kept — nothing lost
        Assert.Equal("survivor", line.Text);
        Assert.Equal(72, page.Dpi);
        Assert.True(page.NeedsReview);                           // 1 word < 3: still a failure
        Assert.NotNull(page.Notice);
        Assert.Contains("after retry", page.Notice);
    }

    [Fact]
    public async Task HealthyWordCount_NeverTriggersRetry()
    {
        var renderer = new FakeRenderer();
        var ocr = new SeqOcr(Lines("three words here"));         // exactly the threshold: not < 3
        var upscaler = new FakeUpscaler();
        using var processor = NewProcessor(renderer, ocr, new FakeScanResolution(72), upscaler);

        var result = await processor.ProcessAsync(ScannedShapePdf(), Opts());

        var page = Assert.Single(result.Pages);
        Assert.Equal(1, ocr.Calls);                              // single pass — Gate 9b shape
        Assert.Equal(0, upscaler.Calls);
        Assert.Equal(1, renderer.RenderCalls);
        Assert.False(page.NeedsReview);
        Assert.Null(page.Notice);
    }
}
