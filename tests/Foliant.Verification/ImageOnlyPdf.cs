using System.Globalization;
using System.Text;

namespace Foliant.Verification;

// Minimal one-page IMAGE-ONLY PDF: a single full-page JPEG XObject, no text layer, no fonts.
// This is the customer's "low-resolution scan" page class in synthetic form — the effective DPI
// seen by PdfImageScanResolutionEstimator is jpegWidth / (pageWidthPts / 72), so embedding a
// 72-DPI render of a letter page (612×792 pts → 612×792 samples) yields EffectiveDpi ≈ 72.
//
// Follows the FilledFormPdf approach (tests/Foliant.Tests): objects written sequentially, xref
// offsets computed from actual stream positions, Latin1 so string lengths equal byte offsets.
// Built in memory; nothing is committed to git.
internal static class ImageOnlyPdf
{
    public static byte[] Build(
        byte[] jpeg, int jpegWidth, int jpegHeight, double pageWidthPts, double pageHeightPts)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        var enc = Encoding.Latin1;
        using var ms = new MemoryStream();
        void W(string s) => ms.Write(enc.GetBytes(s));

        // Paint the image across the full MediaBox (the classic scanned-page structure).
        byte[] content = enc.GetBytes($"q {F(pageWidthPts)} 0 0 {F(pageHeightPts)} 0 0 cm /Im0 Do Q");

        W("%PDF-1.7\n");
        var offsets = new long[5];

        offsets[0] = ms.Length;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[1] = ms.Length;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets[2] = ms.Length;
        W($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(pageWidthPts)} {F(pageHeightPts)}] " +
          "/Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");

        offsets[3] = ms.Length;
        W($"4 0 obj\n<< /Type /XObject /Subtype /Image /Width {jpegWidth} /Height {jpegHeight} " +
          $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n");
        ms.Write(jpeg);
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
