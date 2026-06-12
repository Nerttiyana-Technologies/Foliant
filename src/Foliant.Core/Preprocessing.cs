namespace Foliant;

/// <summary>Result of page-image preprocessing, with a report of what was corrected.</summary>
public sealed record PreprocessedPage(
    PageImage Image,
    float SkewCorrectedDegrees,
    bool ContrastStretched,
    bool Denoised,
    bool WatermarkSuppressed = false)
{
    public bool Changed =>
        SkewCorrectedDegrees != 0f || ContrastStretched || Denoised || WatermarkSuppressed;
}

/// <summary>
/// Cleans rendered page images before OCR and layout detection on the scanned-page path
/// (deskew, contrast normalization, despeckle). Born-digital renders are already clean and
/// skip preprocessing entirely — the pipeline only invokes this when characters must come
/// from pixels.
/// </summary>
public interface IPagePreprocessor
{
    PreprocessedPage Process(PageImage page);
}
