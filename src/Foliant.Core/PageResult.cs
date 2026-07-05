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
/// <param name="OrientationApplied">
/// Coarse rotation (degrees clockwise: 0/90/180/270) the orientation detector applied to bring
/// this page upright before OCR. 0 when the page was already upright or detection was disabled.
/// </param>
/// <param name="EffectiveDpi">
/// Estimated native resolution of this page's dominant scan image (its pixel size relative to its
/// physical size on the page), or null when the page is not a scan or has no image large enough to
/// estimate from (born-digital fast-path pages, or pages with only small decorations). Distinct
/// from <paramref name="Dpi"/>, which is the fixed render target. Only computed on OCR-routed pages.
/// </param>
/// <param name="LowResolution">
/// True when <paramref name="EffectiveDpi"/> is below <see cref="ProcessingOptions.MinScanDpi"/> —
/// an advisory signal that this scan is low-resolution and its OCR is lower-confidence. The page's
/// <paramref name="Markdown"/> is still produced and usable; this never suppresses output.
/// </param>
/// <param name="FormFields">
/// Typed key-value form fields extracted from this page (<see cref="FormField"/>), or null when
/// extraction was not requested (<see cref="ProcessingOptions.ExtractFormFields"/> off) or no
/// extractor is wired. Empty when extraction ran but found no fields.
/// </param>
/// <param name="NeedsReview">
/// True when this page is a FAILED or SUSPECT extraction, not a quiet success — either an
/// OCR-routed page that produced fewer than
/// <see cref="ProcessingOptions.LowResolutionRetryMinWords"/> words with no text-layer truth to
/// vouch for it (<see cref="PageVerification.RecallPercent"/> is null — invisible to recall
/// aggregates), or a text-layer page embedding a large image whose content could not be
/// recovered (<see cref="ProcessingOptions.RecoverEmbeddedImageText"/>) — recall is blind to
/// pixels-only content, so it reports 100% on such pages. Callers aggregating recall MUST also
/// surface <see cref="DocumentResult.PagesNeedingReview"/>: a document can no longer report
/// 100% recall while silently missing content. Accompanied by a <see cref="Notice"/>.
/// </param>
/// <param name="SensitivityMarking">
/// The most severe sensitivity banner marking detected on this page (e.g. <c>CUI//SP-PRVCY</c>,
/// <c>FOR OFFICIAL USE ONLY</c>, <c>SECRET//NOFORN</c>), or null when the page carries none or
/// detection is off (<see cref="ProcessingOptions.DetectSensitivityMarkings"/>). ADVISORY: the
/// page's content is still extracted; callers handling controlled information are responsible
/// for acting on the flag (warn, segregate, restrict downstream flow).
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
    string? Notice = null,
    int OrientationApplied = 0,
    int? EffectiveDpi = null,
    bool LowResolution = false,
    IReadOnlyList<FormField>? FormFields = null,
    bool NeedsReview = false,
    string? SensitivityMarking = null);
