// DocLayout-YOLO ONNX layout detection.
// Pre/post-processing follows the model repo's reference inference.py
// (wybxc/DocLayout-YOLO-DocStructBench-onnx): BGR order, /255, letterbox pad 114,
// YOLOv10-style NMS-free output [N,6] = x1,y1,x2,y2,conf,cls.
// Ported from the Phase 0 spike, which validated it across a 474-page corpus.

using System.Text.RegularExpressions;
using Foliant.Internal;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Foliant.Layout.DocLayoutNet;

public sealed class DocLayoutNetDetector : ILayoutDetector
{
    private const int TargetSize = 1024;   // export is imgsz1024
    private const int Stride = 32;

    // DocStructBench classes — fallback if ONNX metadata "names" is absent.
    private static readonly Dictionary<int, string> FallbackNames = new()
    {
        [0] = "title", [1] = "plain text", [2] = "abandon", [3] = "figure",
        [4] = "figure_caption", [5] = "table", [6] = "table_caption",
        [7] = "table_footnote", [8] = "isolate_formula", [9] = "formula_caption",
    };

    // ≈ legacy SKFilterQuality.Medium (bilinear + mipmaps)
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly bool _fixedInput;
    private readonly Dictionary<int, string> _names;
    private readonly float _confidenceThreshold;

    public DocLayoutNetDetector(string modelPath, float confidenceThreshold = 0.25f)
    {
        _session = new InferenceSession(modelPath);
        _inputName = _session.InputMetadata.Keys.First();
        var dims = _session.InputMetadata[_inputName].Dimensions;
        _fixedInput = dims.Length == 4 && dims[2] > 0 && dims[3] > 0;
        _names = ParseNamesFromMetadata() ?? FallbackNames;
        _confidenceThreshold = confidenceThreshold;
    }

    /// <summary>Maps a DocStructBench label to the normalized <see cref="RegionType"/>.</summary>
    public static RegionType MapLabel(string label) => label switch
    {
        "title" => RegionType.Title,
        "plain text" => RegionType.Text,
        "abandon" => RegionType.PageFurniture,
        "figure" => RegionType.Figure,
        "figure_caption" or "table_caption" or "formula_caption" => RegionType.Caption,
        "table" => RegionType.Table,
        "table_footnote" => RegionType.Footnote,
        "isolate_formula" => RegionType.Formula,
        _ => RegionType.Unknown,
    };

    public IReadOnlyList<LayoutRegion> Detect(PageImage page)
    {
        using var bitmap = SkiaInterop.ToBitmap(page);
        return Detect(bitmap);
    }

    private IReadOnlyList<LayoutRegion> Detect(SKBitmap page)
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
            using var pageImage = SKImage.FromBitmap(page);
            canvas.DrawImage(pageImage, dest, Sampling);
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
            if (conf <= _confidenceThreshold) continue;

            int cls = (int)data[i * cols + 5];
            float x1 = Clamp((data[i * cols + 0] - padLeft) / gain, 0, origW);
            float y1 = Clamp((data[i * cols + 1] - padTop) / gain, 0, origH);
            float x2 = Clamp((data[i * cols + 2] - padLeft) / gain, 0, origW);
            float y2 = Clamp((data[i * cols + 3] - padTop) / gain, 0, origH);

            string label = _names.TryGetValue(cls, out var name) ? name : $"class_{cls}";
            regions.Add(new LayoutRegion(
                MapLabel(label), label, conf, new BoundingBox(x1, y1, x2, y2)));
        }

        return regions.OrderByDescending(r => r.Confidence).ToList();
    }

    /// <summary>Writes a debug PNG with region boxes drawn over the page.</summary>
    public static void DrawOverlay(PageImage page, IReadOnlyList<LayoutRegion> regions, string outputPath)
    {
        using var bitmap = SkiaInterop.ToBitmap(page);
        using var canvas = new SKCanvas(bitmap);
        using var font = new SKFont { Size = 28 };
        using var textPaint = new SKPaint
        {
            IsAntialias = true, Color = SKColors.White,
        };

        foreach (var (region, i) in regions.Select((r, i) => (r, i)))
        {
            var color = ColorFor(region.RawLabel);
            using var boxPaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke, StrokeWidth = 4, Color = color, IsAntialias = true,
            };
            var b = region.Bounds;
            canvas.DrawRect(b.X1, b.Y1, b.Width, b.Height, boxPaint);

            var label = $"{i}:{region.RawLabel} {region.Confidence:0.00}";
            float tw = font.MeasureText(label);
            using var bgPaint = new SKPaint { Color = color, Style = SKPaintStyle.Fill };
            canvas.DrawRect(b.X1, Math.Max(0, b.Y1 - 34), tw + 12, 34, bgPaint);
            canvas.DrawText(label, b.X1 + 6, Math.Max(28, b.Y1 - 8), SKTextAlign.Left, font, textPaint);
        }

        using var image = SKImage.FromBitmap(bitmap);
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
