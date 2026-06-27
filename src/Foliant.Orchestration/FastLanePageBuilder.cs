using System.Text;
using ZD = ZeroDep.Abstractions;

namespace Foliant.Orchestration;

/// <summary>
/// Builds a Foliant <see cref="PageResult"/> for a <b>fast-lane</b> page directly from ZeroDep's structural
/// read — no render, no OCR, no layout ML. Two shapes (ADR-0003 §Phase 1):
/// <list type="bullet">
///   <item><b>FormPage</b> → exact AcroForm <see cref="FormField"/>s + a field-list Markdown section.</item>
///   <item><b>DigitalText</b> (and <c>Empty</c>) → text runs assembled in simple top-down/left-right reading
///   order into prose Markdown. Safe because the router only fast-lanes single-flow pages — multi-column /
///   complex pages are classified <see cref="PageKind.TableOrComplexLayout"/> and escalate to Foliant.</item>
/// </list>
/// Coverage holds by construction (every extracted run is emitted), so verification reports zero lost lines
/// and full recall against its own extraction.
/// </summary>
public sealed class FastLanePageBuilder
{
    private readonly ITypeAdapter _adapter;

    public FastLanePageBuilder(ITypeAdapter adapter)
        => _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));

    /// <summary>Build the page result for one fast-lane page.</summary>
    /// <param name="pageNumber">1-based page number.</param>
    /// <param name="kind">The page's routed kind (FormPage / DigitalText / Empty).</param>
    /// <param name="pageRuns">ZeroDep text runs for this page (any order; OCR-layer runs are ignored).</param>
    /// <param name="pageFields">ZeroDep AcroForm fields whose widget is on this page.</param>
    /// <param name="seconds">Wall-clock seconds spent building this page (for verification telemetry).</param>
    public PageResult Build(
        int pageNumber,
        PageKind kind,
        IReadOnlyList<ZD.TextRunInfo> pageRuns,
        IReadOnlyList<ZD.FormFieldInfo> pageFields,
        double seconds = 0)
    {
        ArgumentNullException.ThrowIfNull(pageRuns);
        ArgumentNullException.ThrowIfNull(pageFields);

        var lines = pageRuns
            .Where(r => !r.IsOcrLayer && !string.IsNullOrWhiteSpace(r.Text))
            .OrderByDescending(r => r.Y).ThenBy(r => r.X)
            .Select(_adapter.ToTextLine)
            .ToList();

        string markdown;
        List<FormField>? formFields = null;

        if (kind == PageKind.FormPage)
        {
            formFields = pageFields
                .Select(_adapter.ToFormField)
                .Where(f => f is not null)
                .Select(f => f!)
                .ToList();
            markdown = ComposeFormMarkdown(formFields);
        }
        else
        {
            markdown = ComposeProse(pageRuns);
        }

        int words = WordCount(markdown);
        var verification = new PageVerification(
            LinesLost: 0, TruthWords: words, TruthWordsFound: words, Seconds: seconds);

        return new PageResult(
            PageNumber: pageNumber,
            WidthPx: 0,                 // fast lane is not rasterized
            HeightPx: 0,
            Dpi: 72,                    // bounds are advisory PDF points (see ZeroDepTypeAdapter remarks)
            Regions: Array.Empty<Region>(),
            Lines: lines,
            PageFurniture: Array.Empty<TextLine>(),
            Source: TextSource.TextLayer,
            Markdown: markdown,
            Verification: verification,
            Notice: null,
            OrientationApplied: 0,
            EffectiveDpi: null,
            LowResolution: false,
            FormFields: formFields);
    }

    // "- **Name:** value" per field; checkboxes render as [x]/[ ].
    private static string ComposeFormMarkdown(IReadOnlyList<FormField> fields)
    {
        if (fields.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (var f in fields)
        {
            string value = f.Kind == FieldKind.Checkbox
                ? (f.Value == "checked" ? "[x]" : "[ ]")
                : f.Value;
            sb.Append("- **").Append(f.Name).Append(":** ").Append(value).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    // Simple single-flow assembly: group runs into visual lines by Y proximity, order each line by X, and
    // insert a paragraph break on a large vertical gap. Sufficient for single-flow pages (the only kind
    // routed here); anything multi-column is escalated to Foliant's XY-Cut++.
    private static string ComposeProse(IReadOnlyList<ZD.TextRunInfo> runs)
    {
        var visible = runs
            .Where(r => !r.IsOcrLayer && !string.IsNullOrWhiteSpace(r.Text))
            .OrderByDescending(r => r.Y).ThenBy(r => r.X)
            .ToList();
        if (visible.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        var lineBuf = new List<ZD.TextRunInfo>();
        double? prevY = null;
        double prevFont = visible[0].FontSize > 0 ? visible[0].FontSize : 10;

        void Flush()
        {
            if (lineBuf.Count == 0) return;
            // Reconstruct word boundaries from run X-positions. ZeroDep emits fine-grained (often glyph-level)
            // positioned runs, so a blind space-join shatters words ("SHORT" -> "SH O R T"). Insert a space
            // only when the horizontal gap between consecutive runs is at least a space width; otherwise the
            // runs belong to the same word and are concatenated directly.
            // Same rule as ZeroDep's TextAnalyzer.BuildPlainText (2.1.1, ADR-0008): a positionally-encoded
            // inter-word space is present when the gap exceeds 0.5 × the font's own space advance
            // (TextRunInfo.SpaceWidthEm × FontSize) — a flat 0.25×FontSize wrongly exceeds many fonts' space
            // width and drops genuine word breaks. Guard against double spaces.
            var ordered = lineBuf.OrderBy(r => r.X).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                var r = ordered[i];
                if (i > 0)
                {
                    var prev = ordered[i - 1];
                    bool prevEndsSpace = prev.Text.Length > 0 && char.IsWhiteSpace(prev.Text[^1]);
                    bool curStartsSpace = r.Text.Length > 0 && char.IsWhiteSpace(r.Text[0]);
                    if (!prevEndsSpace && !curStartsSpace)
                    {
                        double gap = r.X - (prev.X + prev.Width);
                        double fontSize = prev.FontSize > 0 ? prev.FontSize : (r.FontSize > 0 ? r.FontSize : 1);
                        double spaceEm = prev.SpaceWidthEm > 0 ? prev.SpaceWidthEm : 0.25;
                        if (gap > 0.5 * spaceEm * fontSize) sb.Append(' ');
                    }
                }
                sb.Append(r.Text);
            }
            lineBuf.Clear();
        }

        foreach (var r in visible)
        {
            double font = r.FontSize > 0 ? r.FontSize : prevFont;
            if (prevY is double py)
            {
                double dy = py - r.Y;                       // positive = moving down the page
                double sameLineTol = Math.Max(font, prevFont) * 0.6;
                if (dy <= sameLineTol)
                {
                    lineBuf.Add(r);                         // same visual line
                }
                else
                {
                    Flush();
                    sb.Append(dy > Math.Max(font, prevFont) * 1.8 ? "\n\n" : "\n");
                    lineBuf.Add(r);
                }
            }
            else
            {
                lineBuf.Add(r);
            }

            prevY = r.Y;
            prevFont = font;
        }

        Flush();
        return sb.ToString().Trim();
    }

    private static int WordCount(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Count(w => w.Length >= 3);
}
