// One-line construction of the ML super-resolution upscaler from the model catalog.

using Foliant.Models;

namespace Foliant.ScanUpscale.SuperResolution;

/// <summary>
/// Factory for <see cref="OnnxSuperResolutionUpscaler"/> backed by the cataloged
/// Real-ESRGAN x4plus model (BSD-3-Clause), downloaded into the local model cache
/// (SHA-256 verified) on first use. Typical wiring — CPU host:
/// <code>
/// var sr = await SuperResolutionScanUpscale.CreateDefaultAsync();
/// using var processor = FoliantProcessor.CreateDefault(modelsDir, scanUpscaler: sr);
/// </code>
/// GPU host (e.g. a GB10-class AI workstation; the application must also reference
/// <c>Microsoft.ML.OnnxRuntime.Gpu</c>):
/// <code>
/// var sr = await SuperResolutionScanUpscale.CreateDefaultAsync(
///     new SuperResolutionOptions { UseCuda = true });
/// </code>
/// The upscaler serves the low-resolution RETRY role (<c>RetryLowResolutionPages</c>, on by
/// default): it runs only on scanned pages whose first OCR pass produced ~nothing, where it can
/// only add words. The always-on <c>UpscaleLowResolutionScans</c> path remains off by default.
/// </summary>
public static class SuperResolutionScanUpscale
{
    /// <summary>
    /// Downloads (first use only) and constructs the default Real-ESRGAN x4plus upscaler.
    /// </summary>
    /// <param name="options">Execution options; null = CPU defaults.</param>
    /// <param name="cacheDirectory">Model cache override; null = ModelCache resolution order.</param>
    public static async Task<OnnxSuperResolutionUpscaler> CreateDefaultAsync(
        SuperResolutionOptions? options = null,
        string? cacheDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var cache = new ModelCache(cacheDirectory);
        // The graph references its external-weights file BY NAME, so both must land in the same
        // directory under their catalog file names — GetPathAsync guarantees exactly that.
        string modelPath = await cache.GetPathAsync(
            ModelCatalog.SuperResolution, cancellationToken: cancellationToken).ConfigureAwait(false);
        await cache.GetPathAsync(
            ModelCatalog.SuperResolutionData, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new OnnxSuperResolutionUpscaler(modelPath, options ?? new SuperResolutionOptions());
    }
}
