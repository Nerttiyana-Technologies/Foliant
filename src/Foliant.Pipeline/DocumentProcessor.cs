// The default IDocumentProcessor: render → (text-layer fast path | OCR) → layout →
// tables → reading order → Markdown, with per-page self-verification.
//
// Text-layer fast path (Phase 1 priority #1): born-digital pages take characters verbatim
// from the PDF text layer; OCR runs only when the layer is absent or suspiciously sparse.
// Layout detection always runs on the rendered image — structure comes from pixels,
// characters come from wherever they're most trustworthy.

using System.Diagnostics;

namespace Foliant.Pipeline;

public sealed class DocumentProcessor : IDocumentProcessor, IDisposable
{
    private readonly IPageRenderer _renderer;
    private readonly ILayoutDetector _layout;
    private readonly IOcrEngine _ocr;
    private readonly IReadingOrderAssembler _readingOrder;
    private readonly ITextLayerReader _textLayer;
    private readonly ITableExtractor _tables;
    private readonly IPagePreprocessor? _preprocessor;
    private readonly MarkdownComposer _composer;
    private readonly bool _ownsComponents;

    /// <param name="renderer">PDF page rasterizer.</param>
    /// <param name="layout">Layout-detection backend.</param>
    /// <param name="ocr">OCR backend (used when the text-layer fast path does not apply).</param>
    /// <param name="tables">Table-structure backend.</param>
    /// <param name="readingOrder">Reading-order assembler.</param>
    /// <param name="textLayer">Embedded text-layer reader (fast path + verification).</param>
    /// <param name="ownsComponents">When true, disposing this processor disposes the backends.</param>
    /// <param name="preprocessor">Optional scanned-page cleanup (deskew/contrast/despeckle), applied
    /// only to pages routed to OCR and only when <see cref="ProcessingOptions.PreprocessScans"/> is on.</param>
    public DocumentProcessor(
        IPageRenderer renderer,
        ILayoutDetector layout,
        IOcrEngine ocr,
        ITableExtractor tables,
        IReadingOrderAssembler readingOrder,
        ITextLayerReader textLayer,
        bool ownsComponents = false,
        IPagePreprocessor? preprocessor = null)
    {
        _renderer = renderer;
        _layout = layout;
        _ocr = ocr;
        _tables = tables;
        _readingOrder = readingOrder;
        _textLayer = textLayer;
        _preprocessor = preprocessor;
        _composer = new MarkdownComposer(readingOrder, tables);
        _ownsComponents = ownsComponents;
    }

    public Task<DocumentResult> ProcessAsync(
        byte[] pdf, ProcessingOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return Task.Run(() => Process(pdf, options ?? ProcessingOptions.Default, cancellationToken),
                        cancellationToken);
    }

    public async Task<DocumentResult> ProcessAsync(
        Stream pdf, ProcessingOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        using var buffer = new MemoryStream();
        await pdf.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return await ProcessAsync(buffer.ToArray(), options, cancellationToken).ConfigureAwait(false);
    }

    private DocumentResult Process(byte[] pdf, ProcessingOptions options, CancellationToken ct)
    {
        int pageCount = _renderer.GetPageCount(pdf);
        IEnumerable<int> pageNumbers = options.Pages is { Count: > 0 }
            ? options.Pages.Where(p => p >= 1 && p <= pageCount).Distinct().OrderBy(p => p)
            : Enumerable.Range(1, pageCount);

        var pages = new List<PageResult>();
        foreach (int pageNumber in pageNumbers)
        {
            ct.ThrowIfCancellationRequested();
            pages.Add(ProcessPage(pdf, pageNumber, options));
        }

        var md = new System.Text.StringBuilder();
        foreach (var page in pages)
        {
            if (md.Length > 0) md.AppendLine().AppendLine("---").AppendLine();
            md.AppendLine($"<!-- page {page.PageNumber} -->").AppendLine();
            md.Append(page.Markdown);
        }

        return new DocumentResult(pages, md.ToString());
    }

    private PageResult ProcessPage(byte[] pdf, int pageNumber, ProcessingOptions options)
    {
        var sw = Stopwatch.StartNew();

        var image = _renderer.Render(pdf, pageNumber, options.Dpi);

        // ── Characters: text layer when trustworthy, OCR otherwise ──────────
        TextLayerPage? layer = options.TextLayer == TextLayerMode.Never
            ? null
            : _textLayer.Read(pdf, pageNumber, options.Dpi);

        bool useLayer = options.TextLayer switch
        {
            TextLayerMode.Never => false,
            TextLayerMode.Always => layer is { WordCount: > 0 },
            // Auto: enough words AND the layer is trustworthy on both signals. A page can
            // satisfy the word count yet be unusable: body text discarded for degenerate
            // geometry (formmsd class → DroppedCharFraction), or glyphs with valid boxes but
            // no ToUnicode map (CID-magazine class → UndecodableCharFraction). Either routes OCR.
            _ => layer is not null
                 && layer.WordCount >= options.MinTextLayerWords
                 && layer.DroppedCharFraction <= options.MaxTextLayerDroppedCharFraction
                 && layer.UndecodableCharFraction <= options.MaxTextLayerUndecodableFraction,
        };

        // ── Dynamic XFA forms: content is locked in an XFA packet; the text layer AND the
        //    rendered page are both just the Adobe "Please wait…" placeholder, so neither the
        //    fast path nor OCR can recover anything. Emit an honest notice instead of passing
        //    the placeholder downstream as if it were document content. ──────────────────────
        if (layer is not null && PdfTextLayerReader.IsDynamicXfaPlaceholder(layer.Lines))
        {
            sw.Stop();
            const string notice = "dynamic XFA form — content is stored in an XFA packet and " +
                "cannot be extracted without an Adobe XFA engine (the page renders only the " +
                "viewer's \"Please wait\" placeholder)";
            return new PageResult(
                pageNumber, image.Width, image.Height, options.Dpi,
                Array.Empty<Region>(), Array.Empty<TextLine>(), Array.Empty<TextLine>(),
                TextSource.TextLayer,
                $"<!-- Foliant: {notice}. -->\n",
                new PageVerification(0, 0, 0, sw.Elapsed.TotalSeconds),
                Notice: notice);
        }

        // ── Scanned-page cleanup: only when characters must come from pixels ─
        if (!useLayer && options.PreprocessScans && _preprocessor != null)
            image = _preprocessor.Process(image).Image;

        var lines = useLayer ? layer!.Lines : _ocr.Recognize(image);

        // ── Structure: always from pixels ────────────────────────────────────
        var regions = _layout.Detect(image);
        var composed = _composer.Compose(image, regions, lines);

        // ── Self-verification ────────────────────────────────────────────────
        int lost = ExtractionVerifier.CountLostLines(composed.Markdown, lines, composed.PageFurniture);
        int truthWords = 0, truthFound = 0;
        if (options.Verify)
        {
            // Furniture counts as extracted: it is intentionally kept aside, not lost —
            // otherwise near-blank pages with only headers/footers would score 0%.
            string recallText = composed.Markdown + "\n" +
                                string.Join("\n", composed.PageFurniture.Select(l => l.Text));
            (truthWords, truthFound) = ExtractionVerifier.TextLayerRecall(pdf, pageNumber, recallText);
        }

        sw.Stop();
        return new PageResult(
            pageNumber, image.Width, image.Height, options.Dpi,
            composed.Regions, lines, composed.PageFurniture,
            useLayer ? TextSource.TextLayer : TextSource.Ocr,
            composed.Markdown,
            new PageVerification(lost, truthWords, truthFound, sw.Elapsed.TotalSeconds));
    }

    public void Dispose()
    {
        if (!_ownsComponents) return;
        _layout.Dispose();
        _ocr.Dispose();
        _tables.Dispose();
    }
}
