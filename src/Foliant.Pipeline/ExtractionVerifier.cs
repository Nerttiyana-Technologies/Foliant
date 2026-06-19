// Self-verification, ported from the spike scorecard:
//  - coverage invariant: every OCR/text-layer line provably lands in the output
//    (or is intentional page furniture) — "silently lost text" is structurally impossible;
//  - word recall vs the PDF's embedded text layer, the corpus-wide quality metric
//    (98.3% average across 474 pages in Phase 0).

using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Tokens;

namespace Foliant.Pipeline;

internal static class ExtractionVerifier
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
            var page = doc.GetPage(pageNumber);
            var truth = page.GetWords()
                .Select(w => Normalize(w.Text)).Where(t => t.Length >= 3).ToList();

            // AcroForm/XFA FILLED VALUES live in the field widgets, NOT the content-stream text that
            // GetWords() returns — so without this, recall is measured against a value-less text layer
            // and a form whose values were dropped still scores 100%. Add the widget /V (text) values
            // to the truth so the metric (and Gate 1) can SEE value loss. Reads /V off the widget, then
            // its /Parent (covers both merged and separated field/widget structures; works on static
            // XFA where GetFieldsForPage returns nothing). Checkbox /V is a NameToken, not a string, so
            // TryGet<StringToken> naturally skips it.
            foreach (var value in FormFieldTextValues(page))
                foreach (var w in value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    var n = Normalize(w);
                    if (n.Length >= 3) truth.Add(n);
                }

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

    /// <summary>Filled text values of the page's form-field widgets (the values that render in the
    /// fillable boxes but are absent from the content-stream text layer). Best-effort; never throws.</summary>
    internal static IEnumerable<string> FormFieldTextValues(UglyToad.PdfPig.Content.Page page)
    {
        IEnumerable<Annotation> annots;
        try { annots = page.GetAnnotations().ToList(); }
        catch { yield break; }

        foreach (var ann in annots)
        {
            if (ann.Type != AnnotationType.Widget) continue;
            var d = ann.AnnotationDictionary;
            if (d.TryGet(NameToken.Create("V"), out StringToken v) && !string.IsNullOrWhiteSpace(v.Data))
                yield return v.Data;
            else if (d.TryGet(NameToken.Create("Parent"), out DictionaryToken p)
                     && p.TryGet(NameToken.Create("V"), out StringToken pv) && !string.IsNullOrWhiteSpace(pv.Data))
                yield return pv.Data;
        }
    }
}
