// Embedded text-layer fast path (Phase 1 priority #1 from the spike):
// born-digital pages take characters verbatim from the PDF text layer, mapped into the
// same raster coordinate space the layout detector works in — so the rest of the
// pipeline is identical regardless of where the characters came from.

using UglyToad.PdfPig;

namespace Foliant.Pipeline;

public sealed class PdfTextLayerReader : ITextLayerReader
{
    public TextLayerPage? Read(byte[] pdf, int pageNumber, int dpi)
    {
        using var doc = PdfDocument.Open(pdf);
        var page = doc.GetPage(pageNumber);

        var words = page.GetWords()
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .ToList();
        if (words.Count == 0) return null;

        // PdfPig: origin bottom-left, Y up, units = points (72/inch).
        // Raster: origin top-left, Y down, pixels at `dpi`.
        float scale = dpi / 72f;
        float pageH = (float)page.Height;

        var wordBoxes = new List<(BoundingBox Box, string Text)>(words.Count);
        long keptChars = 0, droppedChars = 0, undecodableChars = 0;
        foreach (var w in words)
        {
            undecodableChars += w.Text.Count(IsUndecodable);
            var bb = w.BoundingBox;
            // bb.Top is the larger PDF-Y (Y-up), so after the flip it becomes the smaller
            // raster Y1. Min/Max normalization guards rotated-text rectangles where
            // Left/Right/Top/Bottom orientation can differ.
            float xA = (float)bb.Left * scale, xB = (float)bb.Right * scale;
            float yA = (pageH - (float)bb.Top) * scale, yB = (pageH - (float)bb.Bottom) * scale;
            var box = new BoundingBox(
                Math.Min(xA, xB), Math.Min(yA, yB),
                Math.Max(xA, xB), Math.Max(yA, yB));
            if (box.Width <= 0 || box.Height <= 0)
            {
                // Degenerate glyph boxes. Usually a stray artifact — but on old PDFs with
                // non-embedded fonts (formmsd class: 1998 PageMaker + base-14 Times/Helvetica,
                // /Differences-remapped encodings) PdfPig cannot resolve glyph metrics, the
                // advance widths collapse, and ENTIRE PARAGRAPHS arrive as fused words with
                // zero-size boxes. Dropping them is still right (no usable geometry), but it
                // must be COUNTED: this is exactly the silent text loss that hid behind
                // coverage_missing=0 in the TD-6 sweep (recall 4% on a "text layer" page).
                droppedChars += w.Text.Trim().Length;
                continue;
            }
            keptChars += w.Text.Trim().Length;
            wordBoxes.Add((box, w.Text));
        }
        if (wordBoxes.Count == 0) return null;

        long totalChars = keptChars + droppedChars;
        float droppedFraction = totalChars == 0 ? 0f : (float)droppedChars / totalChars;
        // Undecodable fraction is over ALL text-layer chars (kept + dropped): a CID page's
        // glyphs have valid boxes, so they're all "kept" yet still garbage.
        long allChars = words.Sum(w => (long)w.Text.Length);
        float undecodableFraction = allChars == 0 ? 0f : (float)undecodableChars / allChars;

        return new TextLayerPage(
            GroupWordsIntoRuns(wordBoxes), wordBoxes.Count, droppedFraction, undecodableFraction);
    }

    /// <summary>
    /// A character that survived PDF text extraction but carries no real textual meaning —
    /// the fingerprint of a font with no usable ToUnicode map (subset CID fonts from some
    /// "PDF optimizer" tools). PdfPig returns the raw glyph code, which lands as a C0/C1
    /// control char, the Unicode replacement char, or a private-use-area code point. Tab,
    /// newline and normal whitespace are decodable and excluded.
    /// </summary>
    /// <summary>
    /// True when the text layer is the Adobe LiveCycle "dynamic XFA" placeholder — the page
    /// a non-Adobe viewer shows ("Please wait... If this message is not eventually replaced
    /// by the proper contents of the document..."). For these forms the real content lives in
    /// an XFA XML packet, not the page content stream; the placeholder is ALSO what the page
    /// renders to, so OCR cannot recover the content either. Detecting it lets the pipeline
    /// flag the page honestly instead of emitting the placeholder as if it were document text.
    /// </summary>
    public static bool IsDynamicXfaPlaceholder(IEnumerable<TextLine> lines)
    {
        string joined = string.Join(" ", lines.Select(l => l.Text));
        return joined.Contains("If this message is not eventually replaced",
                   StringComparison.OrdinalIgnoreCase)
            && joined.Contains("proper contents of the document",
                   StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsUndecodable(char c)
    {
        if (c is '\t' or '\n' or '\r' or ' ') return false;
        if (c == '\uFFFD') return true;                     // replacement char
        if (c < '\u0020') return true;                      // C0 control
        if (c is >= '\u007F' and <= '\u009F') return true; // DEL + C1 control
        if (c is >= '\uE000' and <= '\uF8FF') return true; // BMP private use area
        return false;
    }

    /// <summary>
    /// Groups words into baseline rows, then splits each row into runs at large horizontal
    /// gaps. Run granularity matters: OCR DB boxes are roughly per-cell/per-fragment, and
    /// table-cell assignment relies on that. A single full-width row line would smear an
    /// entire table row into one cell.
    /// </summary>
    internal static IReadOnlyList<TextLine> GroupWordsIntoRuns(
        IReadOnlyList<(BoundingBox Box, string Text)> words)
    {
        // 1. Cluster into baseline rows (same heuristic the spike validated).
        var rows = new List<List<(BoundingBox Box, string Text)>>();
        foreach (var w in words.OrderBy(w => w.Box.CenterY))
        {
            if (rows.Count > 0)
            {
                var row = rows[^1];
                float rowCy = row.Average(t => t.Box.CenterY);
                float rowH = row.Average(t => t.Box.Height);
                if (Math.Abs(w.Box.CenterY - rowCy) < 0.6f * rowH) { row.Add(w); continue; }
            }
            rows.Add(new List<(BoundingBox, string)> { w });
        }

        // 2. Split each row into runs at gaps wider than ~1.5× glyph height
        //    (word spacing is ~0.25–0.5em; column/field gaps are much wider).
        var lines = new List<TextLine>();
        foreach (var row in rows)
        {
            var sorted = row.OrderBy(w => w.Box.X1).ToList();
            float rowH = Math.Max(1f, row.Average(t => t.Box.Height));
            float gapThreshold = 1.5f * rowH;

            var run = new List<(BoundingBox Box, string Text)> { sorted[0] };
            foreach (var w in sorted.Skip(1))
            {
                if (w.Box.X1 - run[^1].Box.X2 > gapThreshold)
                {
                    lines.Add(MakeLine(run));
                    run = new List<(BoundingBox, string)>();
                }
                run.Add(w);
            }
            lines.Add(MakeLine(run));
        }

        return lines
            .OrderBy(l => Math.Round(l.Bounds.Y1 / 20.0))
            .ThenBy(l => l.Bounds.X1)
            .ToList();
    }

    private static TextLine MakeLine(List<(BoundingBox Box, string Text)> run)
    {
        var bounds = run.Select(w => w.Box).Aggregate(BoundingBox.Union);
        string text = string.Join(" ", run.Select(w => w.Text));
        return new TextLine(bounds, text, 1f, TextSource.TextLayer);
    }
}
