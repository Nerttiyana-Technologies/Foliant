// Widget-anchored geometric form-field extraction — the no-profile, no-model path for born-digital
// forms (plain AcroForm AND static-XFA). Reads each visible widget's filled value straight off the page
// annotation (/V, like the 1.1.1 AcroFormValueLines, so it works where doc.TryGetForm/GetFieldsForPage
// returns nothing on XFA), then pairs each value with its nearest PRINTED label from the page text —
// because on these forms the widgets carry no meaningful /T name (e.g. SF1449 has only "Page 1"/"Page 2").
//
// This is the deterministic counterpart to the LiLT form-K-V model: exact values from the document,
// labels from geometry. No FormProfile needed (unlike GeometricFormFieldExtractor), so it generalizes
// across form families without a hand-built profile per form.

using UglyToad.PdfPig;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace Foliant.Pipeline;

public sealed class WidgetFormFieldExtractor : IFormFieldExtractor
{
    public IReadOnlyList<FormField> Extract(
        byte[] pdf, int pageNumber, PageImage image, IReadOnlyList<TextLine> lines)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(image);
        var result = new List<FormField>();
        try
        {
            using var doc = PdfDocument.Open(pdf);
            if (pageNumber < 1 || pageNumber > doc.NumberOfPages) return result;
            var page = doc.GetPage(pageNumber);
            float scale = image.Dpi / 72f, pageH = (float)page.Height;

            // 1) Filled widgets → (value, kind, raster box). Same /V read + transform as AcroFormValueLines.
            var widgets = new List<(string Value, FieldKind Kind, BoundingBox Box)>();
            foreach (var ann in page.GetAnnotations())
            {
                if (ann.Type != AnnotationType.Widget) continue;
                if (ann.Flags.HasFlag(AnnotationFlags.Hidden) || ann.Flags.HasFlag(AnnotationFlags.NoView)) continue;

                var (value, kind) = ReadValue(ann.AnnotationDictionary);
                if (value is null) continue;
                var box = ToBox(ann.Rectangle, scale, pageH);
                if (box.Width <= 0 || box.Height <= 0) continue;
                widgets.Add((value, kind, box));
            }
            if (widgets.Count == 0) return result;

            // 2) Label candidates = page text lines that are NOT a filled value themselves (a value line
            //    sits inside its widget box; excluding those keeps us pairing against printed labels).
            var valueRects = widgets.Select(w => w.Box).ToList();
            var labels = lines
                .Where(l => l.Text.Any(char.IsLetter) && !valueRects.Any(r => r.ContainsCenterOf(l.Bounds)))
                .ToList();

            // 3) Pair each value with its nearest printed label (left on the same row, else above).
            foreach (var (value, kind, box) in widgets)
                result.Add(new FormField(
                    NearestLabel(box, labels, kind) ?? string.Empty, value, kind, box, 0.9f, FormFieldSource.Geometry));
        }
        catch { /* best-effort: never block extraction on form-field recovery */ }
        return result;
    }

    private static (string? Value, FieldKind Kind) ReadValue(DictionaryToken widget)
    {
        // Text field: /V string on the widget or its parent.
        if (TryText(widget, out var t)) return (t, FieldKind.Text);
        if (widget.TryGet(NameToken.Create("Parent"), out DictionaryToken parent) && TryText(parent, out var pt))
            return (pt, FieldKind.Text);

        // Checkbox/radio: the selection lives in the WIDGET's appearance state /AS ("Off" = unchecked).
        // Manual fills (and many tools) set /AS rather than a widget /V — so /AS is the reliable signal.
        // (PdfPig's NameToken.Data carries no leading slash, e.g. "Off", "1", "Yes".)
        if (widget.TryGet(NameToken.Create("AS"), out NameToken asTok) && asTok.Data is not "Off")
            return ("checked", FieldKind.Checkbox);

        return (null, FieldKind.Text);
    }

    private static bool TryText(DictionaryToken d, out string value)
    {
        value = string.Empty;
        if (d.TryGet(NameToken.Create("V"), out StringToken v) && !string.IsNullOrWhiteSpace(v.Data))
        {
            value = v.Data.Trim();
            return true;
        }
        return false;
    }

    // PDF points (bottom-left origin) → raster pixels (top-left origin), the transform the text-layer
    // reader uses, so widget boxes are comparable to the page's text-line boxes.
    private static BoundingBox ToBox(PdfRectangle r, float scale, float pageH)
    {
        float xA = (float)r.Left * scale, xB = (float)r.Right * scale;
        float yA = (pageH - (float)r.Top) * scale, yB = (pageH - (float)r.Bottom) * scale;
        return new BoundingBox(MathF.Min(xA, xB), MathF.Min(yA, yB), MathF.Max(xA, xB), MathF.Max(yA, yB));
    }

    // Nearest printed label: the closest text on the SAME ROW to the LEFT (the common form layout),
    // else the closest line directly ABOVE. Returns null when nothing plausible is near.
    private static string? NearestLabel(BoundingBox widget, IReadOnlyList<TextLine> labels, FieldKind kind)
    {
        float h = MathF.Max(1f, widget.Height);

        // Checkbox option text sits to the RIGHT of the box ("☐ WOMEN-OWNED SMALL BUSINESS"), unlike a
        // text field whose label is to the left/above. Prefer the right neighbour for checkboxes.
        if (kind == FieldKind.Checkbox)
        {
            var right = labels
                .Where(l => MathF.Abs(l.Bounds.CenterY - widget.CenterY) < 0.7f * h
                         && l.Bounds.X1 >= widget.X2 - 0.5f * h)
                .OrderBy(l => l.Bounds.X1)
                .FirstOrDefault();
            if (right is not null) return right.Text.Trim();
        }

        var left = labels
            .Where(l => MathF.Abs(l.Bounds.CenterY - widget.CenterY) < 0.7f * h     // same row
                     && l.Bounds.X2 <= widget.X1 + 0.5f * h)                         // to the left
            .OrderByDescending(l => l.Bounds.X2)                                     // closest to the widget
            .FirstOrDefault();
        if (left is not null) return left.Text.Trim();

        var above = labels
            .Where(l => l.Bounds.Y2 <= widget.Y1 + 0.5f * h                          // above
                     && widget.Y1 - l.Bounds.Y2 < 2.5f * h                           // not too far up
                     && MathF.Min(l.Bounds.X2, widget.X2) - MathF.Max(l.Bounds.X1, widget.X1) > 0f) // x-overlap
            .OrderByDescending(l => l.Bounds.Y2)                                     // closest above
            .FirstOrDefault();
        return above?.Text.Trim();
    }
}
