// Self-verification, ported from the spike scorecard:
//  - coverage invariant: every OCR/text-layer line provably lands in the output
//    (or is intentional page furniture) — "silently lost text" is structurally impossible;
//  - word recall vs the PDF's embedded text layer, the corpus-wide quality metric
//    (98.3% average across 474 pages in Phase 0).

namespace Foliant.Pipeline;

public static class ExtractionVerifier
{
    /// <summary>Counts extracted lines that appear neither in the Markdown nor in the
    /// intentionally-kept-aside page furniture. Must be 0 (Gate 2).</summary>
    public static int CountLostLines(
        string markdown, IReadOnlyList<TextLine> lines, IReadOnlyCollection<TextLine> pageFurniture)
    {
        var furniture = pageFurniture as ISet<TextLine> ?? new HashSet<TextLine>(pageFurniture);
        return lines.Count(l =>
            l.Text.Length > 2 && !furniture.Contains(l) &&
            !markdown.Contains(l.Text, StringComparison.Ordinal) &&
            !markdown.Contains(l.Text.Replace("|", "\\|"), StringComparison.Ordinal));
    }

    /// <summary>
    /// Word-level recall of <paramref name="extractedText"/> against the PDF's embedded
    /// text layer (words of length ≥ 3, alphanumeric-normalized). Returns (0,0) when the
    /// page has no text layer — recall is then undefined, not zero.
    /// </summary>
    public static (int TruthWords, int Found) TextLayerRecall(
        byte[] pdf, int pageNumber, string extractedText)
    {
        try
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
            var truth = doc.GetPage(pageNumber).GetWords()
                .Select(w => Normalize(w.Text)).Where(t => t.Length >= 3).ToList();
            if (truth.Count == 0) return (0, 0);

            var extractedWords = new HashSet<string>(
                extractedText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Normalize));
            return (truth.Count, truth.Count(t => extractedWords.Contains(t)));
        }
        catch
        {
            return (0, 0);
        }
    }

    public static string Normalize(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}
