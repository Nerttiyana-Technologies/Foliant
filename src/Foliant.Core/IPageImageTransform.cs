namespace Foliant;

/// <summary>
/// A pure transform over a rendered <see cref="PageImage"/>, applied by the pipeline to the
/// rasterized page <em>before</em> text extraction, layout detection and OCR (see
/// <see cref="ProcessingOptions.ImageTransform"/>).
///
/// Two intended uses:
/// <list type="bullet">
///   <item>External preprocessing a caller wants in the pipeline (e.g. their own
///   deskew/denoise) without forking the processor.</item>
///   <item>Deterministic robustness testing — synthetic degradations (rotation, JPEG,
///   noise, blur, low-DPI, contrast fade) injected to measure OCR recall under scan-like
///   conditions. See <c>ScanDegrader</c> in <c>Foliant.Pipeline</c> and the verification
///   harness's Gate 7.</item>
/// </list>
///
/// Implementations must be side-effect free and return a new <see cref="PageImage"/> (or the
/// input unchanged); the same instance may be reused across pages and threads.
/// </summary>
public interface IPageImageTransform
{
    /// <summary>Returns a transformed copy of <paramref name="image"/> (or the input unchanged).</summary>
    PageImage Transform(PageImage image);
}
