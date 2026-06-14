namespace Foliant;

/// <summary>Top-level entry point: PDF in, structured layout-aware extraction out.</summary>
public interface IDocumentProcessor
{
    Task<DocumentResult> ProcessAsync(
        byte[] pdf, ProcessingOptions? options = null, CancellationToken cancellationToken = default);

    Task<DocumentResult> ProcessAsync(
        Stream pdf, ProcessingOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>Renders PDF pages to raster images.</summary>
public interface IPageRenderer
{
    int GetPageCount(byte[] pdf);

    /// <param name="pdf">The PDF file contents.</param>
    /// <param name="pageNumber">1-based page number.</param>
    /// <param name="dpi">Render resolution in dots per inch.</param>
    PageImage Render(byte[] pdf, int pageNumber, int dpi);
}

/// <summary>Stage 2 — classifies page regions (text, title, table, figure, furniture, ...).</summary>
public interface ILayoutDetector : IDisposable
{
    IReadOnlyList<LayoutRegion> Detect(PageImage page);
}

/// <summary>Stage 3 — recognizes text lines with bounds from page pixels.</summary>
public interface IOcrEngine : IDisposable
{
    IReadOnlyList<TextLine> Recognize(PageImage page);
}

/// <summary>Stage 4 — extracts a row/column grid for one table region.</summary>
public interface ITableExtractor : IDisposable
{
    TableExtraction Extract(PageImage page, LayoutRegion table, IReadOnlyList<TextLine> pageLines);
}

/// <summary>Stage 5 — orders layout regions into logical reading sequence.</summary>
public interface IReadingOrderAssembler
{
    IReadOnlyList<LayoutRegion> Order(IReadOnlyList<LayoutRegion> regions);
}

/// <summary>
/// Estimates the effective source resolution (DPI) of a scanned page from its embedded raster
/// images. Distinct from the render DPI (<see cref="ProcessingOptions.Dpi"/>), a fixed
/// rasterization target that carries no information about scan quality. Used to flag low-resolution
/// scans (<see cref="PageResult.LowResolution"/>) whose OCR is lower-confidence.
/// </summary>
public interface IScanResolutionEstimator
{
    /// <param name="pdf">The PDF file contents.</param>
    /// <param name="pageNumber">1-based page number.</param>
    /// <returns>
    /// Effective DPI of the dominant full-page scan image, or null when the page has no image
    /// large enough to be a scan (born-digital pages, or pages with only small decorations).
    /// </returns>
    int? EstimateEffectiveDpi(byte[] pdf, int pageNumber);
}

/// <summary>
/// Upscales a low-resolution scanned page image before OCR. The default backend is classical
/// (bicubic) resampling; an ML super-resolution backend can replace it without pipeline changes.
/// Applied only to pages flagged <see cref="PageResult.LowResolution"/> when
/// <see cref="ProcessingOptions.UpscaleLowResolutionScans"/> is on.
/// </summary>
public interface IScanUpscaler
{
    /// <param name="image">The rendered page raster.</param>
    /// <param name="factor">Linear scale factor; values ≤ 1 are a no-op.</param>
    /// <returns>The upscaled page (or the original when no upscale is warranted).</returns>
    PageImage Upscale(PageImage image, float factor);
}

/// <summary>The embedded text layer of one PDF page, mapped to raster coordinates.</summary>
/// <param name="Lines">Text lines grouped from the embedded words, in raster coordinates.</param>
/// <param name="WordCount">Raw word count before line grouping (drives the Auto fast-path decision).</param>
/// <param name="DroppedCharFraction">
/// Fraction of text-layer characters (0..1) that belonged to words discarded for unusable
/// geometry (degenerate bounding boxes). Old PDFs with non-embedded fonts (e.g. 1990s
/// PageMaker output where the viewer must substitute Times/Helvetica) can yield words whose
/// glyph metrics are unresolvable — the text exists but its boxes collapse. A high fraction
/// means the text layer is present but untrustworthy, and Auto mode routes the page to OCR.
/// </param>
/// <param name="UndecodableCharFraction">
/// Fraction of text-layer characters (0..1) that are control/non-printable — the fingerprint
/// of a subset CID font with no usable ToUnicode map (seen on magazines run through some
/// "PDF optimizer" tools). Unlike <paramref name="DroppedCharFraction"/>, these glyphs have
/// VALID boxes, so they pass geometry checks while still being garbage; the page renders fine
/// and must go to OCR. A high fraction routes the page to OCR in Auto mode.
/// </param>
public sealed record TextLayerPage(
    IReadOnlyList<TextLine> Lines,
    int WordCount,
    float DroppedCharFraction = 0f,
    float UndecodableCharFraction = 0f);

/// <summary>Reads a PDF's embedded text layer (born-digital fast path + verification truth).</summary>
public interface ITextLayerReader
{
    /// <returns>Null when the page has no text layer at all.</returns>
    TextLayerPage? Read(byte[] pdf, int pageNumber, int dpi);
}
