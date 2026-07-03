// Mixed-page detection (ADR-0004 addendum, customer sample class): a born-digital page with a
// healthy text layer can still carry its REAL content as an embedded raster image — a scanned
// letter of authorization pasted into a proposal, a price table inserted as a screenshot. The
// text-layer fast path silently drops that content, and recall (scored against the same
// image-less text layer) still reports 100%. This probe answers the trigger question: how much
// of the page's area is covered by embedded raster images?

using UglyToad.PdfPig;

namespace Foliant.Pipeline;

/// <summary>
/// Fraction of a page's area covered by embedded raster images (0..1). Specks — logos, rules,
/// signature stamps under 1% of the page — are ignored; overlapping placements are not
/// deduplicated (best-effort sum, capped at 1). Never throws: a page that cannot be probed
/// reports 0 (no trigger), because the probe must never block extraction.
/// </summary>
internal static class EmbeddedImageProbe
{
    private const double MinImageFraction = 0.01;

    public static double Coverage(byte[] pdf, int pageNumber)
    {
        try
        {
            using var doc = PdfDocument.Open(pdf);
            if (pageNumber < 1 || pageNumber > doc.NumberOfPages) return 0;
            var page = doc.GetPage(pageNumber);
            double pageArea = page.Width * page.Height;
            if (pageArea <= 0) return 0;

            double total = 0;
            foreach (var image in page.GetImages())
            {
                double w = image.BoundingBox.Width, h = image.BoundingBox.Height;
                if (w <= 0 || h <= 0) continue;
                double fraction = (w * h) / pageArea;
                if (fraction >= MinImageFraction) total += fraction;
            }
            return Math.Min(1.0, total);
        }
        catch
        {
            return 0;
        }
    }
}
