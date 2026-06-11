// Stage 2 — DocLayout-YOLO ONNX layout detection.
// Pre/post-processing ported from the model repo's reference inference.py
// (wybxc/DocLayout-YOLO-DocStructBench-onnx): BGR order, /255, letterbox pad 114,
// YOLOv10-style NMS-free output [N,6] = x1,y1,x2,y2,conf,cls.

using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Foliant.Spike;

public sealed record LayoutRegion(
    string Label,
    float Confidence,
    float X1, float Y1, float X2, float Y2)
{
    public float Width => X2 - X1;
    public float Height => Y2 - Y1;
}

public sealed class DocLayoutYoloDetector : IDisposable
{
    private const int TargetSize = 1024;   // export is imgsz1024
    private const int Stride = 32;
    private const float ConfidenceThreshold = 0.25f;

    // DocStructBench classes — fallback if ONNX metadata "names" is absent.
    private static readonly Dictionary<int, string> FallbackNames = new()
    {
        [0] = "title", [1] = "plain text", [2] = "abandon", [3] = "figure",
        [4] = "figure_caption", [5] = "table", [6] = "table_caption",
        [7] = "table_footnote", [8] = "isolate_formula", [9] = "formula_caption",
    };

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly bool _fixedInput;
    private readonly Dictionary<int, string> _names;

    public DocLayoutYoloDetector(string modelPath)
    {
        _session = new InferenceSession(modelPath);
        _inputName = _session.InputMetadata.Keys.First();
        var dims = _session.InputMetadata[_inputName].Dimensions;
        _fixedInput = dims.Length == 4 && dims[2] > 0 && dims[3] > 0;
        _names = ParseNamesFromMetadata() ?? FallbackNames;
    }

    public IReadOnlyList<LayoutRegion> Detect(SKBitmap page)
    {
        int origW = page.Width, origH = page.Height;

        // ── Letterbox ────────────────────────────────────────────────────────
        float gain = Math.Min((float)TargetSize / origH, (float)TargetSize / origW);
        int resizedW = (int)Math.Round(origW * gain);
        int resizedH = (int)Math.Round(origH * gain);

        int inW, inH;
        if (_fixedInput)
        {
            inW = TargetSize; inH = TargetSize;          // full square pad
        }
        else
        {
            // reference impl pads only to the next stride multiple
            inW = resizedW + (TargetSize - resizedW) % Stride;
            inH = resizedH + (TargetSize - resizedH) % Stride;
        }
        int padLeft = (inW - resizedW) / 2;
        int padTop = (inH - resizedH) / 2;

        using var input = new SKBitmap(inW, inH, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(input))
        {
            canvas.Clear(new SKColor(114, 114, 114));
            var dest = new SKRect(padLeft, padTop, padLeft + resizedW, padTop + resizedH);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium };
            canvas.DrawBitmap(page, dest, paint);
        }

        // ── To tensor: BCHW float32, BGR order, [0,1] ───────────────────────
        var tensor = new DenseTensor<float>(new[] { 1, 3, inH, inW });
        var pixels = input.Pixels;
        for (int y = 0; y < inH; y++)
        {
            int row = y * inW;
            for (int x = 0; x < inW; x++)
            {
                var c = pixels[row + x];
                tensor[0, 0, y, x] = c.Blue / 255f;
                tensor[0, 1, y, x] = c.Green / 255f;
                tensor[0, 2, y, x] = c.Red / 255f;
            }
        }

        // ── Inference ───────────────────────────────────────────────────────
        using var results = _session.Run(
            new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
        var output = results[0].AsTensor<float>();

        // Output: [1,N,6] or [N,6] — x1,y1,x2,y2,conf,cls (NMS-free, YOLOv10)
        var data = output.ToArray();
        int cols = output.Dimensions[^1];
        int rows = data.Length / cols;

        var regions = new List<LayoutRegion>();
        for (int i = 0; i < rows; i++)
        {
            float conf = data[i * cols + 4];
            if (conf <= ConfidenceThreshold) continue;

            int cls = (int)data[i * cols + 5];
            float x1 = Clamp((data[i * cols + 0] - padLeft) / gain, 0, origW);
            float y1 = Clamp((data[i * cols + 1] - padTop) / gain, 0, origH);
            float x2 = Clamp((data[i * cols + 2] - padLeft) / gain, 0, origW);
            float y2 = Clamp((data[i * cols + 3] - padTop) / gain, 0, origH);

            regions.Add(new LayoutRegion(
                _names.TryGetValue(cls, out var name) ? name : $"class_{cls}",
                conf, x1, y1, x2, y2));
        }

        return regions.OrderByDescending(r => r.Confidence).ToList();
    }

    public static void DrawOverlay(SKBitmap page, IReadOnlyList<LayoutRegion> regions, string outputPath)
    {
        using var annotated = page.Copy();
        using var canvas = new SKCanvas(annotated);
        using var textPaint = new SKPaint
        {
            TextSize = 28, IsAntialias = true, Color = SKColors.White,
        };

        foreach (var (region, i) in regions.Select((r, i) => (r, i)))
        {
            var color = ColorFor(region.Label);
            using var boxPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke, StrokeWidth = 4, Color = color, IsAntialias = true,
            };
            canvas.DrawRect(region.X1, region.Y1, region.Width, region.Height, boxPaint);

            var label = $"{i}:{region.Label} {region.Confidence:0.00}";
            float tw = textPaint.MeasureText(label);
            using var bgPaint = new SKPaint { Color = color, Style = SKPaintStyle.Fill };
            canvas.DrawRect(region.X1, Math.Max(0, region.Y1 - 34), tw + 12, 34, bgPaint);
            canvas.DrawText(label, region.X1 + 6, Math.Max(28, region.Y1 - 8), textPaint);
        }

        using var image = SKImage.FromBitmap(annotated);
        using var png = image.Encode(SKEncodedImageFormat.Png, 90);
        using var file = File.Create(outputPath);
        png.SaveTo(file);
    }

    private static SKColor ColorFor(string label) => label switch
    {
        "title" => new SKColor(0xE6, 0x39, 0x46),      // red
        "plain text" => new SKColor(0x45, 0x7B, 0x9D), // blue
        "table" => new SKColor(0x2A, 0x9D, 0x8F),      // teal
        "figure" => new SKColor(0xF4, 0xA2, 0x61),     // orange
        "abandon" => new SKColor(0x8D, 0x99, 0xAE),    // gray (headers/footers)
        _ => new SKColor(0x9B, 0x5D, 0xE5),            // purple
    };

    private static float Clamp(float v, float min, float max) => Math.Min(Math.Max(v, min), max);

    private Dictionary<int, string>? ParseNamesFromMetadata()
    {
        if (!_session.ModelMetadata.CustomMetadataMap.TryGetValue("names", out var raw))
            return null;
        var parsed = new Dictionary<int, string>();
        foreach (Match m in Regex.Matches(raw, @"(\d+):\s*'([^']+)'"))
            parsed[int.Parse(m.Groups[1].Value)] = m.Groups[2].Value;
        return parsed.Count > 0 ? parsed : null;
    }

    public void Dispose() => _session.Dispose();
}
