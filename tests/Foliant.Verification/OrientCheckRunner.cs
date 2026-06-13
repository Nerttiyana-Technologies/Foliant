// Orientation check — validation for natively-rotated real scans (not synthetic). Forces the
// OCR path (so the orientation detector actually runs) on the first N pages of each PDF and
// reports the rotation the detector chose, plus word recall when the page has a text layer.
//
// Reading it: "upright (0°)" means the detector left the page alone; "rotated N° → corrected"
// means it found the page needed an N° clockwise correction and applied it. For a genuinely
// sideways/upside-down scan you want a non-zero angle here AND, if the page carries a text
// layer, recall that lands near where an upright copy would.

using Foliant;
using Foliant.Pipeline;

namespace Foliant.Verification;

internal static class OrientCheckRunner
{
    public static async Task<bool> RunAsync(DocumentProcessor processor, string pdfDir, int pages)
    {
        var pdfs = Directory.GetFiles(pdfDir, "*.pdf").OrderBy(p => p).ToList();
        if (pdfs.Count == 0) { Console.Error.WriteLine($"orient-check: no PDFs in {pdfDir}"); return true; }

        Console.WriteLine($"\n════ ORIENTATION CHECK — first {pages} page(s)/PDF, detection ON, forced OCR ════");

        var opts = new ProcessingOptions
        {
            TextLayer = TextLayerMode.Never,   // force the OCR path so orientation detection runs
            DetectOrientation = true,
            Verify = true,
            Pages = Enumerable.Range(1, pages).ToArray(),
        };

        int rotatedPages = 0, totalPages = 0;
        foreach (var pdf in pdfs)
        {
            var name = Path.GetFileName(pdf);
            DocumentResult doc;
            try { doc = await processor.ProcessAsync(await File.ReadAllBytesAsync(pdf), opts); }
            catch (Exception ex) { Console.WriteLine($"  {name}: ERROR {ex.Message}"); continue; }

            foreach (var pg in doc.Pages)
            {
                totalPages++;
                if (pg.OrientationApplied != 0) rotatedPages++;
                string rec = pg.Verification.RecallPercent is { } r ? $"{r:0.0}%" : "n/a (no text layer)";
                string det = pg.OrientationApplied == 0
                    ? "upright (0°)"
                    : $"rotated {pg.OrientationApplied}° → corrected";
                Console.WriteLine($"  {name} p{pg.PageNumber:D2}: {det,-28} recall {rec}");
            }
        }

        Console.WriteLine($"\norient-check: {rotatedPages}/{totalPages} page(s) detected as rotated and corrected.");
        Console.WriteLine("(informational — does not fail the build)");
        return true;
    }
}
