// PaddleOCR ONNX: DB text detection + CTC recognition.
// Models: monkt/paddleocr-onnx (PP-OCRv5 server det + en mobile rec).
// Ported from the Phase 0 spike (98.3% corpus recall), plus rotated-textline handling:
// tall narrow boxes are recognized at ±90° and the higher-confidence reading wins, or a
// single rotation disambiguated by the optional textline-orientation classifier.
//
// Documented simplifications vs full PaddleOCR (see spike/NOTES.md):
//  - Connected-component boxes on the binarized DB map (axis-aligned) instead of
//    min-area rotated rects + polygon unclip. Federal forms are axis-aligned.
//  - Recognition runs one line at a time (no width-bucketed batching).

using Foliant.Internal;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Foliant.Ocr.PaddleOcr;

public sealed class PaddleOcrEngine : IOcrEngine
{
    // DB detection params (PaddleOCR defaults)
    private const int DetMaxSide = 1280;
    private const float BinaryThreshold = 0.3f;
    private const float BoxScoreThreshold = 0.6f;
    private const float UnclipRatio = 1.6f;

    // Boxes at least this much taller than wide are treated as vertical text candidates.
    private const float VerticalAspectThreshold = 1.5f;

    // Rec models are trained on short text (~25 chars). Very wide lines must be
    // sliced at whitespace gaps and recognized per chunk, then rejoined.
    private const int MaxWidthHeightRatio = 14;

    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    // ≈ legacy SKFilterQuality.Medium (bilinear + mipmaps)
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private readonly InferenceSession _det;
    private readonly InferenceSession _rec;
    private readonly string[] _dict;
    private readonly int _recHeight;   // read from model input dims (v4=32/48, v5=48)
    private readonly TextlineOrientationClassifier? _orientation;

    /// <param name="detModelPath">DB text-detection ONNX model path.</param>
    /// <param name="recModelPath">CTC recognition ONNX model path.</param>
    /// <param name="dictPath">Character dictionary for the recognition model.</param>
    /// <param name="orientationModelPath">
    /// Optional textline-orientation ONNX model. When absent, vertical candidates are
    /// recognized at both ±90° and the higher-confidence reading wins (slower but model-free).
    /// </param>
    public PaddleOcrEngine(
        string detModelPath, string recModelPath, string dictPath,
        string? orientationModelPath = null)
    {
        _det = new InferenceSession(detModelPath);
        _rec = new InferenceSession(recModelPath);
        _dict = File.ReadAllLines(dictPath);

        var recDims = _rec.InputMetadata.Values.First().Dimensions;
        _recHeight = recDims.Length == 4 && recDims[2] > 0 ? recDims[2] : 48;

        _orientation = orientationModelPath != null
            ? new TextlineOrientationClassifier(orientationModelPath)
            : null;
    }

    public IReadOnlyList<TextLine> Recognize(PageImage page)
    {
        using var bitmap = SkiaInterop.ToBitmap(page);
        var boxes = DetectTextBoxes(bitmap);
        var lines = new List<TextLine>();
        foreach (var box in boxes)
        {
            var (text, conf) = RecognizeLine(bitmap, box);
            if (text.Length > 0)
                lines.Add(new TextLine(
                    new BoundingBox(box.Left, box.Top, box.Right, box.Bottom),
                    text, conf, TextSource.Ocr));
        }
        // top-to-bottom, then left-to-right (crude; reading-order stage does the real work)
        return lines
            .OrderBy(l => Math.Round(l.Bounds.Y1 / 20.0))
            .ThenBy(l => l.Bounds.X1)
            .ToList();
    }

    // ── Detection (DB) ──────────────────────────────────────────────────────
    private List<SKRect> DetectTextBoxes(SKBitmap page)
    {
        int origW = page.Width, origH = page.Height;
        float scale = Math.Min(1f, (float)DetMaxSide / Math.Max(origW, origH));
        int w = Math.Max(32, (int)Math.Round(origW * scale / 32) * 32);
        int h = Math.Max(32, (int)Math.Round(origH * scale / 32) * 32);

        using var resized = page.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque),
                                        Sampling)
                            ?? throw new InvalidOperationException("resize failed");

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

        var inputName = _det.InputMetadata.Keys.First();
        using var results = _det.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
        var probMap = results[0].AsTensor<float>().ToArray();   // [1,1,h,w]

        // Connected components on binarized map (BFS, 4-connectivity)
        var visited = new bool[h * w];
        var boxes = new List<SKRect>();
        var queue = new Queue<int>();

        for (int start = 0; start < h * w; start++)
        {
            if (visited[start] || probMap[start] <= BinaryThreshold) continue;

            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            double scoreSum = 0; int count = 0;
            visited[start] = true;
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                int idx = queue.Dequeue();
                int cy = idx / w, cx = idx % w;
                scoreSum += probMap[idx]; count++;
                if (cx < minX) minX = cx; if (cx > maxX) maxX = cx;
                if (cy < minY) minY = cy; if (cy > maxY) maxY = cy;

                Visit(cx - 1, cy); Visit(cx + 1, cy); Visit(cx, cy - 1); Visit(cx, cy + 1);
            }

            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            if (bw < 3 || bh < 3) continue;
            if (scoreSum / count < BoxScoreThreshold) continue;

            // DB unclip approximation: expand bbox by offset = area*ratio/perimeter
            float offset = (float)(bw * bh) * UnclipRatio / (2 * (bw + bh));
            float sx = (float)origW / w, sy = (float)origH / h;

            boxes.Add(new SKRect(
                Math.Max(0, (minX - offset) * sx),
                Math.Max(0, (minY - offset) * sy),
                Math.Min(origW, (maxX + 1 + offset) * sx),
                Math.Min(origH, (maxY + 1 + offset) * sy)));

            continue;

            void Visit(int vx, int vy)
            {
                if (vx < 0 || vy < 0 || vx >= w || vy >= h) return;
                int vi = vy * w + vx;
                if (visited[vi] || probMap[vi] <= BinaryThreshold) return;
                visited[vi] = true;
                queue.Enqueue(vi);
            }
        }

        return boxes;
    }

    // ── Recognition entry: orientation handling, then the horizontal pipeline ──
    private (string Text, float Confidence) RecognizeLine(SKBitmap page, SKRect box)
    {
        int rawW = Math.Max(1, (int)box.Width);
        int rawH = Math.Max(1, (int)box.Height);

        using var raw = new SKBitmap(rawW, rawH, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(raw))
            canvas.DrawBitmap(page, box, new SKRect(0, 0, rawW, rawH));

        // Vertical text candidate (rotated headers, margin labels): recognize rotated.
        if (rawH > VerticalAspectThreshold * rawW && rawH >= 3 * _recHeight)
        {
            if (_orientation != null)
            {
                // Rotate 90° CW once; classifier says whether it landed upside-down.
                using var upright = Rotate(raw, 90);
                var (degrees, _) = _orientation.Classify(upright);
                if (degrees == 180)
                {
                    using var flipped = Rotate(upright, 180);
                    return RecognizeHorizontal(flipped);
                }
                return RecognizeHorizontal(upright);
            }

            // Model-free fallback: try both rotations, keep the higher-confidence reading.
            using var cw = Rotate(raw, 90);
            using var ccw = Rotate(raw, 270);
            var a = RecognizeHorizontal(cw);
            var b = RecognizeHorizontal(ccw);
            return a.Confidence >= b.Confidence ? a : b;
        }

        return RecognizeHorizontal(raw);
    }

    /// <summary>Rotates clockwise by 90, 180, or 270 degrees.</summary>
    internal static SKBitmap Rotate(SKBitmap source, int degrees)
    {
        bool swap = degrees is 90 or 270;
        int w = swap ? source.Height : source.Width;
        int h = swap ? source.Width : source.Height;

        var rotated = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(rotated);
        canvas.Clear(SKColors.White);
        canvas.Translate(w / 2f, h / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0);
        return rotated;
    }

    // ── Horizontal recognition pipeline (trim → ratio check → chunk → CTC) ──
    private (string Text, float Confidence) RecognizeHorizontal(SKBitmap raw)
    {
        using var crop = TrimVertical(raw);          // true glyph height + small margin
        int cropW = crop.Width, cropH = crop.Height;

        if (cropW <= MaxWidthHeightRatio * cropH)
            return RecognizeCrop(crop);

        var sb = new System.Text.StringBuilder();
        var confs = new List<float>();
        int pad = Math.Max(4, cropH / 3);            // white context margin per chunk

        foreach (var (start, width, spaceBefore) in SplitAtWhitespace(crop, MaxWidthHeightRatio * cropH))
        {
            using var piece = new SKBitmap(width + 2 * pad, cropH, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using (var canvas = new SKCanvas(piece))
            {
                canvas.Clear(SKColors.White);
                canvas.DrawBitmap(crop, SKRect.Create(start, 0, width, cropH),
                                  SKRect.Create(pad, 0, width, cropH));
            }
            var (t, c) = RecognizeCrop(piece);
            if (t.Length > 0)
            {
                if (sb.Length > 0 && spaceBefore) sb.Append(' ');
                sb.Append(t);
                confs.Add(c);
            }
        }
        return (sb.ToString(), confs.Count > 0 ? confs.Average() : 0f);
    }

    /// <summary>Crops away the vertical whitespace the DB unclip expansion added.</summary>
    private static SKBitmap TrimVertical(SKBitmap crop)
    {
        int w = crop.Width, h = crop.Height;
        var px = crop.Pixels;

        bool RowHasInk(int y)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
                if (Luma(px[row + x]) < 160) return true;
            return false;
        }

        int top = 0, bottom = h - 1;
        while (top < bottom && !RowHasInk(top)) top++;
        while (bottom > top && !RowHasInk(bottom)) bottom--;
        top = Math.Max(0, top - 3);
        bottom = Math.Min(h - 1, bottom + 3);

        int nh = Math.Max(8, bottom - top + 1);
        var trimmed = new SKBitmap(w, nh, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(trimmed);
        canvas.DrawBitmap(crop, SKRect.Create(0, top, w, nh), new SKRect(0, 0, w, nh));
        return trimmed;
    }

    /// <summary>Splits a wide line crop into (start,width,spaceBefore) chunks. A cut only
    /// counts as a word boundary (spaceBefore=true) when the whitespace run is wide enough
    /// relative to glyph height; otherwise it's a hard cut and chunks rejoin with no space.</summary>
    internal static List<(int Start, int Width, bool SpaceBefore)> SplitAtWhitespace(SKBitmap crop, int maxChunkWidth)
    {
        int w = crop.Width, h = crop.Height;
        var px = crop.Pixels;
        int minWordGap = Math.Max(4, h / 4);         // intra-word letter gaps are narrower

        var hasInk = new bool[w];
        for (int x = 0; x < w; x++)
        for (int y = 0; y < h; y++)
            if (Luma(px[y * w + x]) < 160) { hasInk[x] = true; break; }

        var chunks = new List<(int, int, bool)>();
        int segStart = 0;
        bool spaceBefore = false;
        while (w - segStart > maxChunkWidth)
        {
            int lo = segStart + maxChunkWidth / 2;
            int hi = Math.Min(segStart + maxChunkWidth, w - 1);

            // widest whitespace run in [lo, hi]
            int bestStart = -1, bestLen = 0, runStart = -1;
            for (int x = lo; x <= hi; x++)
            {
                if (!hasInk[x]) { if (runStart < 0) runStart = x; }
                else if (runStart >= 0)
                {
                    if (x - runStart > bestLen) { bestLen = x - runStart; bestStart = runStart; }
                    runStart = -1;
                }
            }
            if (runStart >= 0 && hi + 1 - runStart > bestLen)
            { bestLen = hi + 1 - runStart; bestStart = runStart; }

            bool isWordGap = bestLen >= minWordGap;
            int cut = bestLen >= 2 ? bestStart + bestLen / 2 : hi;

            chunks.Add((segStart, cut - segStart, spaceBefore));
            spaceBefore = isWordGap;
            segStart = cut;
        }
        chunks.Add((segStart, w - segStart, spaceBefore));
        return chunks;
    }

    private static double Luma(SKColor c) => c.Red * 0.299 + c.Green * 0.587 + c.Blue * 0.114;

    private (string Text, float Confidence) RecognizeCrop(SKBitmap raw)
    {
        // White horizontal margin: glyphs touching the crop edge get misread
        // ("11." → "9.", dropped leading characters). Context fixes it.
        int padX = Math.Max(4, raw.Height / 4);
        using var crop = new SKBitmap(raw.Width + 2 * padX, raw.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var c = new SKCanvas(crop))
        {
            c.Clear(SKColors.White);
            c.DrawBitmap(raw, padX, 0);
        }

        int cropW = crop.Width, cropH = crop.Height;
        int recW = Math.Clamp((int)Math.Round(cropW * (float)_recHeight / cropH), 16, 4096);
        using var resized = crop.Resize(new SKImageInfo(recW, _recHeight, SKColorType.Bgra8888, SKAlphaType.Opaque),
                                        Sampling)
                            ?? throw new InvalidOperationException("rec resize failed");

        var tensor = new DenseTensor<float>(new[] { 1, 3, _recHeight, recW });
        var px = resized.Pixels;
        for (int y = 0; y < _recHeight; y++)
        {
            int row = y * recW;
            for (int x = 0; x < recW; x++)
            {
                var c = px[row + x];
                tensor[0, 0, y, x] = (c.Red / 255f - 0.5f) / 0.5f;
                tensor[0, 1, y, x] = (c.Green / 255f - 0.5f) / 0.5f;
                tensor[0, 2, y, x] = (c.Blue / 255f - 0.5f) / 0.5f;
            }
        }

        var inputName = _rec.InputMetadata.Keys.First();
        using var results = _rec.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
        var output = results[0].AsTensor<float>();              // [1, T, C]
        int steps = output.Dimensions[1], classes = output.Dimensions[2];

        // CTC greedy decode: index 0 = blank; 1..dict = chars; last = space (if present)
        bool hasSpace = classes >= _dict.Length + 2;
        var chars = new List<char>();
        var confs = new List<float>();
        int prev = 0;

        for (int t = 0; t < steps; t++)
        {
            int best = 0; float bestP = float.MinValue;
            for (int c = 0; c < classes; c++)
            {
                float p = output[0, t, c];
                if (p > bestP) { bestP = p; best = c; }
            }

            if (best != 0 && best != prev)
            {
                if (hasSpace && best == classes - 1) chars.Add(' ');
                else if (best - 1 < _dict.Length && _dict[best - 1].Length > 0)
                    chars.Add(_dict[best - 1][0]);
                confs.Add(bestP);
            }
            prev = best;
        }

        var text = new string(chars.ToArray()).Trim();
        return (text, confs.Count > 0 ? confs.Average() : 0f);
    }

    public void Dispose()
    {
        _det.Dispose();
        _rec.Dispose();
        _orientation?.Dispose();
    }
}
