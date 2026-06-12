// SLANet-plus (PP-StructureV2/V3) table-structure recognition via the official
// PaddlePaddle ONNX export (Apache 2.0).
//
// Contract (from the model repo's inference.yml, verified 2026-06-12):
//   Input : BGR, resize longest side to 488 keeping aspect, /255 then ImageNet mean/std,
//           pad bottom/right to 488×488, CHW float32.
//   Output: autoregressive step sequence (max 500):
//             structure probs [1, T, C] — HTML structure tokens (dict embedded below,
//               +sos/+eos wrapping per PaddleOCR TableLabelDecode)
//             loc preds      [1, T, 8] — per-cell quad (xyxyxyxy) normalized to the
//               padded input, emitted at the timesteps of <td> / "<td" tokens
//
// Spans (colspan/rowspan) are first-class: the decoder walks the token stream with an
// occupancy grid, so merged cells land at correct (row, column) indices — the main thing
// TableTransformer cannot express. Lines not captured by any predicted cell are returned
// as UnassignedLines (no-text-loss invariant), same contract as every ITableExtractor.

using Foliant.Internal;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Foliant.Tables.PaddleStructure;

public sealed class SlanetPlusExtractor : ITableExtractor
{
    private const int InputSize = 488;

    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    // EFFECTIVE decode dictionary. The yml character_dict is NOT used verbatim:
    // with merge_no_span_structure=true, PaddleOCR's TableLabelDecode mutates it at
    // load time — "</td>" is REMOVED and the merged cell token "<td></td>" is APPENDED.
    // (First-contact bug 2026-06-12: using the yml dict verbatim shifted every token
    // after index 9 by one, making all plain cells decode as the last entry.)
    // TableLabelDecode then wraps as [sos] + dict + [eos]: index 0 = sos, Length+1 = eos.
    private static readonly string[] Tokens =
    {
        "<thead>", "</thead>", "<tbody>", "</tbody>", "<tr>", "</tr>", "<td>", "<td", ">",
        " colspan=\"2\"", " colspan=\"3\"", " colspan=\"4\"", " colspan=\"5\"", " colspan=\"6\"",
        " colspan=\"7\"", " colspan=\"8\"", " colspan=\"9\"", " colspan=\"10\"", " colspan=\"11\"",
        " colspan=\"12\"", " colspan=\"13\"", " colspan=\"14\"", " colspan=\"15\"", " colspan=\"16\"",
        " colspan=\"17\"", " colspan=\"18\"", " colspan=\"19\"", " colspan=\"20\"",
        " rowspan=\"2\"", " rowspan=\"3\"", " rowspan=\"4\"", " rowspan=\"5\"", " rowspan=\"6\"",
        " rowspan=\"7\"", " rowspan=\"8\"", " rowspan=\"9\"", " rowspan=\"10\"", " rowspan=\"11\"",
        " rowspan=\"12\"", " rowspan=\"13\"", " rowspan=\"14\"", " rowspan=\"15\"", " rowspan=\"16\"",
        " rowspan=\"17\"", " rowspan=\"18\"", " rowspan=\"19\"", " rowspan=\"20\"",
        "<td></td>",
    };

    private readonly InferenceSession _session;
    private readonly string _inputName;

    public SlanetPlusExtractor(string modelPath)
    {
        _session = new InferenceSession(modelPath);
        _inputName = _session.InputMetadata.Keys.First();
    }

    public TableExtraction Extract(PageImage page, LayoutRegion table, IReadOnlyList<TextLine> pageLines)
    {
        var regionLines = pageLines.Where(l => table.Bounds.ContainsCenterOf(l.Bounds)).ToList();

        using var bitmap = SkiaInterop.ToBitmap(page);
        var cellQuads = PredictCells(bitmap, table.Bounds);

        if (cellQuads.Count == 0)
            return new TableExtraction(null, regionLines);   // composer degrades to paragraph

        var cells = new List<TableCell>(cellQuads.Count);
        var consumed = new HashSet<TextLine>();
        int rowCount = 0, colCount = 0;

        foreach (var (row, col, bounds) in cellQuads)
        {
            rowCount = Math.Max(rowCount, row + 1);
            colCount = Math.Max(colCount, col + 1);

            var cellLines = regionLines
                .Where(l => bounds.Contains(l.Bounds.CenterX, l.Bounds.CenterY))
                .OrderBy(l => l.Bounds.Y1).ThenBy(l => l.Bounds.X1)
                .ToList();
            foreach (var l in cellLines) consumed.Add(l);

            cells.Add(new TableCell(
                row, col,
                string.Join(" ", cellLines.Select(l => l.Text)),
                new BoundingBox(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom)));
        }

        var leftover = regionLines.Where(l => !consumed.Contains(l)).ToList();
        return new TableExtraction(new TableStructure(rowCount, colCount, cells), leftover);
    }

    /// <summary>Runs SLANet-plus on the cropped region; returns logical (row, col) plus
    /// page-coordinate bounds per predicted cell, spans resolved via an occupancy grid.</summary>
    private List<(int Row, int Col, SKRect Bounds)> PredictCells(SKBitmap page, BoundingBox region)
    {
        float pad = 8;
        var crop = SKRect.Create(
            Math.Max(0, region.X1 - pad), Math.Max(0, region.Y1 - pad),
            Math.Min(page.Width, region.X2 + pad) - Math.Max(0, region.X1 - pad),
            Math.Min(page.Height, region.Y2 + pad) - Math.Max(0, region.Y1 - pad));

        int cw = (int)crop.Width, ch = (int)crop.Height;
        if (cw < 8 || ch < 8) return new List<(int, int, SKRect)>();

        using var cropBmp = new SKBitmap(cw, ch, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(cropBmp))
            canvas.DrawBitmap(page, crop, new SKRect(0, 0, cw, ch));

        // Resize longest side to 488 (keep aspect), pad bottom/right to 488×488
        float scale = (float)InputSize / Math.Max(cw, ch);
        int rw = Math.Max(1, (int)Math.Round(cw * scale));
        int rh = Math.Max(1, (int)Math.Round(ch * scale));
        using var resized = cropBmp.Resize(new SKImageInfo(rw, rh, SKColorType.Bgra8888, SKAlphaType.Opaque), Sampling)
                            ?? throw new InvalidOperationException("slanet resize failed");

        var tensor = new DenseTensor<float>(new[] { 1, 3, InputSize, InputSize });
        var px = resized.Pixels;
        for (int y = 0; y < rh; y++)
        {
            int row = y * rw;
            for (int x = 0; x < rw; x++)
            {
                var c = px[row + x];
                // BGR channel order per the model contract
                tensor[0, 0, y, x] = (c.Blue / 255f - Mean[0]) / Std[0];
                tensor[0, 1, y, x] = (c.Green / 255f - Mean[1]) / Std[1];
                tensor[0, 2, y, x] = (c.Red / 255f - Mean[2]) / Std[2];
            }
        }
        // Padding area stays zero — matches PaddingTableImage (zero-pad after normalize
        // is the effective behavior of the exported graph).

        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });

        bool debug = Environment.GetEnvironmentVariable("FOLIANT_SLANET_DEBUG") == "1";

        // Identify outputs by trailing dimension: 8 → cell quads, otherwise → structure probs
        Tensor<float>? structureProbs = null, locPreds = null;
        foreach (var r in results)
        {
            var t = r.AsTensor<float>();
            if (debug)
                Console.Error.WriteLine(
                    $"[slanet] output '{r.Name}' dims=[{string.Join(",", t.Dimensions.ToArray())}]");
            if (t.Dimensions[^1] == 8) locPreds = t;
            else structureProbs = t;
        }
        if (structureProbs == null || locPreds == null)
        {
            if (debug) Console.Error.WriteLine("[slanet] FAILED to identify outputs (no dims[^1]==8 pair)");
            return new List<(int, int, SKRect)>();
        }

        if (debug)
        {
            int dbgSteps = structureProbs.Dimensions[1], dbgClasses = structureProbs.Dimensions[2];
            var seq = new List<string>();
            for (int t = 0; t < Math.Min(dbgSteps, 80); t++)
            {
                int best = 0; float bestP = float.MinValue;
                for (int c = 0; c < dbgClasses; c++)
                {
                    float p = structureProbs[0, t, c];
                    if (p > bestP) { bestP = p; best = c; }
                }
                seq.Add(best == 0 ? "[sos]"
                    : best == Tokens.Length + 1 ? "[eos]"
                    : best > 0 && best <= Tokens.Length ? Tokens[best - 1]
                    : $"[{best}?]");
                if (best == Tokens.Length + 1) break;
            }
            Console.Error.WriteLine($"[slanet] steps={dbgSteps} classes={dbgClasses} " +
                                    $"(dict={Tokens.Length} ⇒ expected classes={Tokens.Length + 2})");
            Console.Error.WriteLine($"[slanet] tokens: {string.Join(" ", seq)}");
            Console.Error.WriteLine($"[slanet] loc[t=0]: " + string.Join(",",
                Enumerable.Range(0, 8).Select(k => locPreds[0, 0, k].ToString("0.000"))));
        }

        return DecodeGrid(structureProbs, locPreds, crop);
    }

    private static List<(int Row, int Col, SKRect Bounds)> DecodeGrid(
        Tensor<float> structureProbs, Tensor<float> locPreds, SKRect crop)
    {
        int steps = structureProbs.Dimensions[1];
        int classes = structureProbs.Dimensions[2];
        int eos = Tokens.Length + 1;                       // [sos] + dict + [eos]

        var cells = new List<(int, int, SKRect)>();
        // occupancy[row] = set of columns consumed (by spans from earlier rows/cells)
        var occupancy = new Dictionary<int, HashSet<int>>();
        int curRow = -1, curCol = 0;

        for (int t = 0; t < steps; t++)
        {
            int best = 0; float bestP = float.MinValue;
            for (int c = 0; c < classes; c++)
            {
                float p = structureProbs[0, t, c];
                if (p > bestP) { bestP = p; best = c; }
            }

            if (best == eos) break;
            if (best <= 0 || best > Tokens.Length) continue;   // sos / out of dict
            string token = Tokens[best - 1];

            switch (token)
            {
                case "<tr>":
                    curRow++;
                    curCol = 0;
                    break;

                case "<td></td>":   // merged no-span cell (merge_no_span_structure)
                case "<td>":
                case "<td":
                {
                    if (curRow < 0) curRow = 0;

                    // Spans: "<td" / "<td>" may be followed by attribute tokens, an optional
                    // repeated td-open marker, and a closing ">". The whole group is CONSUMED
                    // (t advanced) — re-processing the trailing "<td" created phantom cells
                    // that shifted columns right (observed on CLIN pricing, 2026-06-12).
                    int colspan = 1, rowspan = 1;
                    if (token != "<td></td>")
                    {
                        int a = t + 1;
                        for (; a < steps; a++)
                        {
                            int ab = 0; float abp = float.MinValue;
                            for (int c = 0; c < classes; c++)
                            {
                                float p = structureProbs[0, a, c];
                                if (p > abp) { abp = p; ab = c; }
                            }
                            if (ab <= 0 || ab > Tokens.Length) break;
                            string attr = Tokens[ab - 1];
                            if (attr == ">") { a++; break; }            // group closed
                            if (attr.StartsWith(" colspan=", StringComparison.Ordinal))
                                colspan = ParseSpan(attr);
                            else if (attr.StartsWith(" rowspan=", StringComparison.Ordinal))
                                rowspan = ParseSpan(attr);
                            else if (attr is "<td" or "<td>") { }       // repeated open marker
                            else break;
                        }
                        t = a - 1;                                      // consume the group
                    }

                    // Advance past columns occupied by rowspans from above
                    var occupied = occupancy.TryGetValue(curRow, out var occ) ? occ : null;
                    while (occupied != null && occupied.Contains(curCol)) curCol++;

                    // Cell quad at this timestep. Per PaddleOCR TableLabelDecode._bbox_decode,
                    // coords are normalized against the ORIGINAL image dims (x×w, y×h) —
                    // not the padded canvas. (First-contact bug 2026-06-12: scaling by
                    // 488/scale broke the non-dominant axis; wide tables kept partial
                    // scores, tall grids scored zero.)
                    float x1 = float.MaxValue, y1 = float.MaxValue, x2 = float.MinValue, y2 = float.MinValue;
                    for (int k = 0; k < 4; k++)
                    {
                        float qx = locPreds[0, t, k * 2] * crop.Width + crop.Left;
                        float qy = locPreds[0, t, k * 2 + 1] * crop.Height + crop.Top;
                        x1 = Math.Min(x1, qx); y1 = Math.Min(y1, qy);
                        x2 = Math.Max(x2, qx); y2 = Math.Max(y2, qy);
                    }

                    cells.Add((curRow, curCol, new SKRect(x1, y1, x2, y2)));

                    // Mark occupancy for spans
                    for (int dr = 0; dr < rowspan; dr++)
                    for (int dc = 0; dc < colspan; dc++)
                    {
                        if (dr == 0 && dc == 0) continue;
                        int rr = curRow + dr;
                        if (!occupancy.TryGetValue(rr, out var set))
                            occupancy[rr] = set = new HashSet<int>();
                        set.Add(curCol + dc);
                    }

                    curCol += colspan;
                    break;
                }
            }
        }

        return cells;
    }

    private static int ParseSpan(string attr)
    {
        int q1 = attr.IndexOf('"');
        int q2 = attr.LastIndexOf('"');
        return q2 > q1 && int.TryParse(attr.AsSpan(q1 + 1, q2 - q1 - 1), out int n)
            ? Math.Clamp(n, 1, 20) : 1;
    }

    public void Dispose() => _session.Dispose();
}
