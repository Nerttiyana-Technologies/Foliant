// ML super-resolution IScanUpscaler (ONNX, local via ONNX Runtime). Sharpens low-DPI scanned pages
// before OCR with a Real-ESRGAN-class model. Off by default; measured on Gate 8 before promotion.
//
// MODEL CONTRACT (verified against sr_real_esrgan_x2 = 64×64→128×128 and real_esrgan_x4plus = 128×128→512×512):
//   input : float32 NCHW [b,3,Hin,Win], RGB, normalized to [0,1]. Hin/Win are usually FIXED (e.g. 64 or 128).
//   output: float32 NCHW [b,3,Hin*scale,Win*scale], RGB, ~[0,1] (can slightly over/undershoot → clamped).
// The page is processed in tiles of the model's exact input size; partial edge tiles are edge-replicated to
// fill the tile and the valid region is cropped back out on stitch. Scale (x2/x4) is read from a dry run.
// If a model exposes DYNAMIC input dims, the constructor's fallbackTile is used instead.
//
// PERF NOTE: small fixed tiles (64×64) mean many inferences on a large raster — fine for the Gate-8
// measurement (degraded rasters are small). If super-res proves out, batch tiles (the models accept a
// dynamic batch dim) and/or upscale before the full-DPI render for production throughput.

using Foliant.Internal;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Foliant.ScanUpscale.SuperResolution;

public sealed class OnnxSuperResolutionUpscaler : IScanUpscaler, IDisposable
{
    private static readonly SKSamplingOptions Cubic = new(SKCubicResampler.CatmullRom);

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _tileW;
    private readonly int _tileH;
    private readonly int _scale;   // model's intrinsic upscale factor (2/3/4), detected from a dry run

    /// <param name="modelPath">Real-ESRGAN-class super-resolution ONNX model.</param>
    /// <param name="fallbackTile">Tile edge to use only if the model exposes dynamic input dims.</param>
    public OnnxSuperResolutionUpscaler(string modelPath, int fallbackTile = 128)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Super-resolution model not found.", modelPath);

        _session = new InferenceSession(modelPath);
        _inputName = _session.InputMetadata.Keys.First();

        var dims = _session.InputMetadata[_inputName].Dimensions;   // [b,3,Hin,Win]; dynamic dims are -1
        _tileH = dims.Length == 4 && dims[2] > 0 ? dims[2] : fallbackTile;
        _tileW = dims.Length == 4 && dims[3] > 0 ? dims[3] : fallbackTile;
        _scale = DetectScale();
    }

    public PageImage Upscale(PageImage image, float factor)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (factor <= 1f) return image;

        // 1) Super-resolve to the model's native scale (tiled at the model's fixed input size).
        int sw = image.Width * _scale, sh = image.Height * _scale;
        var srBgra = new byte[(long)sw * sh * 4];
        RunTiled(image, srBgra, sw);

        // 2) Resize the native-scale result to the requested factor (no-op when they already match).
        int targetW = (int)Math.Round(image.Width * (double)factor);
        int targetH = (int)Math.Round(image.Height * (double)factor);
        var native = new PageImage(sw, sh, image.Dpi, srBgra);
        if (sw == targetW && sh == targetH) return native;

        using var bmp = SkiaInterop.ToBitmap(native);
        using var dst = bmp.Resize(new SKImageInfo(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Opaque), Cubic)
            ?? throw new InvalidOperationException("Super-resolution resize failed.");
        return SkiaInterop.ToPageImage(dst, image.Dpi);
    }

    // Non-overlapping fixed-size tiles; partial edge tiles are filled by edge-replication and the valid
    // region is cropped on stitch. Output indices use the tile's top-left valid block.
    private void RunTiled(PageImage image, byte[] outBgra, int outStridePx)
    {
        byte[] src = image.PixelsBgra8888;
        int W = image.Width, H = image.Height, srcStride = W * 4;

        for (int ty = 0; ty < H; ty += _tileH)
        for (int tx = 0; tx < W; tx += _tileW)
        {
            int coreH = Math.Min(_tileH, H - ty), coreW = Math.Min(_tileW, W - tx);

            var input = new DenseTensor<float>(new[] { 1, 3, _tileH, _tileW });
            for (int y = 0; y < _tileH; y++)
            {
                int sy = ty + Math.Min(y, coreH - 1);          // edge-replicate beyond the valid region
                int srow = sy * srcStride;
                for (int x = 0; x < _tileW; x++)
                {
                    int sx = tx + Math.Min(x, coreW - 1);
                    int p = srow + sx * 4;                      // BGRA
                    input[0, 0, y, x] = src[p + 2] / 255f;      // R
                    input[0, 1, y, x] = src[p + 1] / 255f;      // G
                    input[0, 2, y, x] = src[p + 0] / 255f;      // B
                }
            }

            using var results = _session.Run(
                new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) });
            var outT = results.First().AsTensor<float>();       // [1,3,_tileH*scale,_tileW*scale]

            for (int y = 0; y < coreH * _scale; y++)
            {
                int outRow = (ty * _scale + y) * outStridePx * 4 + (tx * _scale) * 4;
                for (int x = 0; x < coreW * _scale; x++)
                {
                    int o = outRow + x * 4;
                    outBgra[o + 0] = ToByte(outT[0, 2, y, x]);  // B
                    outBgra[o + 1] = ToByte(outT[0, 1, y, x]);  // G
                    outBgra[o + 2] = ToByte(outT[0, 0, y, x]);  // R
                    outBgra[o + 3] = 255;
                }
            }
        }
    }

    // Dry run at the model's fixed input size to learn its intrinsic scale (output edge / input edge).
    private int DetectScale()
    {
        var input = new DenseTensor<float>(new[] { 1, 3, _tileH, _tileW });
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) });
        int outEdge = results.First().AsTensor<float>().Dimensions[^1];
        int scale = Math.Max(1, outEdge / _tileW);
        if (scale is not (2 or 3 or 4))
            throw new InvalidOperationException(
                $"Unexpected super-resolution scale {scale} (output edge {outEdge} for input {_tileW}). " +
                "Expected a x2/x3/x4 Real-ESRGAN-style model.");
        return scale;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    public void Dispose() => _session.Dispose();
}
