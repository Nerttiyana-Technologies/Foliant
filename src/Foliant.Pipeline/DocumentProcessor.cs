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
    /// <param name="formFields">Optional typed key-value form-field extractor; populates
    /// <see cref="PageResult.FormFields"/> when <see cref="ProcessingOptions.ExtractFormFields"/> is on.
    /// Null disables it (the default bare constructor wires none).</param>
    /// <param name="templateRouter">Optional per-page template router. When supplied and
    /// <see cref="ProcessingOptions.UseTemplateRouting"/> is on, a page recognized as a known form gets
    /// deterministic, label-bound fields plus an appended template-field Markdown section. Null disables it
    /// (the default pipeline wires none).</param>
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
        int renderDpi = options.Dpi;      // actual render DPI of the raster the page ends up using
        bool retried = false;             // the ADR-0004 retry ladder ran on this page
        string? recoveredVia = null;      // set when a retry rung's result was kept
        IReadOnlyList<TextLine> lines;
        int imageLinesRecovered = 0;        // mixed page: OCR lines merged from embedded image content
        bool imageContentSuspected = false; // mixed-page probe fired on this fast-path page
        if (useLayer)
        {
            lines = layer!.Lines;

            // ── Mixed pages (ADR-0004 addendum): born-digital text PLUS a large embedded image —
            //    a scanned letter pasted into a proposal, a price table inserted as a screenshot.
            //    The image's text exists only as pixels, so the fast path would silently drop it
            //    while recall (scored against the same image-less text layer) still reports 100%.
            //    Probe for significant embedded images and OCR-merge the lines the layer does not
            //    already cover: layer text stays verbatim, image text is recovered additively. ──
            if (options.RecoverEmbeddedImageText
                && EmbeddedImageProbe.Coverage(pdf, pageNumber) >= options.MinEmbeddedImageCoverage)
            {
                imageContentSuspected = true;
                var ocrLines = _ocr.Recognize(image);
                var added = ocrLines.Where(o => !OverlapsAnyLine(o, lines)).ToList();
                if (added.Count > 0)
                {
                    lines = lines.Concat(added).ToList();
                    imageLinesRecovered = added.Count;
                }
            }
        }
        else
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
            // (Always-on path, off by default per the Gate 8 verdict — distinct from the retry below.)
            if (lowResolution && options.UpscaleLowResolutionScans
                && _upscaler is not null && options.LowResolutionUpscaleFactor > 1f)
            {
                image = _upscaler.Upscale(image, options.LowResolutionUpscaleFactor);
            }

            if (options.DetectOrientation)
                (image, orientationApplied) = _orientation.Correct(image, _ocr);

            // Pixel stages, re-runnable for the ADR-0004 retry ladder. Orientation is intentionally
            // NOT re-run in retries (it already ran; re-running quadruples thumbnail OCR cost for
            // no signal).
            (PageImage Image, IReadOnlyList<TextLine> Lines) RunPixelStages(PageImage raster)
            {
                if (options.PreprocessScans && _preprocessor != null)
                    raster = _preprocessor.Process(raster).Image;
                return (raster, _ocr.Recognize(raster));
            }

            var baseRaster = image;                       // post-orientation, pre-preprocess
            var best = RunPixelStages(baseRaster);
            // Confidence-floored count: hallucinated texture read as garbage words must neither
            // win keep-better nor mask the honesty flag (LowResolutionRetryMinConfidence).
            float minConf = options.LowResolutionRetryMinConfidence;
            int bestWords = CountWords(best.Lines, minConf);

            // ── ADR-0004 retry ladder ─────────────────────────────────────────
            // Trigger: flagged low-resolution AND the first pass produced ~nothing. The baseline
            // is an empty page, so a retry can only add words (keep-better guarantees
            // monotonicity); pages that never trigger take exactly the single-pass path — which
            // is how this escapes the Gate 8 always-on-upscale net-negative verdict.
            if (options.RetryLowResolutionPages && lowResolution
                && bestWords < options.LowResolutionRetryMinWords)
            {
                retried = true;

                // Rung 1: upscale the raster we already have with the wired IScanUpscaler.
                if (_upscaler is not null && options.LowResolutionUpscaleFactor > 1f)
                {
                    var candidate = RunPixelStages(
                        _upscaler.Upscale(baseRaster, options.LowResolutionUpscaleFactor));
                    int words = CountWords(candidate.Lines, minConf);
                    if (words > bestWords)
                    {
                        (best, bestWords) = (candidate, words);
                        recoveredVia = $"{options.LowResolutionUpscaleFactor:0.#}× upscale retry";
                    }
                }

                // Rung 2: re-render the page at elevated DPI (capped at 600), only if still under
                // the threshold. The caller/test ImageTransform and any orientation correction are
                // re-applied so the re-render faces the same conditions as the first pass
                // (Gate 7/8 degradations live in the transform!).
                int retryDpi = Math.Min(2 * options.Dpi, 600);
                if (bestWords < options.LowResolutionRetryMinWords && retryDpi > options.Dpi)
                {
                    var rerendered = _renderer.Render(pdf, pageNumber, retryDpi);
                    if (options.ImageTransform is { } retryTransform)
                        rerendered = retryTransform.Transform(rerendered);
                    if (orientationApplied != 0)
                        rerendered = ScanDegrader.Rotate(orientationApplied).Transform(rerendered);
                    // Upscale ONLY when the 600-DPI cap kept the re-render below the intended 2×
                    // (options.Dpi > 300). An uncapped re-render already doubled the pixel budget;
                    // stacking another upscale on top of it buys nothing and — with a 4×-native SR
                    // backend — explodes the intermediate buffer past the array limit.
                    if (retryDpi < 2 * options.Dpi
                        && _upscaler is not null && options.LowResolutionUpscaleFactor > 1f)
                        rerendered = _upscaler.Upscale(rerendered, options.LowResolutionUpscaleFactor);

                    var candidate = RunPixelStages(rerendered);
                    int words = CountWords(candidate.Lines, minConf);
                    if (words > bestWords)
                    {
                        (best, bestWords) = (candidate, words);
                        renderDpi = retryDpi;   // line/region geometry lives in this raster's space
                        recoveredVia = $"re-render at {retryDpi} DPI retry";
                    }
                }
            }

            image = best.Image;
            lines = best.Lines;
        }

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

        // Decide template routing BEFORE composition. No-op unless a router is wired.
        var templated = (options.UseTemplateRouting && _templateRouter is not null)
            ? _templateRouter.TryRoute(pdf, pageNumber)
            : null;

        // By-identity fallback: a federal form with no usable widget signature (flattened or scanned) gets
        // matched by its printed "STANDARD FORM N" designation and bound to the known template geometry
        // (checkbox state from pixels, values from OCR within each field rect). Federal-scoped; only fires
        // when the widget route missed AND the page is a recognized Standard Form.
        bool scannedMatch = false;
        if (templated is null && options.UseTemplateRouting && federalForm
            && _templateRouter is IScannedFormRouter scanned
            && FormIdentifier.Identify(lines) is { } designation)
        {
            string? revisionYear = FormIdentifier.IdentifyRevisionYear(lines);
            templated = scanned.TryRouteByDesignation(designation, revisionYear, image, lines, pageNumber);
            scannedMatch = templated is not null;
        }

        // plainFormBody (flatten the form region to reading-order prose) is ONLY for the scanned/by-identity
        // path, where the OCR'd form grid would be garbled. A born-digital WIDGET match keeps its clean
        // FederalFormTableRenderer grid (each value stays in its labelled cell, e.g. "5. SOLICITATION NUMBER
        // 80TECH24R0001"); flattening that grid would scatter values away from their labels. The structured
        // values are appended as a Form-fields section below regardless of which body rendering is used.
        var composed = _composer.Compose(
            image, regions, lines, options.EnumeratorReadingOrder, federalForm,
            plainFormBody: scannedMatch);

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

        // ── Template routing output ──────────────────────────────────────────
        // A matched page gets deterministic, label-bound fields plus an APPENDED template-field section.
        // The base Markdown already rendered the form region as clean prose (plainFormBody above); appending
        // is additive, so no text is lost and unmatched pages are unaffected.
        string markdown = composed.Markdown;
        if (templated is { } t)
        {
            formFields = t.Fields;   // deterministic; supersedes geometric extraction for this page
            markdown += "\n" + TemplateFieldSection.Render(t);
        }

        // ── Honest metrics (ADR-0004) ────────────────────────────────────────
        // A page whose text came from pixels and produced ~nothing, with no text-layer truth to
        // vouch for it (truthWords == 0 → RecallPercent null → invisible to recall aggregates),
        // is a FAILED extraction and must be impossible to miss — not a quiet success.
        int pageWords = CountWords(lines, options.LowResolutionRetryMinConfidence);
        bool needsReview = !useLayer && truthWords == 0 && pageWords < options.LowResolutionRetryMinWords;
        string? pageNotice = null;
        if (!useLayer)
        {
            if (needsReview)
                pageNotice = lowResolution && effectiveDpi is int srcDpi
                    ? $"Low-resolution scan (~{srcDpi} DPI): no text could be recovered" +
                      (retried ? " after retry" : "") + " — page needs manual review."
                    : "Scanned page produced no extractable text — page needs manual review.";
            else if (recoveredVia is not null)
                pageNotice = $"Low-resolution scan (~{effectiveDpi} DPI): recovered via {recoveredVia}.";
        }
        else if (imageContentSuspected)
        {
            // Mixed page verdict. Either the image's text was recovered (informational — reviewers
            // can see what was added), or nothing could be read out of a large embedded image and
            // the page must be flagged: its visible content may be missing from the output while
            // recall — blind to pixels-only content — reports 100%.
            if (imageLinesRecovered > 0)
            {
                pageNotice = $"Mixed page: {imageLinesRecovered} line(s) recovered via OCR from " +
                             "embedded image content absent from the text layer.";
            }
            else
            {
                needsReview = true;
                pageNotice = "Page embeds a large image but no additional text could be recovered " +
                             "from it — visible content may be missing; page needs manual review.";
            }
        }

        sw.Stop();
        return new PageResult(
            pageNumber, image.Width, image.Height, renderDpi,
            composed.Regions, lines, composed.PageFurniture,
            useLayer ? TextSource.TextLayer : TextSource.Ocr,
            markdown,
            new PageVerification(lost, truthWords, truthFound, sw.Elapsed.TotalSeconds),
            Notice: pageNotice,
            OrientationApplied: orientationApplied,
            EffectiveDpi: effectiveDpi,
            LowResolution: lowResolution,
            FormFields: formFields,
            NeedsReview: needsReview);
    }

    private static int CountWords(IReadOnlyList<TextLine> lines, float minConfidence = 0f) =>
        lines.Where(l => l.Confidence >= minConfidence)
             .Sum(l => l.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);

    // An OCR line already covered by the text layer: ≥30% of its box intersects a layer line's
    // box. Layer text re-read by OCR lands on top of its source and is dropped; embedded-image
    // content has no layer lines beneath it and survives the merge.
    private static bool OverlapsAnyLine(TextLine candidate, IReadOnlyList<TextLine> existing)
    {
        float area = Math.Max(1f, candidate.Bounds.Width * candidate.Bounds.Height);
        foreach (var line in existing)
        {
            float ix = Math.Min(candidate.Bounds.X2, line.Bounds.X2) - Math.Max(candidate.Bounds.X1, line.Bounds.X1);
            float iy = Math.Min(candidate.Bounds.Y2, line.Bounds.Y2) - Math.Max(candidate.Bounds.Y1, line.Bounds.Y1);
            if (ix > 0 && iy > 0 && ix * iy / area > 0.3f) return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (!_ownsComponents) return;
        _layout.Dispose();
        _ocr.Dispose();
        _tables.Dispose();
    }
}
