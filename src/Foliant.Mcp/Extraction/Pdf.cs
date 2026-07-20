namespace Foliant.Mcp.Extraction;

/// <summary>
/// Cheap PDF facts via PdfPig (a public transitive dependency of Foliant.Pipeline) — the same way
/// Foliant.Templates' TemplateRouter counts pages. Loads no models, renders nothing.
/// </summary>
internal static class Pdf
{
    public static int GetPageCount(byte[] pdf)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        return doc.NumberOfPages;
    }
}
