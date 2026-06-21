using System.Text;

namespace Foliant.Pipeline;

/// <summary>
/// Federal-Standard-Form-only table rendering. Identical to <see cref="MarkdownComposer.RenderTable"/>
/// EXCEPT it re-splits a data row whose lines form several vertically-separated bands across two or more
/// columns into one row per band. SF schedule areas (e.g. SF-1449 blocks 19–24, SF-1409/1410 abstracts of
/// offers) have no visible row rules, so the table model predicts a single data row and every item's
/// values collapse into one mega-row; this restores the per-item rows.
///
/// Used ONLY when the page is identified as a federal Standard Form (see <see cref="FormIdentifier"/>), so
/// the shared/default table path — and therefore the entire Gate 6 corpus — is provably untouched. Code is
/// intentionally close to the default renderer (duplicated, not shared) to keep that guarantee.
/// </summary>
internal static class FederalFormTableRenderer
{
    public static string Render(TableExtraction extraction, IReadOnlyList<TextLine> regionLines)
    {
        var sb = new StringBuilder();

        if (extraction.Structure is { ColumnCount: > 0 } t)
        {
            var rows = ExpandCollapsedRows(t, regionLines);
            for (int r = 0; r < rows.Count; r++)
            {
                sb.Append('|');
                for (int c = 0; c < t.ColumnCount; c++)
                {
                    string text = c < rows[r].Length ? rows[r][c] ?? "" : "";
                    sb.Append(' ').Append(text.Replace("|", "\\|")).Append(" |");
                }
                sb.AppendLine();
                if (r == 0)   // header separator after the first (header) row
                {
                    sb.Append('|');
                    for (int c = 0; c < t.ColumnCount; c++) sb.Append("---|");
                    sb.AppendLine();
                }
            }
        }

        // Same no-text-loss tail as the default renderer: lines inside the region but outside the grid.
        if (extraction.UnassignedLines.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            foreach (var (_, _, text) in LineGrouping.GroupIntoVisualLines(extraction.UnassignedLines))
                sb.AppendLine(text);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns the table as rows of per-column text. The header (row 0) is never split. A DATA row is
    /// split into one row per vertical band when its lines form ≥2 bands AND ≥2 columns carry content in
    /// multiple bands (genuinely tabular, not a wrapped paragraph in one column). Otherwise the row is
    /// emitted unchanged from the predicted cell text.
    /// </summary>
    internal static List<string[]> ExpandCollapsedRows(TableStructure t, IReadOnlyList<TextLine> regionLines)
    {
        var result = new List<string[]>();
        for (int r = 0; r < t.RowCount; r++)
        {
            var rowCells = t.Cells.Where(x => x.Row == r).ToList();

            string[] Original()
            {
                var row = new string[t.ColumnCount];
                foreach (var cell in rowCells)
                    if (cell.Column >= 0 && cell.Column < t.ColumnCount) row[cell.Column] = cell.Text;
                return row;
            }

            if (r == 0 || rowCells.Count == 0) { result.Add(Original()); continue; }   // never split header

            // Column X-ranges from THIS row's own cells. Global ranges are unreliable on a whole-form
            // table whose columns differ block-to-block (they smear together and collapse every value
            // into one column, defeating the multi-column guard).
            var colX = new (float Lo, float Hi)[t.ColumnCount];
            for (int c = 0; c < t.ColumnCount; c++)
            {
                var cc = rowCells.Where(x => x.Column == c).ToList();
                colX[c] = cc.Count > 0 ? (cc.Min(x => x.Bounds.X1), cc.Max(x => x.Bounds.X2)) : (float.NaN, float.NaN);
            }
            int ColumnOf(TextLine l)
            {
                for (int c = 0; c < t.ColumnCount; c++)
                    if (!float.IsNaN(colX[c].Lo) && l.Bounds.CenterX >= colX[c].Lo && l.Bounds.CenterX <= colX[c].Hi)
                        return c;
                return -1;
            }

            float y1 = rowCells.Min(x => x.Bounds.Y1), y2 = rowCells.Max(x => x.Bounds.Y2);
            var rowLines = regionLines
                .Where(l => l.Bounds.CenterY >= y1 - 0.5f && l.Bounds.CenterY <= y2 + 0.5f)
                .ToList();
            var bands = ClusterByY(rowLines);

            // Guard: ≥2 columns must each carry content in ≥2 different bands (genuinely tabular multi-row,
            // not a single wrapped paragraph).
            var colBands = new HashSet<int>[t.ColumnCount];
            for (int c = 0; c < t.ColumnCount; c++) colBands[c] = new HashSet<int>();
            for (int b = 0; b < bands.Count; b++)
                foreach (var l in bands[b]) { int c = ColumnOf(l); if (c >= 0) colBands[c].Add(b); }
            int multiBandCols = colBands.Count(s => s.Count >= 2);

            if (bands.Count < 2 || multiBandCols < 2) { result.Add(Original()); continue; }

            foreach (var band in bands)
            {
                var row = new string[t.ColumnCount];
                for (int c = 0; c < t.ColumnCount; c++)
                    row[c] = string.Join(" ", band.Where(l => ColumnOf(l) == c)
                                                   .OrderBy(l => l.Bounds.X1).Select(l => l.Text));
                result.Add(row);
            }
        }
        return result;
    }

    /// <summary>Clusters lines into vertical bands: sorted by center-Y, a gap larger than ~0.8× the
    /// median line height starts a new band.</summary>
    internal static List<List<TextLine>> ClusterByY(IReadOnlyList<TextLine> lines)
    {
        var bands = new List<List<TextLine>>();
        if (lines.Count == 0) return bands;

        var sorted = lines.OrderBy(l => l.Bounds.CenterY).ToList();
        float medianH = sorted.Select(l => Math.Max(1f, l.Bounds.Height)).OrderBy(h => h)
                              .ElementAt(sorted.Count / 2);
        float gap = 0.8f * medianH;

        var current = new List<TextLine> { sorted[0] };
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i].Bounds.CenterY - sorted[i - 1].Bounds.CenterY > gap)
            {
                bands.Add(current);
                current = new List<TextLine>();
            }
            current.Add(sorted[i]);
        }
        bands.Add(current);
        return bands;
    }
}
