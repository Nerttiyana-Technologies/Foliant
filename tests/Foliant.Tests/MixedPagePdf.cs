using System.Globalization;
using System.Text;

namespace Foliant.Tests;

// In-test generator for a minimal one-page PDF that embeds a single raster-image XObject covering
// a chosen fraction of the page — the "mixed page" shape from the customer samples (a scanned
// letter or table screenshot pasted into a born-digital document). Only the STRUCTURE matters:
// EmbeddedImageProbe reads the image's placed bounding box from the content stream, never the
// pixels, so the JPEG payload is a token stub. Characters come from the test's fake text layer.
// Follows the FilledFormPdf pattern: offsets computed from stream positions, Latin1 throughout.
internal static class MixedPagePdf
{
    /// <param name="coverage">Fraction (0..1) of the 100×100pt page the image should cover.</param>
    public static byte[] Build(double coverage = 0.6)
    {
        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        // Square patch anchored at the origin whose area is `coverage` of the 100×100 page.
        double side = 100.0 * Math.Sqrt(Math.Clamp(coverage, 0.0, 1.0));
        byte[] jpegStub = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0xFF, 0xD9 };

        var enc = Encoding.Latin1;
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(enc.GetBytes(s));

        byte[] content = enc.GetBytes($"q {F(side)} 0 0 {F(side)} 0 0 cm /Im0 Do Q");

        W("%PDF-1.7\n");
        var offsets = new long[5];

        offsets[0] = ms.Length;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[1] = ms.Length;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets[2] = ms.Length;
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] " +
          "/Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");

        offsets[3] = ms.Length;
        W($"4 0 obj\n<< /Type /XObject /Subtype /Image /Width 100 /Height 100 " +
          $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpegStub.Length} >>\nstream\n");
        ms.Write(jpegStub);
        W("\nendstream\nendobj\n");

        offsets[4] = ms.Length;
        W($"5 0 obj\n<< /Length {content.Length} >>\nstream\n");
        ms.Write(content);
        W("\nendstream\nendobj\n");

        long xref = ms.Length;
        var sb = new StringBuilder();
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (long off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        W(sb.ToString());
        return ms.ToArray();
    }
}
