namespace Foliant;

/// <summary>
/// Per-page self-verification, computed during processing (not after the fact).
/// The coverage invariant makes "silently lost text" structurally impossible:
/// every extracted line provably lands in the output or is intentional page furniture.
/// </summary>
/// <param name="LinesLost">Extracted lines that failed to appear in the composed output. Must be 0.</param>
/// <param name="TruthWords">Words (length ≥ 3) in the PDF's embedded text layer; 0 when no text layer exists.</param>
/// <param name="TruthWordsFound">Of those, how many appear in the output (including page furniture).</param>
/// <param name="Seconds">Wall-clock processing time for the page.</param>
public sealed record PageVerification(
    int LinesLost,
    int TruthWords,
    int TruthWordsFound,
    double Seconds)
{
    /// <summary>Word recall vs the embedded text layer; null when the page has no text layer.</summary>
    public double? RecallPercent => TruthWords > 0 ? 100.0 * TruthWordsFound / TruthWords : null;

    public bool CoverageHolds => LinesLost == 0;
}

/// <summary>Result of processing a single page.</summary>
/// <param name="PageNumber">1-based page number.</param>
/// <param name="WidthPx">Rendered page width in pixels at <paramref name="Dpi"/>.</param>
/// <param name="HeightPx">Rendered page height in pixels at <paramref name="Dpi"/>.</param>
/// <param name="Dpi">Resolution the page was processed at.</param>
/// <param name="Regions">Output regions in reading order.</param>
/// <param name="Lines">All extracted text lines (the coverage-invariant source of truth).</param>
/// <param name="PageFurniture">Headers/footers/page numbers, kept as metadata rather than dropped.</param>
/// <param name="Source">Whether this page's text came from OCR or the embedded text layer.</param>
/// <param name="Markdown">The page's composed Markdown.</param>
/// <param name="Verification">Per-page self-verification results.</param>
/// <param name="Notice">
/// Optional structured notice when the page could not be normally extracted for a known
/// structural reason (e.g. a dynamic XFA form whose content lives in an XFA packet and is
/// unreachable without an Adobe engine). Null on normal pages. When set, callers should
/// treat the page as needing review rather than trusting <paramref name="Markdown"/>.
/// </param>
public sealed record PageResult(
    int PageNumber,
    int WidthPx,
    int HeightPx,
    int Dpi,
    IReadOnlyList<Region> Regions,
    IReadOnlyList<TextLine> Lines,
    IReadOnlyList<TextLine> PageFurniture,
    TextSource Source,
    string Markdown,
    PageVerification Verification,
    string? Notice = null);
