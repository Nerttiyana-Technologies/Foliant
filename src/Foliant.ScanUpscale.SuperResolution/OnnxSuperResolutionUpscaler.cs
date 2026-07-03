// ML super-resolution IScanUpscaler (ONNX, local via ONNX Runtime). Sharpens low-DPI scanned pages
// before OCR with a Real-ESRGAN-class model. In the shipped pipeline it serves the ADR-0004
// RETRY-ONLY role (pages whose first OCR pass produced ~nothing), where recovery can only add
// words; the always-on UpscaleLowResolutionScans path keeps its Gate 8 verdict and stays off.
//
// MODEL CONTRACT (verified against sr_real_esrgan_x2 = 64×64→128×128 and real_esrgan_x4plus = 128×128→512×512):
//   input : float32 NCHW [b,3,Hin,Win], RGB, normalized to [0,1]. Hin/Win are usually FIXED (e.g. 64 or 128).
//   output: float32 NCHW [b,3,Hin*scale,Win*scale], RGB, ~[0,1] (can slightly over/undershoot → clamped).
// The page is processed in tiles of the model's exact input size; partial edge tiles are edge-replicated to
// fill the tile and the valid region is cropped back out on stitch. Scale (x2/x4) is read from a dry run.
// If a model exposes DYNAMIC input dims, the constructor's FallbackTile is used instead.
//
// EXECUTION PROVIDERS: CPU by default. GPU hosts (the intended production home — e.g. a GB10-class
// AI workstation) opt in via SuperResolutionOptions.UseCuda; the HOST application must reference
// Microsoft.ML.OnnxRuntime.Gpu (same version as this package's Microsoft.ML.OnnxRuntime) so the
// CUDA native provider is present at runtime. CUDA failures throw with an actionable message
// rather than silently falling back to CPU — a mis-configured GPU box should be loud, not slow.
//
// THROUGHPUT: models with a DYNAMIC batch dimension get tile batching ([N,3,h,w] per inference,
// N = TileBatchSize) — the difference between per-tile latency × hundreds and a few large GPU
// calls. Fixed-batch exports (b=1, e.g. Qualcomm AI Hub exports) fall back to per-tile inference.

using Foliant.Internal;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace Foliant.ScanUpscale.SuperResolution;

/// <summary>Construction options for <see cref="OnnxSuperResolutionUpscaler"/>.</summary>
public sealed record SuperResolutionOptions
{
    /// <summary>
    /// Run inference on the CUDA execution provider. The host application must reference
    /// <c>Microsoft.ML.OnnxRuntime.Gpu</c> (matching this package's ONNX Runtime version) and have
    /// a compatible CUDA/cuDNN installed. Construction THROWS if CUDA cannot be initialized —
    /// no silent CPU fallback, so a misconfigured GPU host fails loudly instead of running slow.
    /// </summary>
    public bool UseCuda { get; init; }

    /// <summary>CUDA device ordinal when <see cref="UseCuda"/> is on.</summary>
    public int CudaDeviceId { get; init; }

    /// <summary>Tile edge (px) used only when the model exposes dynamic spatial dims.</summary>
    public int FallbackTile { get; init; } = 128;

    /// <summary>
    /// Tiles per inference when the model has a DYNAMIC batch dimension (ignored for fixed-batch
    /// exports). 8 is a safe CPU default; GPU hosts can raise it (VRAM permitting) — batching is
    /// where the GPU throughput lives.
    /// </summary>
    public int TileBatchSize { get; init; } = 8;
}

public sealed class OnnxSuperResolutionUpscaler : IScanUpscaler, IDisposable
{
    private static readonly SKSamplingOptions Cubic = new(SKCubicResampler.CatmullRom);

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly int _tileW;
    private readonly int _tileH;
    private readonly int _scale;        // model's intrinsic upscale factor (2/3/4), detected from a dry run
    private readonly bool _dynamicBatch;
    private readonly int _batchSize;

    /// <param name="modelPath">Real-ESRGAN-class super-resolution ONNX model.</param>
    /// <param name="fallbackTile">Tile edge to use only if the model exposes dynamic input dims.</param>
    public OnnxSuperResolutionUpscaler(string modelPath, int fallbackTile = 128)
        : this(modelPath, new SuperResolutionOptions { FallbackTile = fallbackTile }) { }

    /// <param name="modelPath">Real-ESRGAN-class super-resolution ONNX model.</param>
    /// <param name="options">Execution options (CUDA, tiling, batching).</param>
    public OnnxSuperResolutionUpscaler(string modelPath, SuperResolutionOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Super-resolution model not found.", modelPath);

        SessionOptions sessionOptions;
        if (options.UseCuda)
        {
            try
            {
                sessionOptions = SessionOptions.MakeSessionOptionWithCudaProvider(options.CudaDeviceId);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "CUDA execution provider could not be initialized. The host application must " +
                    "reference Microsoft.ML.OnnxRuntime.Gpu (same version as Microsoft.ML.OnnxRuntime " +
                    "used by Foliant.ScanUpscale.SuperResolution) and have a compatible CUDA/cuDNN " +
                    "installed. To run on CPU instead, construct with UseCuda = false.", ex);
            }
        }
        else
        {
            sessionOptions = new SessionOptions();
        }

        _session = new InferenceSession(modelPath, sessionOptions);
        _inputName = _session.InputMetadata.Keys.First();

        var dims = _session.InputMetadata[_inputName].Dimensions;   // [b,3,Hin,Win]; dynamic dims are -1
        _tileH = dims.Length == 4 && dims[2] > 0 ? dims[2] : options.FallbackTile;
        _tileW = dims.Length == 4 && dims[3] > 0 ? dims[3] : options.FallbackTile;
        _dynamicBatch = dims.Length == 4 && dims[0] <= 0;
        _batchSize = _dynamicBatch ? Math.Max(1, options.TileBatchSize) : 1;
        _scale = DetectScale();
    }

    /// <summary>True when the loaded model supports tile batching (dynamic batch dimension).</summary>
    public bool SupportsBatching => _dynamicBatch;

    // Largest native-scale intermediate we will materialize (bytes). A 4×-native model applied to
    // an already-large raster (e.g. a 600-DPI re-render) would need a >2GB buffer — past the CLR
    // array limit. Above this, super-resolution cannot help anyway (the raster is not
    // detail-starved), so fall back to a classical Catmull-Rom resize instead of throwing.
    private const long MaxNativeBufferBytes = 1L << 30;   // 1 GiB

    public PageImage Upscale(PageImage image, float factor)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (factor <= 1f) return image;

        // 1) Super-resolve to the model's native scale (tiled at the model's fixed input size).
        int sw = image.Width * _scale, sh = image.Height * _scale;
        if ((long)sw * sh * 4 > MaxNativeBufferBytes)
            return ClassicalResize(image, factor);
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
    // region is cropped on stitch. Tiles are batched into [N,3,h,w] when the model allows it.
    private void RunTiled(PageImage image, byte[] outBgra, int outStridePx)
    {
        int W = image.Width, H = image.Height;

        // Tile origins in scan order.
        var origins = new List<(int X, int Y)>();
        for (int ty = 0; ty < H; ty += _tileH)
            for (int tx = 0; tx < W; tx += _tileW)
                origins.Add((tx, ty));

        for (int start = 0; start < origins.Count; start += _batchSize)
        {
            int n = Math.Min(_batchSize, origins.Count - start);
            var input = new DenseTensor<float>(new[] { n, 3, _tileH, _tileW });
            for (int b = 0; b < n; b++)
                FillTile(image, origins[start + b], input, b);

            using var results = _session.Run(
                new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) });
            var outT = results.First().AsTensor<float>();       // [n,3,_tileH*scale,_tileW*scale]

            for (int b = 0; b < n; b++)
                StitchTile(outT, b, origins[start + b], image, outBgra, outStridePx);
        }
    }

    private void FillTile(PageImage image, (int X, int Y) origin, DenseTensor<float> input, int batchIndex)
    {
        byte[] src = image.PixelsBgra8888;
        int W = image.Width, H = image.Height, srcStride = W * 4;
        int coreH = Math.Min(_tileH, H - origin.Y), coreW = Math.Min(_tileW, W - origin.X);

        for (int y = 0; y < _tileH; y++)
        {
            int sy = origin.Y + Math.Min(y, coreH - 1);         // edge-replicate beyond the valid region
            int srow = sy * srcStride;
            for (int x = 0; x < _tileW; x++)
            {
                int sx = origin.X + Math.Min(x, coreW - 1);
                int p = srow + sx * 4;                          // BGRA
                input[batchIndex, 0, y, x] = src[p + 2] / 255f; // R
                input[batchIndex, 1, y, x] = src[p + 1] / 255f; // G
                input[batchIndex, 2, y, x] = src[p + 0] / 255f; // B
            }
        }
    }

    private void StitchTile(
        Tensor<float> outT, int batchIndex, (int X, int Y) origin,
        PageImage image, byte[] outBgra, int outStridePx)
    {
        int coreH = Math.Min(_tileH, image.Height - origin.Y);
        int coreW = Math.Min(_tileW, image.Width - origin.X);

        for (int y = 0; y < coreH * _scale; y++)
        {
            int outRow = (origin.Y * _scale + y) * outStridePx * 4 + (origin.X * _scale) * 4;
            for (int x = 0; x < coreW * _scale; x++)
            {
                int o = outRow + x * 4;
                outBgra[o + 0] = ToByte(outT[batchIndex, 2, y, x]);  // B
                outBgra[o + 1] = ToByte(outT[batchIndex, 1, y, x]);  // G
                outBgra[o + 2] = ToByte(outT[batchIndex, 0, y, x]);  // R
                outBgra[o + 3] = 255;
            }
        }
    }

    // Oversized-raster fallback: plain high-quality resample, no model.
    private static PageImage ClassicalResize(PageImage image, float factor)
    {
        int targetW = (int)Math.Round(image.Width * (double)factor);
        int targetH = (int)Math.Round(image.Height * (double)factor);
        using var bmp = SkiaInterop.ToBitmap(image);
        using var dst = bmp.Resize(new SKImageInfo(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Opaque), Cubic)
            ?? throw new InvalidOperationException("Fallback resize failed.");
        return SkiaInterop.ToPageImage(dst, image.Dpi);
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
