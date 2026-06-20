using System.Text;

namespace Foliant.Tests;

// In-test generator for a minimal, valid one-page AcroForm PDF that carries a single FILLED text
// widget. Follows the SyntheticFormFiller approach (drive off widget annotations: /T name, /FT Tx,
// /Rect, and a SyntheticFormFiller-style synthetic value) — but where that harness draws the value
// onto a raster, this writes it into the widget's /V so the AcroForm value-recovery path
// (DocumentProcessor.AcroFormValueLines) can be exercised directly. Nothing is committed to git: the
// PDF is built in memory at test time, deterministic and offline.
//
// Geometry: /Rect and /MediaBox are PDF points (bottom-left origin). The default 100x100 MediaBox is
// chosen so that, rendered at 72 dpi (scale = dpi/72 = 1), the recovered value line lands inside a
// 100x100 raster — matching the unit tests' FakeRenderer/FakeLayout region for the end-to-end path.
internal static class FilledFormPdf
{
    // A SF-1449-flavoured filled field, in the spirit of SyntheticFormFiller's value pools.
    public const string DefaultFieldName = "solicitation_number";
    public const string DefaultValue = "ABC123-25-R-00001";

    public static byte[] Build(
        string fieldName = DefaultFieldName,
        string value = DefaultValue,
        double rectLeft = 10, double rectBottom = 70, double rectRight = 90, double rectTop = 90,
        int mediaBoxW = 100, int mediaBoxH = 100)
    {
        // Objects, in order. Offsets are computed from actual stream positions (never hand-counted),
        // so the body text can change freely without breaking the xref.
        string[] objects =
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] /DA (/Helv 0 Tf 0 g) >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {mediaBoxW} {mediaBoxH}] /Resources << >> /Annots [4 0 R] >>",
            // /F 4 = Print (NOT Hidden/NoView), so AcroFormValueLines recovers the value.
            $"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({EscapeLiteral(fieldName)}) " +
            $"/V ({EscapeLiteral(value)}) /Rect [{F(rectLeft)} {F(rectBottom)} {F(rectRight)} {F(rectTop)}] /F 4 >>",
        };

        // Latin1 keeps one char == one byte, so string lengths equal byte offsets.
        var enc = Encoding.Latin1;
        using var ms = new MemoryStream();
        void Write(string s) => ms.Write(enc.GetBytes(s));

        Write("%PDF-1.7\n");

        var offsets = new long[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = ms.Length;
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        long xref = ms.Length;
        var sb = new StringBuilder();
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        foreach (var off in offsets)
            sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        Write(sb.ToString());

        return ms.ToArray();
    }

    // PDF literal-string escaping for the value/name (parentheses and backslash).
    private static string EscapeLiteral(string s) =>
        s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    // Invariant numeric formatting (no thousands separators, no locale decimal comma).
    private static string F(double d) => d.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
}
