// Effective scan-resolution estimate from a page's embedded raster images.
//
// The render DPI (ProcessingOptions.Dpi) is a fixed rasterization target and says nothing about
// scan quality: a 120-DPI scan rendered at 300 DPI is upsampled mush. The signal that actually
// governs OCR legibility is the *native* pixel size of the page's scan image relative to its
// physical size on the page — its effective DPI. PdfPig exposes each image's sample dimensions
// and its placement rectangle in points (72/inch), which is all that is needed.

using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Foliant.Pipeline;

public sealed class PdfImageScanResolutionEstimator : IScanResolutionEstimator
{
    /// <summary>
    /// An embedded image must cover at least this fraction of the page area to be treated as the
    /// "scan" whose resolution defines the page. Below it, the image is decoration (a logo, a
    /// signature stamp, a header rule) and a page DPI computed from it would be meaningless.
    /// </summary>
    private const double MinPageCoverage = 0.5;

    public int? EstimateEffectiveDpi(byte[] pdf, int pageNumber)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);

        using var doc = PdfDocument.Open(pdf);
        if (pageNumber > doc.NumberOfPages) return null;
        return EstimateEffectiveDpi(doc.GetPage(pageNumber));
    }

    /// <summary>
    /// Effective DPI of the dominant page-covering image, or null when no image covers enough of
    /// the page to be a scan. Uses the limiting (smaller) of the horizontal/vertical DPI, since
    /// the worse axis governs legibility; picks the image with the greatest page coverage when
    /// several qualify (a full-page scan dwarfs any inset figure).
    /// </summary>
    internal static int? EstimateEffectiveDpi(Page page)
    {
        double pageArea = page.Width * page.Height;
        if (pageArea <= 0) return null;

        int? best = null;
        double bestCoverage = MinPageCoverage;

        foreach (IPdfImage image in page.GetImages())
        {
            double placedWidth = image.BoundingBox.Width;
            double placedHeight = image.BoundingBox.Height;
            if (placedWidth <= 0 || placedHeight <= 0) continue;
            if (image.WidthInSamples <= 0 || image.HeightInSamples <= 0) continue;

            double coverage = (placedWidth * placedHeight) / pageArea;
            if (coverage < bestCoverage) continue;

            // points → inches is /72; samples / inches = DPI.
            double dpiX = image.WidthInSamples / (placedWidth / 72.0);
            double dpiY = image.HeightInSamples / (placedHeight / 72.0);

            best = (int)Math.Round(Math.Min(dpiX, dpiY));
            bestCoverage = coverage;
        }

        return best;
    }
}
