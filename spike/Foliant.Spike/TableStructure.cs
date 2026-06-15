// Stage 4 — Microsoft TableTransformer (structure recognition v1.1) via Xenova ONNX export.
// DETR: resize longest edge 800, /255, ImageNet mean/std, RGB. Outputs: logits [1,125,7]
// (6 classes + no-object last), pred_boxes [1,125,4] cxcywh normalized to the crop.

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Foliant.Spike;

public sealed class TableStructureExtractor : IDisposable
{
    private const int MaxEdge = 800;
    private const float ScoreThreshold = 0.5f;
    // Share of a region's text allowed OUTSIDE the predicted grid before we treat the block as
    // prose-in-a-box rather than a real table (mirrors MarkdownComposer in the shipping library).
    private const double MaxUnassignedTextFraction = 0.25;

    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };
    private static readonly string[] Labels =
        { "table", "table column", "table row", "table column header", "table projected row header", "table spanning cell" };

    private readonly InferenceSession _session;
    private readonly string _inputName;

    public TableStructureExtractor(string modelPath)
    {
        _session = new InferenceSession(modelPath);
        _inputName = _session.InputMetadata.Keys.First();
    }

    /// <summary>Extracts a Markdown table for one layout table region.</summary>
    public string ExtractMarkdown(SKBitmap page, LayoutRegion region, IReadOnlyList<TextLine> pageLines)
    {
        // Crop the table region (small pad helps DETR see the border)
        float pad = 8;
        var crop = SKRect.Create(
            Math.Max(0, region.X1 - pad), Math.Max(0, region.Y1 - pad),
            Math.Min(page.Width, region.X2 + pad) - Math.Max(0, region.X1 - pad),
            Math.Min(page.Height, region.Y2 + pad) - Math.Max(0, region.Y1 - pad));

        int cw = (int)crop.Width, ch = (int)crop.Height;
        using var cropBmp = new SKBitmap(cw, ch, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(cropBmp))
            canvas.DrawBitmap(page, crop, new SKRect(0, 0, cw, ch));

        float scale = (float)MaxEdge / Math.Max(cw, ch);
        int w = Math.Max(32, (int)(cw * scale)), h = Math.Max(32, (int)(ch * scale));
        using var resized = cropBmp.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque),
                                           SKFilterQuality.Medium)
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

            switch (Labels[best])
            {
                case "table row": case "table projected row header":
                case "table column header": rows.Add(rect); break;
                case "table column": cols.Add(rect); break;
            }
        }

        if (rows.Count == 0 || cols.Count == 0)
            return FallbackParagraph(region, pageLines);

        // NMS: DETR predicts overlapping duplicates; merge rects overlapping >50%
        rows = MergeOverlapping(rows.OrderBy(r => r.MidY).ToList(), vertical: true);
        cols = MergeOverlapping(cols.OrderBy(c => c.MidX).ToList(), vertical: false);

        // Fill grid: line center inside row∩col cell
        var grid = new string[rows.Count, cols.Count];
        var regionLines = pageLines.Where(l => Inside(region, l)).ToList();
        var consumed = new HashSet<TextLine>();

        for (int r = 0; r < rows.Count; r++)
        for (int c = 0; c < cols.Count; c++)
        {
            var cell = SKRect.Intersect(rows[r], cols[c]);
            var cellLines = regionLines
                .Where(l => cell.Contains((l.X1 + l.X2) / 2, (l.Y1 + l.Y2) / 2))
                .OrderBy(l => l.Y1).ThenBy(l => l.X1)
                .ToList();
            foreach (var l in cellLines) consumed.Add(l);
            grid[r, c] = string.Join(" ", cellLines.Select(l => l.Text)).Replace("|", "\\|");
        }

        // Grid-fit guard: a box-grid FORM block mis-detected as a table ejects sentences that span
        // its fake cell borders outside the grid, scrambling them on linearization. If the grid
        // captures too little of the region's text (or is a single column), render the block as
        // flowing reading-order prose so spanning sentences stay intact and in order.
        var leftoverForFit = regionLines.Where(l => !consumed.Contains(l)).ToList();
        double totalLen = regionLines.Sum(l => (double)l.Text.Length);
        double leftoverLen = leftoverForFit.Sum(l => (double)l.Text.Length);
        if (cols.Count <= 1 || (totalLen > 0 && leftoverLen / totalLen > MaxUnassignedTextFraction))
            return string.Join("\n", ReadingOrder.GroupIntoVisualLines(regionLines).Select(g => g.Text));

        var sb = new System.Text.StringBuilder();
        for (int r = 0; r < rows.Count; r++)
        {
            sb.Append('|');
            for (int c = 0; c < cols.Count; c++) sb.Append(' ').Append(grid[r, c]).Append(" |");
            sb.AppendLine();
            if (r == 0)
            {
                sb.Append('|');
                for (int c = 0; c < cols.Count; c++) sb.Append("---|");
                sb.AppendLine();
            }
        }

        // No text loss: lines inside the region but outside the predicted grid,
        // grouped into visual rows so same-row fragments stay in left-to-right order
        var leftover = regionLines.Where(l => !consumed.Contains(l)).ToList();
        if (leftover.Count > 0)
        {
            sb.AppendLine();
            foreach (var (_, _, text) in ReadingOrder.GroupIntoVisualLines(leftover))
                sb.AppendLine(text);
        }

        return sb.ToString();
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

    private static string FallbackParagraph(LayoutRegion region, IReadOnlyList<TextLine> lines) =>
        string.Join("\n", lines.Where(l => Inside(region, l)).Select(l => l.Text));

    private static bool Inside(LayoutRegion r, TextLine l)
    {
        float cx = (l.X1 + l.X2) / 2, cy = (l.Y1 + l.Y2) / 2;
        return cx >= r.X1 && cx <= r.X2 && cy >= r.Y1 && cy <= r.Y2;
    }

    public void Dispose() => _session.Dispose();
}
