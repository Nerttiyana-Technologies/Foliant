using Foliant;

namespace Foliant.Templates;

/// <summary>
/// Extracts a federal form that was matched BY IDENTITY (its printed "STANDARD FORM N" designation) rather
/// than by widget signature — i.e. a flattened or scanned form with no usable AcroForm widgets. Uses the
/// known <see cref="FormLayout"/> geometry against the rendered page + its OCR text:
///   • checkboxes  → <see cref="CheckboxPixelDetector"/> (OCR-free dark-mark detection at the known rect),
///   • text fields → the OCR lines assigned to the field's known rect (label from the template).
/// Values are OCR-derived (<see cref="FormFieldSource.Geometry"/>, confidence &lt; 1); the LABELS are exact
/// (from the reviewed template), which is what the Q&amp;A needs.
///
/// Each OCR line is assigned to AT MOST ONE text element (the rect that contains its centre; nearest centre
/// wins ties), so a line falling in two overlapping field rects is not copied into both — that double
/// assignment was the source of duplicate values across labels. Lines that merely re-print the field's own
/// label are dropped (label echo), as are junk values (lone marks, single characters).
/// </summary>
public static class ScannedFormExtractor
{
    public static IReadOnlyList<FormField> Extract(
        PageImage image, IReadOnlyList<TextLine> lines, FormLayout template, int templatePage)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(template);
        lines ??= Array.Empty<TextLine>();

        var textEls = template.Elements
            .Where(e => e.Page == templatePage && e.Kind != FormElementKind.Checkbox)
            .ToList();

        // Single global assignment: each OCR line → the one text element whose rect contains its centre
        // (nearest element centre wins when several rects overlap). No line is shared between fields.
        var assigned = new Dictionary<FormElement, List<TextLine>>();
        foreach (var line in lines)
        {
            float cx = (line.Bounds.X1 + line.Bounds.X2) / 2f / image.Width;
            float cy = (line.Bounds.Y1 + line.Bounds.Y2) / 2f / image.Height;

            FormElement? best = null;
            float bestDist = float.MaxValue;
            foreach (var el in textEls)
            {
                var r = el.Rect;
                if (cx < r.X1 || cx > r.X2 || cy < r.Y1 || cy > r.Y2) continue;
                float dx = cx - r.CenterX, dy = cy - r.CenterY;
                float dist = dx * dx + dy * dy;
                if (dist < bestDist) { bestDist = dist; best = el; }
            }
            if (best is null) continue;
            if (!assigned.TryGetValue(best, out var list)) assigned[best] = list = new List<TextLine>();
            list.Add(line);
        }

        var fields = new List<FormField>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var el in template.Elements.Where(e => e.Page == templatePage))
        {
            string label = el.Label.Length > 0 ? el.Label : "(unlabeled)";

            if (el.Kind == FormElementKind.Checkbox)
            {
                if (!CheckboxPixelDetector.IsChecked(image, el.Rect)) continue;
                if (seen.Add("cb|" + (el.Group ?? label)))
                    fields.Add(new FormField(
                        el.Group ?? label, label, FieldKind.Checkbox,
                        Confidence: 0.8f, Source: FormFieldSource.Geometry));
                continue;
            }

            if (!assigned.TryGetValue(el, out var elLines)) continue;
            var labelTokens = DistinctiveTokens(label);
            var parts = elLines
                .OrderBy(l => l.Bounds.Y1).ThenBy(l => l.Bounds.X1)
                .Select(l => l.Text)
                .Where(t => !IsLabelEcho(t, labelTokens));   // drop the printed label bleeding into the value
            string value = NormalizeWhitespace(string.Join(" ", parts));

            if (!IsMeaningful(value)) continue;              // drop lone marks / single chars / punctuation-only
            if (seen.Add("tx|" + label + "|" + value))          // drop exact label+value duplicates
                fields.Add(new FormField(
                    label, value, FieldKind.Text, Confidence: 0.8f, Source: FormFieldSource.Geometry));
        }
        return fields;
    }

    // A line is "label echo" when it carries distinctive words and ALL of them are the field's own label
    // tokens (i.e. it is the printed caption, not a filled value). Lines with no distinctive tokens
    // (digits, dates, codes) are never treated as echo, so short real values survive.
    private static bool IsLabelEcho(string? text, IReadOnlyCollection<string> labelTokens)
    {
        if (labelTokens.Count == 0) return false;
        var tokens = DistinctiveTokens(text);
        return tokens.Count > 0 && tokens.All(labelTokens.Contains);
    }

    // A value is meaningful when it has at least one letter, or two or more digits. Rejects lone "x"/marks,
    // single characters and punctuation-only fragments (the scanned-form noise) without discarding short
    // real values like "14".
    private static bool IsMeaningful(string value)
    {
        if (value.Length < 2) return false;
        if (value.Any(char.IsLetter)) return true;
        return value.Count(char.IsDigit) >= 2;
    }

    private static string NormalizeWhitespace(string s) =>
        string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();

    // Words ≥4 letters — the distinctive printed tokens (skips "OF"/"TO", numbers, punctuation).
    private static List<string> DistinctiveTokens(string? text) =>
        (text ?? string.Empty)
        .ToUpperInvariant()
        .Split(new[] { ' ', '—', '-', '/', '.', '(', ')', ',', ':', '\t' }, StringSplitOptions.RemoveEmptyEntries)
        .Where(w => w.Length >= 4 && w.Any(char.IsLetter))
        .Distinct(StringComparer.Ordinal)
        .ToList();
}
