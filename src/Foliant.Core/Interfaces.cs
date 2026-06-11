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

/// <summary>The embedded text layer of one PDF page, mapped to raster coordinates.</summary>
/// <param name="Lines">Text lines grouped from the embedded words, in raster coordinates.</param>
/// <param name="WordCount">Raw word count before line grouping (drives the Auto fast-path decision).</param>
public sealed record TextLayerPage(
    IReadOnlyList<TextLine> Lines,
    int WordCount);

/// <summary>Reads a PDF's embedded text layer (born-digital fast path + verification truth).</summary>
public interface ITextLayerReader
{
    /// <returns>Null when the page has no text layer at all.</returns>
    TextLayerPage? Read(byte[] pdf, int pageNumber, int dpi);
}
