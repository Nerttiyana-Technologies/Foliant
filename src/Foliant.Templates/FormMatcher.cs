using Foliant;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Tokens;

namespace Foliant.Templates;

/// <summary>A page matched to a registered template: which template, which of its pages, and the similarity.</summary>
public sealed record TemplateMatch(FormLayout Template, int TemplatePage, double Score);

/// <summary>
/// Matches ONE uploaded page against registered templates by comparing widget-layout signatures (kind +
/// normalized centre of each widget). PER-PAGE by design: a package (SF1449 page 1 + scanned page 10) routes
/// each page independently. CONSERVATIVE by design: only returns a match when similarity clears a high
/// threshold — a miss falls back to default processing (safe), a FALSE match would apply the wrong template's
/// coordinates (the dangerous failure), so the bar is set to avoid it. Born-digital only (uses widgets);
/// a page with no widgets (a scan) returns null → fallback.
/// </summary>
public static class FormMatcher
{
    public const double DefaultThreshold = 0.85;

    public static TemplateMatch? MatchPage(
        byte[] uploadPdf, int pageNumber, IReadOnlyList<FormLayout> templates, double threshold = DefaultThreshold)
    {
        ArgumentNullException.ThrowIfNull(uploadPdf);
        ArgumentNullException.ThrowIfNull(templates);

        var uploadSig = UploadPageSignature(uploadPdf, pageNumber);
        if (uploadSig.Count == 0) return null;   // no widgets → can't fingerprint → fall back

        TemplateMatch? best = null;
        foreach (var t in templates)
            foreach (int page in t.Elements.Select(e => e.Page).Distinct())
            {
                var templateSig = t.Elements.Where(e => e.Page == page)
                    .Select(e => Token(e.Kind, e.Rect.CenterX, e.Rect.CenterY)).ToHashSet();
                if (templateSig.Count == 0) continue;

                double score = Jaccard(uploadSig, templateSig);
                if (score >= threshold && (best is null || score > best.Score))
                    best = new TemplateMatch(t, page, score);
            }
        return best;
    }

    private static HashSet<string> UploadPageSignature(byte[] pdf, int pageNumber)
    {
        var sig = new HashSet<string>();
        try
        {
            using var doc = PdfDocument.Open(pdf);
            if (pageNumber < 1 || pageNumber > doc.NumberOfPages) return sig;
            var page = doc.GetPage(pageNumber);
            float w = (float)page.Width, h = (float)page.Height;

            foreach (var ann in page.GetAnnotations())
            {
                if (ann.Type != AnnotationType.Widget) continue;
                if (ann.Flags.HasFlag(AnnotationFlags.Hidden) || ann.Flags.HasFlag(AnnotationFlags.NoView)) continue;
                var kind = FieldKindOf(ann.AnnotationDictionary);
                if (kind is null) continue;

                var r = ann.Rectangle;
                float cx = ((float)r.Left + (float)r.Right) / 2f / w;
                float cy = (h - ((float)r.Top + (float)r.Bottom) / 2f) / h;
                sig.Add(Token(kind.Value, cx, cy));
            }
        }
        catch { /* unreadable/encrypted → no signature → fall back */ }
        return sig;
    }

    // Same token + rounding the generator uses (kind + centre rounded to 0.1% of the page).
    private static string Token(FormElementKind kind, float cx, float cy) =>
        $"{kind}:{(int)MathF.Round(cx * 1000f)}:{(int)MathF.Round(cy * 1000f)}";

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        int intersection = a.Count(b.Contains);
        int union = a.Count + b.Count - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static FormElementKind? FieldKindOf(DictionaryToken d)
    {
        if (TryFt(d, out var ft) || (d.TryGet(NameToken.Create("Parent"), out DictionaryToken p) && TryFt(p, out ft)))
            return ft switch { "Tx" => FormElementKind.Text, "Ch" => FormElementKind.Text,
                               "Btn" => FormElementKind.Checkbox, _ => null };
        return null;

        static bool TryFt(DictionaryToken dict, out string value)
        {
            value = string.Empty;
            if (dict.TryGet(NameToken.Create("FT"), out NameToken ftTok)) { value = ftTok.Data; return true; }
            return false;
        }
    }
}
