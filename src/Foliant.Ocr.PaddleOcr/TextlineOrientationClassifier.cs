// Optional PaddleOCR textline-orientation classifier (ONNX).
// Adapts to the loaded model: input H/W read from model metadata; output classes 2 ({0°,180°})
// or 4 ({0°,90°,180°,270°}). Normalization is the PaddleOCR cls standard: (x/255 - 0.5)/0.5, BGR? —
// the cls models are trained RGB-agnostic in practice; we feed RGB to match RapidOCR's ONNX port.

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Foliant.Ocr.PaddleOcr;

public sealed class TextlineOrientationClassifier : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _height;
    private readonly int _width;

    public TextlineOrientationClassifier(string modelPath)
    {
        _session = new InferenceSession(modelPath);
        _inputName = _session.InputMetadata.Keys.First();
        var dims = _session.InputMetadata[_inputName].Dimensions;
        // PP-LCNet_x1_0_textline_ori exports with dynamic dims; its training size is 80×160.
        _height = dims.Length == 4 && dims[2] > 0 ? dims[2] : 80;
        _width = dims.Length == 4 && dims[3] > 0 ? dims[3] : 160;
    }

    /// <summary>
    /// Classifies the rotation of a (nominally horizontal) text-line crop.
    /// Returns degrees clockwise to apply to make the text upright: 0 or 180
    /// (or 90/270 when the model has 4 classes), with the winning probability.
    /// </summary>
    public (int Degrees, float Probability) Classify(SKBitmap lineCrop)
    {
        // Resize keeping aspect, pad right with black (PaddleOCR cls convention)
        float scale = Math.Min((float)_width / lineCrop.Width, (float)_height / lineCrop.Height);
        int w = Math.Max(1, Math.Min(_width, (int)Math.Round(lineCrop.Width * scale)));
        int h = Math.Max(1, Math.Min(_height, (int)Math.Round(lineCrop.Height * scale)));

        using var input = new SKBitmap(_width, _height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(input))
        {
            canvas.Clear(SKColors.Black);
            using var cropImage = SKImage.FromBitmap(lineCrop);
            canvas.DrawImage(cropImage, new SKRect(0, 0, w, h),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }

        var tensor = new DenseTensor<float>(new[] { 1, 3, _height, _width });
        var px = input.Pixels;
        for (int y = 0; y < _height; y++)
        {
            int row = y * _width;
            for (int x = 0; x < _width; x++)
            {
                var c = px[row + x];
                tensor[0, 0, y, x] = (c.Red / 255f - 0.5f) / 0.5f;
                tensor[0, 1, y, x] = (c.Green / 255f - 0.5f) / 0.5f;
                tensor[0, 2, y, x] = (c.Blue / 255f - 0.5f) / 0.5f;
            }
        }

        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, tensor) });
        var output = results[0].AsTensor<float>();   // [1, C] probabilities (model has softmax)
        int classes = output.Dimensions[^1];

        int best = 0;
        float bestP = float.MinValue;
        for (int c = 0; c < classes; c++)
        {
            float p = output[0, c];
            if (p > bestP) { bestP = p; best = c; }
        }

        int degrees = classes switch
        {
            2 => best * 180,                         // {0, 180}
            4 => best * 90,                          // {0, 90, 180, 270}
            _ => 0,
        };
        return (degrees, bestP);
    }

    public void Dispose() => _session.Dispose();
}
