// PDF page rasterization via PDFtoImage (bundled pdfium, cross-platform).
// PdfPig handles page counting — it parses PDF structure without native deps.

using Foliant.Internal;
using PDFtoImage;

namespace Foliant.Pipeline;

public sealed class PdfPageRenderer : IPageRenderer
{
    public int GetPageCount(byte[] pdf)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return doc.NumberOfPages;
    }

    public PageImage Render(byte[] pdf, int pageNumber, int dpi)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);
        using var stream = new MemoryStream(pdf, writable: false);
        // CA1416: PDFtoImage supports Windows/Linux/macOS (+ mobile) — exactly Foliant's
        // supported platforms; pdfium native assets ship for all of them.
#pragma warning disable CA1416
        using var bitmap = Conversion.ToImage(
            stream, page: (Index)(pageNumber - 1), options: new RenderOptions(Dpi: dpi));
#pragma warning restore CA1416
        return SkiaInterop.ToPageImage(bitmap, dpi);
    }
}
