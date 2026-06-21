// The default IDocumentProcessor: render → (text-layer fast path | OCR) → layout →
// tables → reading order → Markdown, with per-page self-verification.
//
// Text-layer fast path (Phase 1 priority #1): born-digital pages take characters verbatim
// from the PDF text layer; OCR runs only when the layer is absent or suspiciously sparse.
// Layout detection always runs on the rendered image — structure comes from pixels,
// characters come from wherever they're most trustworthy.

using System.Diagnostics;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Tokens;

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
    private readonly OrientationDetector _orientation;
    private readonly IScanResolutionEstimator? _scanResolution;
    private readonly IScanUpscaler? _upscaler;
    private readonly IFormFieldExtractor? _formFields;
    private readonly IPageTemplateRouter? _templateRouter;
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
    /// <param name="orientation">Coarse page-orientation detector (0/90/180/270°), applied to pages
    /// routed to OCR when <see cref="ProcessingOptions.DetectOrientation"/> is on. Defaults to a new
    /// <see cref="OrientationDetector"/> with standard settings.</param>
    /// <param name="scanResolution">Optional effective-scan-resolution estimator. When supplied, pages
    /// routed to OCR report their estimated source DPI (<see cref="PageResult.EffectiveDpi"/>) and are
    /// flagged <see cref="PageResult.LowResolution"/> below <see cref="ProcessingOptions.MinScanDpi"/>.
    /// Null disables the estimate (default in the bare constructor; wired by
    /// <see cref="FoliantProcessor.CreateDefault"/>).</param>
    /// <param name="scanUpscaler">Optional pre-OCR upscaler for pages flagged
    /// <see cref="PageResult.LowResolution"/>, applied only when
    /// <see cref="ProcessingOptions.UpscaleLowResolutionScans"/> is on. Null disables upscaling
    /// (default in the bare constructor; wired by <see cref="FoliantProcessor.CreateDefault"/>).</param>
    public DocumentProcessor(
        IPageRenderer renderer,
        ILayoutDetector layout,
        IOcrEngine ocr,
        ITableExtractor tables,
        IReadingOrderAssembler readingOrder,
        ITextLayerReader textLayer,
        bool ownsComponents = false,
        IPagePreprocessor? preprocessor = null,
        OrientationDetector? orientation = null,
        IScanResolutionEstimator? scanResolution = null,
        IScanUpscaler? scanUpscaler = null,
        IFormFieldExtractor? formFields = null,
        IPageTemplateRouter? templateRouter = null)
    {
        _renderer = renderer;
        _layout = layout;
        _ocr = ocr;
        _tables = tables;
        _readingOrder = readingOrder;
        _textLayer = textLayer;
        _preprocessor = preprocessor;
        _orientation = orientation ?? new OrientationDetector();
        _scanResolution = scanResolution;
        _upscaler = scanUpscaler;
        _formFields = formFields;
        _templateRouter = templateRouter;
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
        var pageNumbers = (options.Pages is { Count: > 0 }
            ? options.Pages.Where(p => p >= 1 && p <= pageCount).Distinct().OrderBy(p => p)
            : Enumerable.Range(1, pageCount)).ToList();

        var pages = new List<PageResult>();
        int total = pageNumbers.Count, completed = 0;
        foreach (int pageNumber in pageNumbers)
        {
            ct.ThrowIfCancellationRequested();
            pages.Add(ProcessPage(pdf, pageNumber, options));
            // Per-page progress (1.1.0): reports CompletedPages/TotalPages after each page so callers
            // (e.g. a UI) show real progress that reaches 100% as the last page finishes.
            options.Progress?.Report(new ProcessingProgress(total, ++completed, pageNumber));
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

    // Recovers AcroForm/XFA filled field VALUES as positioned text lines (the values render in the
    // fillable boxes but are absent from the content-stream text layer). Reads /V off each visible
    // widget (then its /Parent), maps the widget rect into raster pixels with the same transform the
    // text-layer reader uses, and emits a TextLine. Best-effort; never throws.
    internal static List<TextLine> AcroFormValueLines(byte[] pdf, int pageNumber, int dpi)
    {
        var result = new List<TextLine>();
        try
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
            var page = doc.GetPage(pageNumber);
            float scale = dpi / 72f, pageH = (float)page.Height;
            foreach (var ann in page.GetAnnotations())
            {
                if (ann.Type != AnnotationType.Widget) continue;
                if (ann.Flags.HasFlag(AnnotationFlags.Hidden) || ann.Flags.HasFlag(AnnotationFlags.NoView)) continue;
                var d = ann.AnnotationDictionary;
                string? value = null;
                if (d.TryGet(NameToken.Create("V"), out StringToken v) && !string.IsNullOrWhiteSpace(v.Data))
                    value = v.Data;
                else if (d.TryGet(NameToken.Create("Parent"), out DictionaryToken p)
                         && p.TryGet(NameToken.Create("V"), out StringToken pv) && !string.IsNullOrWhiteSpace(pv.Data))
                    value = pv.Data;

                // Checkboxes/radios carry no text /V — their selected state is the widget's appearance
                // state /AS ("Off" = unchecked). Without this, checked boxes leave NO mark in the text
                // (the check renders as a glyph, not a content-stream character), so a side-by-side
                // PDF-vs-markdown comparison shows the selection silently missing. Emit a visible mark
                // for checked boxes so the form's selections survive into the output.
                if (value is null
                    && d.TryGet(NameToken.Create("AS"), out NameToken asTok)
                    && !string.Equals(asTok.Data, "Off", StringComparison.Ordinal))
                    value = "[X]";

                if (value is null) continue;

                var r = ann.Rectangle;   // PDF points, bottom-left origin → raster pixels, top-left
                float xA = (float)r.Left * scale, xB = (float)r.Right * scale;
                float yA = (pageH - (float)r.Top) * scale, yB = (pageH - (float)r.Bottom) * scale;
                var box = new BoundingBox(Math.Min(xA, xB), Math.Min(yA, yB), Math.Max(xA, xB), Math.Max(yA, yB));
                if (box.Width <= 0 || box.Height <= 0) continue;
                result.Add(new TextLine(box, value.Trim(), 1f, TextSource.TextLayer));
            }
        }
        catch { /* best-effort: never block extraction on form-value recovery */ }
        return result;
    }

    private PageResult ProcessPage(byte[] pdf, int pageNumber, ProcessingOptions options)
    {
        var sw = Stopwatch.StartNew();

        var image = _renderer.Render(pdf, pageNumber, options.Dpi);

        // Optional caller/test transform on the raw raster (external preprocessing, or
        // synthetic degradation for Gate 7). Runs before the text-layer decision and OCR.
        if (options.ImageTransform is { } transform)
            image = transform.Transform(image);

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

        // ── Scanned pages: correct coarse orientation, then fine cleanup, before OCR ─
        //    Both run only when characters must come from pixels (the OCR path).
        int orientationApplied = 0;
        int? effectiveDpi = null;
        bool lowResolution = false;
        if (!useLayer)
        {
            // Effective source DPI from the embedded scan image (not the fixed render DPI).
            // Computed from the original PDF bytes, independent of the in-memory raster transforms.
            if (_scanResolution is not null)
            {
                effectiveDpi = _scanResolution.EstimateEffectiveDpi(pdf, pageNumber);
                lowResolution = effectiveDpi is int dpi && dpi < options.MinScanDpi;
            }

            // Super-res seam: enlarge flagged low-resolution pages before orientation, preprocessing,
            // layout and OCR all run, so every downstream stage sees the upscaled raster. Advisory
            // EffectiveDpi/LowResolution still describe the original source scan, not the upscale.
            if (lowResolution && options.UpscaleLowResolutionScans
                && _upscaler is not null && options.LowResolutionUpscaleFactor > 1f)
            {
                image = _upscaler.Upscale(image, options.LowResolutionUpscaleFactor);
            }

            if (options.DetectOrientation)
                (image, orientationApplied) = _orientation.Correct(image, _ocr);
            if (options.PreprocessScans && _preprocessor != null)
                image = _preprocessor.Process(image).Image;
        }

        var lines = useLayer ? layer!.Lines : _ocr.Recognize(image);

        // ── AcroForm/XFA field VALUES live in the field widgets, not the content-stream text the
        //    fast path reads — so on the text-layer path they were silently dropped (and recall,
        //    measured against the same value-less text layer, still scored 100%). Inject the filled
        //    values as positioned text lines so they flow into layout/reading-order/composition and
        //    the output. The OCR path already captures them from the rendered widgets. ────────────
        if (useLayer)
        {
            var fieldLines = AcroFormValueLines(pdf, pageNumber, options.Dpi);
            if (fieldLines.Count > 0) lines = lines.Concat(fieldLines).ToList();
        }

        // ── Structure: always from pixels ────────────────────────────────────
        var regions = _layout.Detect(image);
        // Federal Standard Forms get schedule-aware table rendering (form-scoped; identified from the
        // page's printed "STANDARD FORM N" designation). Non-forms compose exactly as before.
        bool federalForm = FormIdentifier.IsFederalForm(lines);
        var composed = _composer.Compose(image, regions, lines, options.EnumeratorReadingOrder, federalForm);

        // ── Self-verification ────────────────────────────────────────────────
        int lost = ExtractionVerifier.CountLostLines(composed.Markdown, lines, composed.PageFurniture);
        int truthWords = 0, truthFound = 0;

        // Don't verify against a text layer we rejected as undecodable garbage (subset CID fonts
        // with no ToUnicode map — control-char codes). The "truth" would be the corruption itself,
        // so a recall score against it is meaningless. Such pages are OCR'd and reported with no
        // text-layer truth (RecallPercent == null), surfacing as "needs review" rather than a
        // misleading ~0%. The dropped-char (formmsd) class is intentionally NOT excluded: its
        // word text is usually real (only the glyph geometry is degenerate), so it stays valid truth.
        bool undecodableLayer = layer is not null
            && layer.UndecodableCharFraction > options.MaxTextLayerUndecodableFraction;

        if (options.Verify && !undecodableLayer)
        {
            // Furniture counts as extracted: it is intentionally kept aside, not lost —
            // otherwise near-blank pages with only headers/footers would score 0%.
            string recallText = composed.Markdown + "\n" +
                                string.Join("\n", composed.PageFurniture.Select(l => l.Text));
            (truthWords, truthFound) = ExtractionVerifier.TextLayerRecall(pdf, pageNumber, recallText);
        }

        // ── Typed key-value form fields (opt-in) ─────────────────────────────
        // AcroForm dictionary when present; geometric fallback (later) uses the page's lines.
        IReadOnlyList<FormField>? formFields = null;
        if (options.ExtractFormFields && _formFields is not null)
            formFields = _formFields.Extract(pdf, pageNumber, image, lines);

        // ── Template routing ─────────────────────────────────────────────────
        // A page recognized as a known form template gets deterministic, label-bound fields and an APPENDED
        // template-field section. Additive: the base Markdown, regions, and reading order are untouched, so
        // recall and ordering cannot regress; unmatched pages are unaffected. No-op unless a router is wired.
        string markdown = composed.Markdown;
        if (options.UseTemplateRouting && _templateRouter is not null
            && _templateRouter.TryRoute(pdf, pageNumber) is { } templated)
        {
            formFields = templated.Fields;   // deterministic; supersedes geometric extraction for this page
            markdown += "\n" + TemplateFieldSection.Render(templated);
        }

        sw.Stop();
        return new PageResult(
            pageNumber, image.Width, image.Height, options.Dpi,
            composed.Regions, lines, composed.PageFurniture,
            useLayer ? TextSource.TextLayer : TextSource.Ocr,
            markdown,
            new PageVerification(lost, truthWords, truthFound, sw.Elapsed.TotalSeconds),
            OrientationApplied: orientationApplied,
            EffectiveDpi: effectiveDpi,
            LowResolution: lowResolution,
            FormFields: formFields);
    }

    public void Dispose()
    {
        if (!_ownsComponents) return;
        _layout.Dispose();
        _ocr.Dispose();
        _tables.Dispose();
    }
}
