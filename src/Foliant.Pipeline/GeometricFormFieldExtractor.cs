// Label-anchored geometric form-field extraction for FLATTENED forms (no AcroForm dictionary).
//
// Given a FormProfile (label text + field name + value location), this locates each label on the
// page's recognized text and reads the geometrically-associated value — inline (after the label on
// the same line), to the right (same row), or below. Checkboxes are read by detecting a mark glyph
// on the label's row. Pure geometry + the caller's profile: deterministic, no model, no license
// question. Quality is decided by the Gate 3 extraction scorecard, not assumed.

using System.Text.RegularExpressions;

namespace Foliant.Pipeline;

public sealed class GeometricFormFieldExtractor : IFormFieldExtractor
{
    // A standalone mark glyph (not part of a word) — the flattened checkbox indicator.
    private static readonly Regex Mark = new(@"(?<![A-Za-z0-9])[xX☒✓✗](?![A-Za-z0-9])", RegexOptions.Compiled);

    private readonly IReadOnlyList<FormProfile> _profiles;
    private readonly int _minLabelMatches;

    /// <param name="profiles">Known form families to match against; the best-matching one is used.</param>
    /// <param name="minLabelMatches">A profile must match at least this many of its labels on the page
    /// to be applied — guards against extracting from a page that isn't this form.</param>
    public GeometricFormFieldExtractor(IReadOnlyList<FormProfile> profiles, int minLabelMatches = 2)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _minLabelMatches = Math.Max(1, minLabelMatches);
    }

    public IReadOnlyList<FormField> Extract(
        byte[] pdf, int pageNumber, PageImage image, IReadOnlyList<TextLine> lines)
    {
        if (_profiles.Count == 0 || lines.Count == 0) return Array.Empty<FormField>();

        // Pick the profile whose labels best match this page.
        FormProfile? best = null;
        int bestHits = 0;
        foreach (var p in _profiles)
        {
            int hits = p.Fields.Count(f => FindLabel(lines, f.Label) is not null);
            if (hits > bestHits) { bestHits = hits; best = p; }
        }
        if (best is null || bestHits < _minLabelMatches) return Array.Empty<FormField>();

        var result = new List<FormField>();
        foreach (var spec in best.Fields)
        {
            var label = FindLabel(lines, spec.Label);
            if (label is null) continue;

            if (spec.Kind == FieldKind.Checkbox || spec.Anchor == ValueAnchor.Mark)
            {
                bool marked = HasMarkOnRow(label, lines);
                result.Add(new FormField(spec.Name, marked ? "checked" : "unchecked",
                    FieldKind.Checkbox, label.Bounds, 0.8f, FormFieldSource.Geometry));
                continue;
            }

            var (value, bounds) = FindValue(label, spec.Label, lines, spec.Anchor);
            if (string.IsNullOrWhiteSpace(value)) continue;
            result.Add(new FormField(spec.Name, value!.Trim(), FieldKind.Text, bounds, 0.8f, FormFieldSource.Geometry));
        }
        return result;
    }

    private static string Norm(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>The shortest line whose normalized text contains the normalized label (most specific).</summary>
    private static TextLine? FindLabel(IReadOnlyList<TextLine> lines, string label)
    {
        string key = Norm(label);
        if (key.Length == 0) return null;
        return lines.Where(l => Norm(l.Text).Contains(key))
                    .OrderBy(l => Norm(l.Text).Length)
                    .FirstOrDefault();
    }

    private static (string? Value, BoundingBox? Bounds) FindValue(
        TextLine label, string labelText, IReadOnlyList<TextLine> lines, ValueAnchor anchor)
    {
        // 1. Inline: value sits after the label on the same line ("SOLICITATION NO.  697DCK-…").
        string? inline = InlineValue(label.Text, labelText);
        if (inline is not null) return (inline, label.Bounds);

        // 2. Separate line to the right, then below.
        if (anchor is ValueAnchor.Right or ValueAnchor.RightThenBelow)
        {
            var r = NearestRight(label, lines);
            if (r is not null) return (r.Text, r.Bounds);
        }
        if (anchor is ValueAnchor.Below or ValueAnchor.RightThenBelow)
        {
            var b = NearestBelow(label, lines);
            if (b is not null) return (b.Text, b.Bounds);
        }
        return (null, null);
    }

    /// <summary>Text remaining on the label line after the label words, or null when none.</summary>
    private static string? InlineValue(string raw, string label)
    {
        var words = label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0)
            .ToList();
        if (words.Count == 0) return null;

        string pattern = string.Join(@"\W+", words) + @"\W*(.+)$";
        var m = Regex.Match(raw, pattern, RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        string v = m.Groups[1].Value.Trim();
        return v.Length > 0 && v.Any(char.IsLetterOrDigit) ? v : null;
    }

    private static TextLine? NearestRight(TextLine label, IReadOnlyList<TextLine> lines)
    {
        float cy = label.Bounds.CenterY, h = Math.Max(1f, label.Bounds.Height);
        return lines
            .Where(l => !ReferenceEquals(l, label)
                     && Math.Abs(l.Bounds.CenterY - cy) < 0.6f * h          // same row
                     && l.Bounds.X1 >= label.Bounds.X2 - 2f                 // to the right
                     && l.Text.Any(char.IsLetterOrDigit))
            .OrderBy(l => l.Bounds.X1)
            .FirstOrDefault();
    }

    private static TextLine? NearestBelow(TextLine label, IReadOnlyList<TextLine> lines)
    {
        float h = Math.Max(1f, label.Bounds.Height);
        return lines
            .Where(l => !ReferenceEquals(l, label)
                     && l.Bounds.Y1 >= label.Bounds.Y2 - 1f                 // below
                     && l.Bounds.Y1 - label.Bounds.Y2 < 2.5f * h            // not too far down
                     && Math.Min(l.Bounds.X2, label.Bounds.X2) - Math.Max(l.Bounds.X1, label.Bounds.X1) > 0f
                     && l.Text.Any(char.IsLetterOrDigit))
            .OrderBy(l => l.Bounds.Y1)
            .FirstOrDefault();
    }

    private static bool HasMarkOnRow(TextLine label, IReadOnlyList<TextLine> lines)
    {
        float cy = label.Bounds.CenterY, h = Math.Max(1f, label.Bounds.Height);
        return lines.Any(l => Math.Abs(l.Bounds.CenterY - cy) < 0.6f * h && Mark.IsMatch(l.Text));
    }
}
