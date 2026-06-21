using Foliant;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Tokens;

namespace Foliant.Templates;

/// <summary>
/// Extracts a matched page's fields using its template's KNOWN element positions + labels. For each template
/// element it reads the upload widget at that position — text from <c>/V</c>, checkbox state from <c>/AS</c> —
/// and emits a <see cref="FormField"/> whose Name comes from the template (the semantic label), not from
/// runtime geometry. This is what makes dense blocks (SF1449 27a/27b) correct and deterministic: the meaning
/// of every position is already known, so a checked box is bound to its real option with zero guessing.
/// </summary>
public static class TemplateExtractor
{
    private sealed record UploadWidget(NormalizedRect Rect, string? Text, bool Checked);

    public static IReadOnlyList<FormField> Extract(
        byte[] uploadPdf, int pageNumber, FormLayout template, int templatePage)
    {
        ArgumentNullException.ThrowIfNull(uploadPdf);
        ArgumentNullException.ThrowIfNull(template);

        var widgets = ReadWidgets(uploadPdf, pageNumber);
        if (widgets.Count == 0) return Array.Empty<FormField>();

        // 1:1 assignment: each upload widget feeds AT MOST ONE template element. Schedule rows sit ~0.017
        // apart, so a generous overlap pad lets one widget overlap two adjacent rows — without consuming, that
        // widget's value would be emitted twice. Assign each element its NEAREST unclaimed overlapping widget.
        var claimed = new bool[widgets.Count];
        var fields = new List<FormField>();
        foreach (var el in template.Elements.Where(e => e.Page == templatePage))
        {
            int best = -1; float bestDist = float.MaxValue;
            for (int wi = 0; wi < widgets.Count; wi++)
            {
                if (claimed[wi]) continue;
                var wd = widgets[wi];
                if (!(el.Rect.ContainsCenterOf(wd.Rect, pad: 0.01f) || wd.Rect.ContainsCenterOf(el.Rect, pad: 0.01f)))
                    continue;
                float dx = el.Rect.CenterX - wd.Rect.CenterX, dy = el.Rect.CenterY - wd.Rect.CenterY;
                float dist = dx * dx + dy * dy;
                if (dist < bestDist) { bestDist = dist; best = wi; }
            }
            if (best < 0) continue;
            claimed[best] = true;
            var widget = widgets[best];

            if (el.Kind == FormElementKind.Text)
            {
                if (!string.IsNullOrWhiteSpace(widget.Text))
                    fields.Add(new FormField(
                        el.Label.Length > 0 ? el.Label : "(unlabeled)", widget.Text!.Trim(),
                        FieldKind.Text, Confidence: 1f, Source: FormFieldSource.AcroForm));
            }
            else if (widget.Checked)   // only emit checked boxes; the template label IS the selected option
            {
                fields.Add(new FormField(
                    el.Group ?? (el.Label.Length > 0 ? el.Label : "(unlabeled)"),
                    el.Label.Length > 0 ? el.Label : "checked",
                    FieldKind.Checkbox, Confidence: 1f, Source: FormFieldSource.AcroForm));
            }
        }
        return fields;
    }

    private static List<UploadWidget> ReadWidgets(byte[] pdf, int pageNumber)
    {
        var result = new List<UploadWidget>();
        try
        {
            using var doc = PdfDocument.Open(pdf);
            if (pageNumber < 1 || pageNumber > doc.NumberOfPages) return result;
            var page = doc.GetPage(pageNumber);
            float w = (float)page.Width, h = (float)page.Height;

            foreach (var ann in page.GetAnnotations())
            {
                if (ann.Type != AnnotationType.Widget) continue;
                if (ann.Flags.HasFlag(AnnotationFlags.Hidden) || ann.Flags.HasFlag(AnnotationFlags.NoView)) continue;

                var d = ann.AnnotationDictionary;
                string? text = ReadText(d);
                bool isChecked = !string.IsNullOrEmpty(ReadOnState(d));

                var r = ann.Rectangle;
                float x1 = (float)r.Left / w, x2 = (float)r.Right / w;
                float y1 = (h - (float)r.Top) / h, y2 = (h - (float)r.Bottom) / h;
                var rect = new NormalizedRect(MathF.Min(x1, x2), MathF.Min(y1, y2), MathF.Max(x1, x2), MathF.Max(y1, y2));
                if (rect.X2 <= rect.X1 || rect.Y2 <= rect.Y1) continue;

                result.Add(new UploadWidget(rect, text, isChecked));
            }
        }
        catch { /* best-effort */ }
        return result;
    }

    private static string? ReadText(DictionaryToken d)
    {
        if (d.TryGet(NameToken.Create("V"), out StringToken v) && !string.IsNullOrWhiteSpace(v.Data)) return v.Data;
        if (d.TryGet(NameToken.Create("Parent"), out DictionaryToken p)
            && p.TryGet(NameToken.Create("V"), out StringToken pv) && !string.IsNullOrWhiteSpace(pv.Data)) return pv.Data;
        return null;
    }

    private static string? ReadOnState(DictionaryToken d) =>
        d.TryGet(NameToken.Create("AS"), out NameToken asTok) && asTok.Data is not "Off" ? asTok.Data : null;
}
