// Rule-based grid detection from explicit ruling lines (RESULTS.md Phase 1 priority #3).
// Government forms (SF-33, SF-30, questionnaire grids) draw their cell borders; pixel
// projection finds them far more reliably than a DETR model that is out-of-distribution
// on forms.
//
// Forms are HIERARCHICAL: a full-width ruling line splits the form into sections, and each
// section has its own sub-grid whose lines span only that section (the SF-33 TOC's narrow
// checkbox columns are invisible to any single whole-region grid — verified by geometry
// inspection 2026-06-11). So detection is a recursive ruling decomposition: split a cell by
// the ruling lines that span (almost) its full width/height, recurse into the parts, and
// the leaves are the form's true cells.
//
// Used as a complement: the extractor keeps whichever structure (TableTransformer's grid or
// these leaf cells) assigns more of the region's text lines into cells.

using SkiaSharp;

namespace Foliant.Tables.TableTransformer;

internal static class RulingGrid
{
    private const double InkLumaThreshold = 160;

    /// <summary>A ruling line must span at least this fraction of the cell being split.</summary>
    private const double SpanFraction = 0.85;

    /// <summary>Minimum cell dimension (px at 300 DPI); smaller fragments are not split further.</summary>
    private const int MinCell = 12;

    private const int MaxDepth = 4;

    /// <summary>
    /// Recursive ruling decomposition of a table-region crop. Returns leaf cells in page
    /// coordinates, or null when the region is not visibly ruled (fewer than 4 leaves).
    /// </summary>
    public static List<SKRect>? DetectCells(SKBitmap crop, SKRect cropInPage)
    {
        int w = crop.Width, h = crop.Height;
        if (w < 3 * MinCell || h < 3 * MinCell) return null;

        // Row-major ink bitmap + per-row and per-column prefix sums for O(1) span queries.
        var px = crop.Pixels;
        var ink = new bool[h * w];
        for (int i = 0; i < px.Length; i++)
        {
            var c = px[i];
            ink[i] = c.Red * 0.299 + c.Green * 0.587 + c.Blue * 0.114 < InkLumaThreshold;
        }

        // prefRow[y * (w+1) + x] = ink pixels in row y, columns [0, x)
        var prefRow = new int[h * (w + 1)];
        for (int y = 0; y < h; y++)
        {
            int b = y * (w + 1), r = y * w;
            for (int x = 0; x < w; x++)
                prefRow[b + x + 1] = prefRow[b + x] + (ink[r + x] ? 1 : 0);
        }

        // prefCol[x * (h+1) + y] = ink pixels in column x, rows [0, y)
        var prefCol = new int[w * (h + 1)];
        for (int x = 0; x < w; x++)
        {
            int b = x * (h + 1);
            for (int y = 0; y < h; y++)
                prefCol[b + y + 1] = prefCol[b + y] + (ink[y * w + x] ? 1 : 0);
        }

        var leaves = new List<SKRect>();
        Split(0, 0, w, h, 0);
        if (leaves.Count < 4) return null;

        return leaves
            .Select(c => new SKRect(
                cropInPage.Left + c.Left, cropInPage.Top + c.Top,
                cropInPage.Left + c.Right, cropInPage.Top + c.Bottom))
            .ToList();

        int RowInk(int y, int x1, int x2) => prefRow[y * (w + 1) + x2] - prefRow[y * (w + 1) + x1];
        int ColInk(int x, int y1, int y2) => prefCol[x * (h + 1) + y2] - prefCol[x * (h + 1) + y1];

        void Split(int x1, int y1, int x2, int y2, int depth)
        {
            if (depth < MaxDepth && x2 - x1 >= 2 * MinCell && y2 - y1 >= 2 * MinCell)
            {
                // Horizontal cuts: rows spanning ≥85% of this cell's width (interior only)
                var hCuts = FindCuts(
                    y1 + MinCell, y2 - MinCell,
                    y => RowInk(y, x1, x2) >= SpanFraction * (x2 - x1));
                if (hCuts.Count > 0)
                {
                    int top = y1;
                    foreach (int cut in hCuts)
                    {
                        if (cut - top >= MinCell) Split(x1, top, x2, cut, depth + 1);
                        top = cut;
                    }
                    if (y2 - top >= MinCell) Split(x1, top, x2, y2, depth + 1);
                    return;
                }

                // Vertical cuts: columns spanning ≥85% of this cell's height
                var vCuts = FindCuts(
                    x1 + MinCell, x2 - MinCell,
                    x => ColInk(x, y1, y2) >= SpanFraction * (y2 - y1));
                if (vCuts.Count > 0)
                {
                    int left = x1;
                    foreach (int cut in vCuts)
                    {
                        if (cut - left >= MinCell) Split(left, y1, cut, y2, depth + 1);
                        left = cut;
                    }
                    if (x2 - left >= MinCell) Split(left, y1, x2, y2, depth + 1);
                    return;
                }
            }

            leaves.Add(new SKRect(x1, y1, x2, y2));
        }
    }

    /// <summary>Positions in [from, to) satisfying the predicate; adjacent hits (≤2px apart,
    /// thick/anti-aliased lines) merge into their center.</summary>
    internal static List<int> FindCuts(int from, int to, Func<int, bool> isLine)
    {
        var cuts = new List<int>();
        int runStart = -1, lastHit = -10;
        for (int i = from; i < to; i++)
        {
            if (isLine(i))
            {
                if (i - lastHit > 2)
                {
                    if (runStart >= 0) cuts.Add((runStart + lastHit) / 2);
                    runStart = i;
                }
                lastHit = i;
            }
        }
        if (runStart >= 0) cuts.Add((runStart + lastHit) / 2);
        return cuts;
    }
}
