using Foliant;

namespace Foliant.Forms.Lilt;

/// <summary>
/// Learned form key-value extraction behind the <see cref="IFormFieldExtractor"/> seam (ADR-0001
/// Lever 2). Splits the page's recognized lines into words, runs the LiLT token classifier, decodes
/// KEY/VALUE spans, and pairs each VALUE span with its nearest KEY span (same line to the left, or
/// directly above — the two layouts federal forms use). Abstains rather than guesses: spans below
/// <see cref="MinConfidence"/> are dropped, and an unpaired value is emitted with an empty name only
/// when <see cref="EmitUnpairedValues"/> is set (off by default — Gate 3 zero-fabrication first).
/// Intended for flattened/scanned forms; compose AFTER AcroForm/profile extractors
/// (<c>CompositeFormFieldExtractor</c>), which win on pages where exact sources exist.
/// </summary>
public sealed class LiltFormFieldExtractor : IFormFieldExtractor
{
    private readonly LiltFormKvModel _model;

    public LiltFormFieldExtractor(LiltFormKvModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
    }

    /// <summary>Minimum span confidence; predictions below this are abstained. Default 0.65.</summary>
    public float MinConfidence { get; init; } = 0.65f;

    /// <summary>Emit VALUE spans that found no KEY (with an empty Name). Default false (abstain).</summary>
    public bool EmitUnpairedValues { get; init; }

    /// <summary>
    /// When no model KEY span pairs with a value, derive the field name from the printed words
    /// geometrically adjacent to it (same row to the left, else directly above) — the layouts federal
    /// forms use. Default true: the Gate-3 scanned-holdout diagnostic (2026-07-05) showed value
    /// tagging at 87% recall while KEY spans were too sparse to pair (143/239 values unnamed) —
    /// pairing, not tagging, was the wall.
    /// </summary>
    public bool GeometricKeyFallback { get; init; } = true;

    /// <summary>Max key→value pairing distance in page-width fractions. Default 0.35.</summary>
    public float MaxPairDistance { get; init; } = 0.35f;

    /// <summary>
    /// Drop predicted values with no real content: zero alphanumeric characters, or nothing
    /// but tick-glyph/punctuation marks (x, ×, dots, colons, brackets — what OCR reads off
    /// checkbox marks and specks). Evidence (TD-41 spurious dump, 2026-07-06, 325 rows):
    /// 119 of 325 spurious predictions were this class, emitted at up to 0.99 confidence
    /// ("×" as a Delivery/Quantity value on checkbox grids), while ZERO of the corpus's
    /// 1,161 truth values match the pattern — no measured collateral. Default on.
    /// </summary>
    public bool FilterTickValues { get; init; } = true;

    /// <summary>
    /// Set <see cref="FormField.PossiblyTruncated"/> on values whose ink runs flush into a
    /// vertical ruling (cell-border clipping in the source image — see
    /// <see cref="ValueTruncationProbe"/>). DEFAULT OFF: the probe needs ink-accurate value
    /// geometry, and none available in production measures up (Gate 3 + bench, 2026-07-06:
    /// truth rects 0.58 recall / 0.18 false-flag, but SplitWords value boxes 0.16/0.26 and
    /// det line boxes 0.27/0.25). Enable only for measurement, or once the extractor carries
    /// real word-level boxes — that box-fidelity work unlocks the 0.58/0.18 operating point.
    /// The Gate 3 TRUNCATED-SOURCE column remains the honesty mechanism meanwhile.
    /// </summary>
    public bool FlagPossiblyTruncated { get; init; }

    public IReadOnlyList<FormField> Extract(
        byte[] pdf, int pageNumber, PageImage image, IReadOnlyList<TextLine> lines)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0) return Array.Empty<FormField>();

        var (words, boxes) = SplitWords(lines);
        if (words.Count == 0) return Array.Empty<FormField>();

        var spans = _model.Predict(words, boxes, image.Width, image.Height)
            .Where(s => s.Confidence >= MinConfidence)
            .ToList();
        if (spans.Count == 0) return Array.Empty<FormField>();

        var keys = spans.Where(s => s.Kind == LiltSpanKind.Key)
            .Select(s => (Span: s, Box: Union(s, boxes), Text: Text(s, words)))
            .ToList();

        float maxDist = MaxPairDistance * image.Width;
        var valueWords = spans.Where(s => s.Kind == LiltSpanKind.Value)
            .SelectMany(s => s.WordIndices).ToHashSet();
        var fields = new List<FormField>();
        foreach (var v in spans.Where(s => s.Kind == LiltSpanKind.Value))
        {
            var vBox = Union(v, boxes);
            string value = Text(v, words);
            if (value.Length == 0) continue;
            if (FilterTickValues && IsTickOrPunctuation(value)) continue;   // checkbox mark, not a text value

            (string Text, float Confidence)? key = null;
            float best = float.MaxValue;
            foreach (var k in keys)
            {
                float d = PairDistance(k.Box, vBox);
                if (d < best && d <= maxDist) { best = d; key = (k.Text, k.Span.Confidence); }
            }

            if (key is null && GeometricKeyFallback)
            {
                string? geo = GeometricKey(vBox, words, boxes, valueWords);
                if (geo is not null) key = (geo, v.Confidence);
            }

            if (key is null && !EmitUnpairedValues) continue;   // abstain: a value we can't name is a guess
            float confidence = key is null ? v.Confidence : Math.Min(v.Confidence, key.Value.Confidence);
            // Honesty flag, never suppression: a value whose ink runs flush into a vertical
            // ruling is likely CLIPPED IN THE SOURCE (cell-border truncation at flatten/scan
            // time — ~7% of TD-41 holdout fields, class confirmed in production scans).
            // The transcription is faithful to the page; the page may not hold the full value.
            bool truncated = FlagPossiblyTruncated
                             && ValueTruncationProbe.IsFlushAgainstRuling(image, vBox);
            fields.Add(new FormField(
                key?.Text ?? string.Empty, value, FieldKind.Text, vBox, confidence,
                FormFieldSource.Learned, truncated));
        }
        return fields;
    }

    /// <summary>
    /// True when the value carries no real content: every character is punctuation, whitespace,
    /// or a tick glyph (x/X/× — what OCR reads off checkbox marks). Any other letter or digit
    /// makes the value substantive. See <see cref="FilterTickValues"/> for the evidence.
    /// </summary>
    internal static bool IsTickOrPunctuation(string value)
    {
        foreach (char c in value)
            if (char.IsLetterOrDigit(c) && c is not ('x' or 'X'))
                return false;
        return true;
    }

    /// <summary>
    /// Splits lines into word tokens with proportional x-slices of the line box — the same
    /// approximation the training rig's OCR arm uses (<c>prepare_scan_kv.py</c>), keeping the
    /// train/inference featurization aligned.
    /// </summary>
    internal static (List<string> Words, List<BoundingBox> Boxes) SplitWords(IReadOnlyList<TextLine> lines)
    {
        var words = new List<string>();
        var boxes = new List<BoundingBox>();
        foreach (var line in lines)
        {
            var parts = line.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) continue;
            float x0 = line.Bounds.X1, width = Math.Max(1f, line.Bounds.X2 - line.Bounds.X1);
            int total = parts.Sum(p => p.Length) + (parts.Length - 1);
            float cursor = x0, unit = width / Math.Max(1, total);
            foreach (var p in parts)
            {
                float w = unit * p.Length;
                words.Add(p);
                boxes.Add(new BoundingBox(cursor, line.Bounds.Y1, Math.Min(cursor + w, line.Bounds.X2), line.Bounds.Y2));
                cursor += w + unit;
            }
        }
        return (words, boxes);
    }

    /// <summary>
    /// Geometric field name for an unpaired value: the contiguous run of non-value words on the same
    /// row to the LEFT of the value (the common form layout), else the nearest run directly ABOVE.
    /// Null when nothing plausible is near — the caller then abstains (or emits unnamed if opted in).
    /// </summary>
    private static string? GeometricKey(
        BoundingBox vBox, IReadOnlyList<string> words, IReadOnlyList<BoundingBox> boxes, HashSet<int> valueWords)
    {
        float h = Math.Max(1f, vBox.Height);
        float vCy = (vBox.Y1 + vBox.Y2) / 2f;

        // candidates: label-ish words (contain a letter, not part of any predicted value)
        bool Candidate(int i) => !valueWords.Contains(i) && words[i].Any(char.IsLetter);

        // 1) same row, left of the value, nearest first
        var left = Enumerable.Range(0, words.Count)
            .Where(i => Candidate(i)
                        && Math.Abs((boxes[i].Y1 + boxes[i].Y2) / 2f - vCy) <= 0.8f * Math.Max(h, boxes[i].Height)
                        && boxes[i].X2 <= vBox.X1 + 0.25f * h)
            .OrderByDescending(i => boxes[i].X2)
            .ToList();
        var run = TakeRun(left, boxes, reach: vBox.X1, maxGap: 2f * h);
        if (run.Count == 0)
        {
            // 2) directly above: words overlapping the value's x-range, nearest row first
            var above = Enumerable.Range(0, words.Count)
                .Where(i => Candidate(i)
                            && boxes[i].Y2 <= vBox.Y1 + 0.5f * h
                            && boxes[i].X2 >= vBox.X1 - 2f * h && boxes[i].X1 <= vBox.X2 + 2f * h
                            && vBox.Y1 - boxes[i].Y2 <= 3.5f * h)
                .OrderByDescending(i => boxes[i].Y2)
                .ToList();
            if (above.Count > 0)
            {
                float rowY = boxes[above[0]].Y2;
                run = above.Where(i => rowY - boxes[i].Y2 <= 0.8f * h).OrderBy(i => boxes[i].X1).ToList();
            }
        }
        else
        {
            run.Reverse();   // collected right-to-left; read left-to-right
        }

        if (run.Count == 0) return null;
        string text = string.Join(' ', run.Take(10).Select(i => words[i])).Trim().TrimEnd(':', '.', '-');
        return text.Length > 0 ? text : null;
    }

    /// <summary>Chains same-row words right-to-left from the value edge while gaps stay small.</summary>
    private static List<int> TakeRun(List<int> ordered, IReadOnlyList<BoundingBox> boxes, float reach, float maxGap)
    {
        var run = new List<int>();
        float edge = reach;
        foreach (int i in ordered)
        {
            if (edge - boxes[i].X2 > maxGap) break;
            run.Add(i);
            edge = boxes[i].X1;
        }
        return run;
    }

    /// <summary>Key→value distance: same-line left-of (preferred) or directly-above, else unpairable.</summary>
    private static float PairDistance(BoundingBox key, BoundingBox value)
    {
        float keyCy = (key.Y1 + key.Y2) / 2f;
        bool sameLine = keyCy >= value.Y1 - (value.Y2 - value.Y1) && keyCy <= value.Y2 + (value.Y2 - value.Y1);
        if (sameLine && key.X2 <= value.X1 + (value.X2 - value.X1) * 0.25f)
            return Math.Max(0, value.X1 - key.X2);                       // left-of, gap distance

        bool above = key.Y2 <= value.Y1 + (value.Y2 - value.Y1) * 0.5f
                     && key.X2 >= value.X1 - (key.X2 - key.X1)           // horizontal overlap-ish
                     && key.X1 <= value.X2;
        if (above)
            return Math.Max(0, value.Y1 - key.Y2) + Math.Abs(key.X1 - value.X1) * 0.25f;

        return float.MaxValue;
    }

    private static BoundingBox Union(LiltSpan s, IReadOnlyList<BoundingBox> boxes)
    {
        float x1 = float.MaxValue, y1 = float.MaxValue, x2 = float.MinValue, y2 = float.MinValue;
        foreach (int wi in s.WordIndices)
        {
            var b = boxes[wi];
            x1 = Math.Min(x1, b.X1); y1 = Math.Min(y1, b.Y1);
            x2 = Math.Max(x2, b.X2); y2 = Math.Max(y2, b.Y2);
        }
        return new BoundingBox(x1, y1, x2, y2);
    }

    private static string Text(LiltSpan s, IReadOnlyList<string> words) =>
        string.Join(' ', s.WordIndices.Select(wi => words[wi])).Trim();
}
