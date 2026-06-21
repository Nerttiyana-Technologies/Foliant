using System.Security.Cryptography;
using System.Text;
using Foliant;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace Foliant.Templates;

/// <summary>
/// Builds a <see cref="FormLayout"/> from a BLANK template PDF: reads each page's widget geometry, auto-pairs
/// each widget with its nearest printed label, normalizes coordinates to 0..1, and computes a layout
/// fingerprint for matching. The output is a DRAFT — dense blocks (e.g. SF1449 27a/27b) get the wrong label
/// from geometry alone and should be hand-corrected once; the form never changes, so the template is then
/// permanent. Born-digital (AcroForm/XFA) only for now — widgets carry exact coordinates.
/// </summary>
public static class FormLayoutGenerator
{
    public static FormLayout Generate(byte[] blankPdf, string templateId, string name)
    {
        ArgumentNullException.ThrowIfNull(blankPdf);
        ArgumentException.ThrowIfNullOrWhiteSpace(templateId);

        var elements = new List<FormElement>();
        var signature = new List<string>();   // stable per-form layout signature → fingerprint

        using var doc = PdfDocument.Open(blankPdf);
        for (int pageNo = 1; pageNo <= doc.NumberOfPages; pageNo++)
        {
            var page = doc.GetPage(pageNo);
            float w = (float)page.Width, h = (float)page.Height;

            var labels = GroupWordsIntoLines(page, w, h);   // printed text as normalized lines

            foreach (var ann in page.GetAnnotations())
            {
                if (ann.Type != AnnotationType.Widget) continue;
                if (ann.Flags.HasFlag(AnnotationFlags.Hidden) || ann.Flags.HasFlag(AnnotationFlags.NoView)) continue;

                var kind = FieldKindOf(ann.AnnotationDictionary);
                if (kind is null) continue;

                var rect = Normalize(ann.Rectangle, w, h);
                if (rect.X2 <= rect.X1 || rect.Y2 <= rect.Y1) continue;

                string label = NearestLabel(rect, labels, kind.Value) ?? string.Empty;
                elements.Add(new FormElement(kind.Value, pageNo, rect, label));

                // Round to 0.1% of the page so tiny coordinate noise doesn't change the fingerprint.
                signature.Add($"{pageNo}:{kind}:{Round(rect.CenterX)}:{Round(rect.CenterY)}");
            }
        }

        DedupeOverlapping(elements);
        FillBlankLabelsFromColumnHeaders(elements);
        return new FormLayout(templateId, name, elements, Fingerprint(signature));
    }

    // ── dedupe overlapping widgets ───────────────────────────────────────────────
    // AcroForm fields can carry several widget kids at the SAME spot (e.g. signature blocks, duplicated
    // schedule cells), which would emit the same value twice. Collapse elements sharing kind+page+position
    // (rounded to 0.1% of the page), keeping the most informative label.
    private static void DedupeOverlapping(List<FormElement> elements)
    {
        var keptAt = new Dictionary<(FormElementKind, int, int, int), int>();
        var kept = new List<FormElement>();
        foreach (var e in elements)
        {
            var key = (e.Kind, e.Page, Round(e.Rect.CenterX), Round(e.Rect.CenterY));
            if (keptAt.TryGetValue(key, out int idx))
            {
                if (e.Label.Length > kept[idx].Label.Length) kept[idx] = e;   // prefer the richer label
            }
            else { keptAt[key] = kept.Count; kept.Add(e); }
        }
        elements.Clear();
        elements.AddRange(kept);
    }

    // ── table column-label inheritance ───────────────────────────────────────────
    // In a line-item schedule, only the TOP row sits under a printed header, so rows 2+ get no label.
    // Fill each BLANK text label from the nearest labeled text element directly above it in the SAME column
    // (X-band). Blanks-only → never overwrites a real label → strictly no worse than "(unlabeled)".
    private static void FillBlankLabelsFromColumnHeaders(List<FormElement> elements)
    {
        const float xTol = 0.03f;     // same column: centres within 3% of page width
        const float maxRise = 0.25f;  // don't inherit a header from across the whole page

        for (int i = 0; i < elements.Count; i++)
        {
            var e = elements[i];
            if (e.Kind != FormElementKind.Text || e.Label.Length > 0) continue;

            FormElement? header = null;
            float bestDy = float.MaxValue;
            foreach (var c in elements)
            {
                if (c.Kind != FormElementKind.Text || c.Label.Length == 0 || c.Page != e.Page) continue;
                if (MathF.Abs(c.Rect.CenterX - e.Rect.CenterX) > xTol) continue;   // same column
                float dy = e.Rect.CenterY - c.Rect.CenterY;                        // header is ABOVE → dy > 0
                if (dy <= 0 || dy > maxRise) continue;
                if (dy < bestDy) { bestDy = dy; header = c; }
            }
            if (header is not null) elements[i] = e with { Label = header.Label };
        }
    }

    // ── geometry ───────────────────────────────────────────────────────────────
    private static NormalizedRect Normalize(PdfRectangle r, float w, float h)
    {
        // PDF points (bottom-left origin) → fractions of the page, top-left origin.
        float x1 = (float)r.Left / w, x2 = (float)r.Right / w;
        float y1 = (h - (float)r.Top) / h, y2 = (h - (float)r.Bottom) / h;
        return new NormalizedRect(MathF.Min(x1, x2), MathF.Min(y1, y2), MathF.Max(x1, x2), MathF.Max(y1, y2));
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

    // ── labels (printed text → normalized lines) ─────────────────────────────────
    private static List<(string Text, NormalizedRect Rect)> GroupWordsIntoLines(
        UglyToad.PdfPig.Content.Page page, float w, float h)
    {
        var words = page.GetWords()
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .Select(x => (x.Text, Rect: Normalize(x.BoundingBox, w, h)))
            .OrderBy(x => x.Rect.CenterY).ThenBy(x => x.Rect.X1)
            .ToList();

        var lines = new List<(string Text, NormalizedRect Rect)>();
        int i = 0;
        while (i < words.Count)
        {
            // Collect one visual row (same Y band).
            var row = new List<(string Text, NormalizedRect Rect)> { words[i] };
            float cy = words[i].Rect.CenterY, band = MathF.Max(0.004f, words[i].Rect.Y2 - words[i].Rect.Y1);
            int j = i + 1;
            while (j < words.Count && MathF.Abs(words[j].Rect.CenterY - cy) < 0.6f * band) { row.Add(words[j]); j++; }
            i = j;

            // Split the row into SEPARATE labels on large X-gaps — a header row carries many distinct
            // column labels (e.g. "2. CONTRACT NUMBER" | "3. AWARD DATE"), not one phrase.
            var ordered = row.OrderBy(x => x.Rect.X1).ToList();
            var segment = new List<(string Text, NormalizedRect Rect)> { ordered[0] };
            for (int k = 1; k < ordered.Count; k++)
            {
                float gap = ordered[k].Rect.X1 - segment[^1].Rect.X2;
                float wh = MathF.Max(0.004f, ordered[k].Rect.Y2 - ordered[k].Rect.Y1);
                if (gap > 1.5f * wh) { lines.Add(Combine(segment)); segment = new(); }   // gap > a space → new label
                segment.Add(ordered[k]);
            }
            lines.Add(Combine(segment));
        }
        return lines;

        static (string Text, NormalizedRect Rect) Combine(List<(string Text, NormalizedRect Rect)> seg) =>
            (string.Join(" ", seg.Select(x => x.Text)),
             new NormalizedRect(seg.Min(x => x.Rect.X1), seg.Min(x => x.Rect.Y1),
                                seg.Max(x => x.Rect.X2), seg.Max(x => x.Rect.Y2)));
    }

    private static string? NearestLabel(NormalizedRect widget, List<(string Text, NormalizedRect Rect)> labels, FormElementKind kind)
    {
        float h = MathF.Max(0.004f, widget.Y2 - widget.Y1);

        // Checkbox option text is to the RIGHT of the box; text-field labels are to the LEFT/above.
        if (kind == FormElementKind.Checkbox)
        {
            var right = labels.Where(l => MathF.Abs(l.Rect.CenterY - widget.CenterY) < 0.7f * h && l.Rect.X1 >= widget.X2 - 0.5f * h)
                              .OrderBy(l => l.Rect.X1).FirstOrDefault();
            if (right.Text is not null) return right.Text.Trim();
        }

        var left = labels.Where(l => MathF.Abs(l.Rect.CenterY - widget.CenterY) < 0.7f * h && l.Rect.X2 <= widget.X1 + 0.5f * h)
                         .OrderByDescending(l => l.Rect.X2).FirstOrDefault();
        if (left.Text is not null) return left.Text.Trim();

        var above = labels.Where(l => l.Rect.Y2 <= widget.Y1 + 0.5f * h && widget.Y1 - l.Rect.Y2 < 2.5f * h
                                   && MathF.Min(l.Rect.X2, widget.X2) - MathF.Max(l.Rect.X1, widget.X1) > 0f)
                          .OrderByDescending(l => l.Rect.Y2).FirstOrDefault();
        return above.Text?.Trim();
    }

    // ── fingerprint ──────────────────────────────────────────────────────────────
    private static int Round(float fraction) => (int)MathF.Round(fraction * 1000f);   // 0.1% buckets

    private static string Fingerprint(List<string> signature)
    {
        signature.Sort(StringComparer.Ordinal);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", signature)));
        return Convert.ToHexStringLower(hash);
    }
}
