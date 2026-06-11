// Microsoft TableTransformer (structure recognition v1.1) via Xenova ONNX export.
// DETR: resize longest edge 800, /255, ImageNet mean/std, RGB. Outputs: logits [1,125,7]
// (6 classes + no-object last), pred_boxes [1,125,4] cxcywh normalized to the crop.
// Ported from the Phase 0 spike. Returns a cell grid; Markdown rendering happens in the
// composer. Lines that fall inside the region but outside the predicted grid are returned
// as UnassignedLines — the composer must emit them (no-text-loss invariant).

using Foliant.Internal;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Foliant.Tables.TableTransformer;

public sealed class TableTransformerExtractor : ITableExtractor
{
    private const int MaxEdge = 800;
    private const float ScoreThreshold = 0.5f;

    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    // ≈ legacy SKFilterQuality.Medium (bilinear + mipmaps)
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);
    private static readonly string[] Labels =
        { "table", "table column", "table row", "table column header", "table projected row header", "table spanning cell" };

    private readonly InferenceSession _session;
    private readonly string _inputName;

    public TableTransformerExtractor(string modelPath)
    {
        _session = new InferenceSession(modelPath);
        _inputName = _session.InputMetadata.Keys.First();
    }

    public TableExtraction Extract(PageImage page, LayoutRegion table, IReadOnlyList<TextLine> pageLines)
    {
        var regionLines = pageLines.Where(l => table.Bounds.ContainsCenterOf(l.Bounds)).ToList();

        using var bitmap = SkiaInterop.ToBitmap(page);
        var (rows, cols) = PredictGrid(bitmap, table.Bounds);

        // Hybrid (RESULTS.md priority #3): forms draw their cell borders, and TableTransformer
        // is out-of-distribution on them. Recursive ruling decomposition finds the form's true
        // (hierarchical) cells; keep whichever structure assigns more of the region's text
        // lines into cells — directly minimizing the leftover/orphan failure class (e.g.
        // SF-33 TOC checkbox marks detached from their rows).
        var leaves = DetectRuledCells(bitmap, table.Bounds);
        if (leaves != null)
        {
            int ttAssigned = CountAssigned(rows, cols, regionLines);
            int leafAssigned = regionLines.Count(l =>
                leaves.Any(c => c.Contains(l.Bounds.CenterX, l.Bounds.CenterY)));
            if (leafAssigned > ttAssigned)
                return ExtractFromLeaves(leaves, regionLines);
        }

        if (rows.Count == 0 || cols.Count == 0)
            return new TableExtraction(null, regionLines);   // composer degrades to paragraph

        // Fill grid: line center inside row∩col cell
        var cells = new List<TableCell>(rows.Count * cols.Count);
        var consumed = new HashSet<TextLine>();

        for (int r = 0; r < rows.Count; r++)
        for (int c = 0; c < cols.Count; c++)
        {
            var cell = SKRect.Intersect(rows[r], cols[c]);
            var cellLines = regionLines
                .Where(l => cell.Contains(l.Bounds.CenterX, l.Bounds.CenterY))
                .OrderBy(l => l.Bounds.Y1).ThenBy(l => l.Bounds.X1)
                .ToList();
            foreach (var l in cellLines) consumed.Add(l);

            cells.Add(new TableCell(
                r, c,
                string.Join(" ", cellLines.Select(l => l.Text)),
                new BoundingBox(cell.Left, cell.Top, cell.Right, cell.Bottom)));
        }

        var leftover = regionLines.Where(l => !consumed.Contains(l)).ToList();
        return new TableExtraction(
            new TableStructure(rows.Count, cols.Count, cells), leftover);
    }

    /// <summary>Number of region lines whose center lands inside some row∩col cell.</summary>
    private static int CountAssigned(
        List<SKRect> rows, List<SKRect> cols, IReadOnlyList<TextLine> regionLines)
    {
        if (rows.Count == 0 || cols.Count == 0) return 0;
        return regionLines.Count(l =>
            rows.Any(r => l.Bounds.CenterY >= r.Top && l.Bounds.CenterY <= r.Bottom) &&
            cols.Any(c => l.Bounds.CenterX >= c.Left && l.Bounds.CenterX <= c.Right));
    }

    /// <summary>Crops the region and runs recursive ruling decomposition (page coordinates).</summary>
    private static List<SKRect>? DetectRuledCells(SKBitmap page, BoundingBox region)
    {
        float pad = 8;
        var crop = SKRect.Create(
            Math.Max(0, region.X1 - pad), Math.Max(0, region.Y1 - pad),
            Math.Min(page.Width, region.X2 + pad) - Math.Max(0, region.X1 - pad),
            Math.Min(page.Height, region.Y2 + pad) - Math.Max(0, region.Y1 - pad));

        int cw = (int)crop.Width, ch = (int)crop.Height;
        if (cw < 8 || ch < 8) return null;

        using var cropBmp = new SKBitmap(cw, ch, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(cropBmp))
            canvas.DrawBitmap(page, crop, new SKRect(0, 0, cw, ch));

        return RulingGrid.DetectCells(cropBmp, crop);
    }

    /// <summary>
    /// Builds a TableStructure from ruling-decomposition leaf cells: leaves cluster into
    /// rows by vertical overlap, then order left-to-right for column indices. Rows may have
    /// differing cell counts (hierarchical forms); the renderer tolerates missing positions.
    /// </summary>
    private static TableExtraction ExtractFromLeaves(
        List<SKRect> leaves, IReadOnlyList<TextLine> regionLines)
    {
        // Cluster into rows by TOP edge: leaves of one ruled row share their top band
        // boundary (the physical ruling line), so tops match within line thickness. A
        // growing vertical-extent cluster is wrong here — one tall leaf (a spanning cell)
        // stretches the band and swallows the next row, fusing adjacent form rows
        // (observed on SF-33 TOC rows E/F, 2026-06-11).
        const float topTolerance = 6f;
        var rowClusters = new List<(float Top, List<SKRect> Cells)>();
        foreach (var leaf in leaves.OrderBy(c => c.Top).ThenBy(c => c.Left))
        {
            bool placed = false;
            for (int i = 0; i < rowClusters.Count; i++)
            {
                if (Math.Abs(rowClusters[i].Top - leaf.Top) <= topTolerance)
                {
                    rowClusters[i].Cells.Add(leaf);
                    placed = true;
                    break;
                }
            }
            if (!placed) rowClusters.Add((leaf.Top, new List<SKRect> { leaf }));
        }

        var cells = new List<TableCell>();
        var consumed = new HashSet<TextLine>();
        int maxCols = 0;

        var orderedRows = rowClusters.OrderBy(r => r.Top).ToList();
        for (int r = 0; r < orderedRows.Count; r++)
        {
            var rowCells = orderedRows[r].Cells.OrderBy(c => c.Left).ToList();
            maxCols = Math.Max(maxCols, rowCells.Count);
            for (int c = 0; c < rowCells.Count; c++)
            {
                var cellLines = regionLines
                    .Where(l => rowCells[c].Contains(l.Bounds.CenterX, l.Bounds.CenterY))
                    .OrderBy(l => l.Bounds.Y1).ThenBy(l => l.Bounds.X1)
                    .ToList();
                foreach (var l in cellLines) consumed.Add(l);
                if (cellLines.Count == 0) continue;   // sparse forms: skip empty leaves

                cells.Add(new TableCell(
                    r, c,
                    string.Join(" ", cellLines.Select(l => l.Text)),
                    new BoundingBox(rowCells[c].Left, rowCells[c].Top,
                                    rowCells[c].Right, rowCells[c].Bottom)));
            }
        }

        var leftover = regionLines.Where(l => !consumed.Contains(l)).ToList();
        return new TableExtraction(
            new TableStructure(orderedRows.Count, Math.Max(1, maxCols), cells), leftover);
    }

    private (List<SKRect> Rows, List<SKRect> Cols) PredictGrid(SKBitmap page, BoundingBox region)
    {
        // Crop the table region (small pad helps DETR see the border)
        float pad = 8;
        var crop = SKRect.Create(
            Math.Max(0, region.X1 - pad), Math.Max(0, region.Y1 - pad),
            Math.Min(page.Width, region.X2 + pad) - Math.Max(0, region.X1 - pad),
            Math.Min(page.Height, region.Y2 + pad) - Math.Max(0, region.Y1 - pad));

        int cw = (int)crop.Width, ch = (int)crop.Height;
        if (cw < 8 || ch < 8) return (new List<SKRect>(), new List<SKRect>());

        using var cropBmp = new SKBitmap(cw, ch, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(cropBmp))
            canvas.DrawBitmap(page, crop, new SKRect(0, 0, cw, ch));

        float scale = (float)MaxEdge / Math.Max(cw, ch);
        int w = Math.Max(32, (int)(cw * scale)), h = Math.Max(32, (int)(ch * scale));
        using var resized = cropBmp.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque),
                                           Sampling)
                            ?? throw new InvalidOperationException("table resize failed");

        var tensor = new DenseTensor<float>(new[] { 1, 3, h, w });
        var px = resized.Pixels;
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                var c = px[row + x];
                tensor[0, 0, y, x] = (c.Red / 255f - Mean[0]) / Std[0];
                tensor[0, 1, y, x] = (c.Green / 255f - Mean[1]) / Std[1];
                tensor[0, 2, y, x] = (c.Blue / 255f - Mean[2]) / Std[2];
            }
        }

        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
        var logits = (results.FirstOrDefault(r => r.Name == "logits") ?? results[0]).AsTensor<float>();
        var boxes = (results.FirstOrDefault(r => r.Name == "pred_boxes") ?? results[1]).AsTensor<float>();
        int queries = logits.Dimensions[1], classes = logits.Dimensions[2];

        var rows = new List<SKRect>();
        var cols = new List<SKRect>();

        for (int q = 0; q < queries; q++)
        {
            // softmax; last class = no-object
            float max = float.MinValue;
            for (int c = 0; c < classes; c++) max = Math.Max(max, logits[0, q, c]);
            double sum = 0; var probs = new double[classes];
            for (int c = 0; c < classes; c++) { probs[c] = Math.Exp(logits[0, q, c] - max); sum += probs[c]; }

            int best = 0; double bestP = 0;
            for (int c = 0; c < classes - 1; c++)
                if (probs[c] / sum > bestP) { bestP = probs[c] / sum; best = c; }
            if (bestP < ScoreThreshold) continue;

            // cxcywh normalized → page coords
            float cx = boxes[0, q, 0] * cw + crop.Left, cyc = boxes[0, q, 1] * ch + crop.Top;
            float bw = boxes[0, q, 2] * cw, bh = boxes[0, q, 3] * ch;
            var rect = SKRect.Create(cx - bw / 2, cyc - bh / 2, bw, bh);

            switch (Labels[Math.Min(best, Labels.Length - 1)])
            {
                case "table row": case "table projected row header":
                case "table column header": rows.Add(rect); break;
                case "table column": cols.Add(rect); break;
            }
        }

        // NMS: DETR predicts overlapping duplicates; merge rects overlapping >50%
        rows = MergeOverlapping(rows.OrderBy(r => r.MidY).ToList(), vertical: true);
        cols = MergeOverlapping(cols.OrderBy(c => c.MidX).ToList(), vertical: false);
        return (rows, cols);
    }

    /// <summary>Merges sorted row/column rects whose overlap on the relevant axis exceeds 50%.</summary>
    private static List<SKRect> MergeOverlapping(List<SKRect> rects, bool vertical)
    {
        var merged = new List<SKRect>();
        foreach (var r in rects)
        {
            if (merged.Count > 0)
            {
                var last = merged[^1];
                float overlap = vertical
                    ? Math.Min(last.Bottom, r.Bottom) - Math.Max(last.Top, r.Top)
                    : Math.Min(last.Right, r.Right) - Math.Max(last.Left, r.Left);
                float minSpan = vertical
                    ? Math.Min(last.Height, r.Height)
                    : Math.Min(last.Width, r.Width);

                if (minSpan > 0 && overlap / minSpan > 0.5f)
                {
                    merged[^1] = SKRect.Union(last, r);   // duplicate → merge
                    continue;
                }
            }
            merged.Add(r);
        }
        return merged;
    }

    public void Dispose() => _session.Dispose();
}
