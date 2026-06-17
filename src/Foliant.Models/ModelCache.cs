using System.Security.Cryptography;

namespace Foliant.Models;

/// <summary>
/// Local cache for ONNX model assets: downloads on first use, verifies SHA-256, and marks
/// verified files so subsequent calls are a cheap existence check.
///
/// Cache directory resolution order:
///  1. explicit constructor argument,
///  2. FOLIANT_MODELS_DIR environment variable,
///  3. {LocalApplicationData}/Foliant/models.
/// </summary>
public sealed class ModelCache
{
    private const int DownloadAttempts = 3;
    private static readonly HttpClient DefaultHttp = new() { Timeout = TimeSpan.FromMinutes(30) };

    private readonly HttpClient _http;

    public ModelCache(string? cacheDirectory = null, HttpClient? httpClient = null)
    {
        CacheDirectory = cacheDirectory
            ?? Environment.GetEnvironmentVariable("FOLIANT_MODELS_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Foliant", "models");
        _http = httpClient ?? DefaultHttp;
    }

    public string CacheDirectory { get; }

    /// <summary>
    /// Returns the local path of <paramref name="asset"/>, downloading and verifying it first
    /// when not already cached.
    /// </summary>
    public async Task<string> GetPathAsync(
        ModelAsset asset, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        Directory.CreateDirectory(CacheDirectory);

        string path = Path.Combine(CacheDirectory, asset.FileName);
        string marker = path + ".sha256-ok";

        if (File.Exists(path))
        {
            if (File.Exists(marker)) return path;

            // Pre-existing file without marker (e.g. placed by download-models.sh): verify once.
            if (await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false) == asset.Sha256)
            {
                File.WriteAllText(marker, asset.Sha256);
                return path;
            }
            File.Delete(path);   // corrupt/stale — re-download below
        }

        Exception? last = null;
        for (int attempt = 1; attempt <= DownloadAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DownloadAsync(asset, path, progress, cancellationToken).ConfigureAwait(false);

                string actual = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
                if (actual != asset.Sha256)
                {
                    File.Delete(path);
                    throw new InvalidDataException(
                        $"Checksum mismatch for {asset.Id}: expected {asset.Sha256}, got {actual}.");
                }

                File.WriteAllText(marker, asset.Sha256);
                return path;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
            {
                last = ex;
                if (attempt < DownloadAttempts)
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken)
                        .ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"Failed to download model '{asset.Id}' after {DownloadAttempts} attempts.", last);
    }

    /// <summary>Ensures every asset is cached; returns id → local path.</summary>
    public async Task<IReadOnlyDictionary<string, string>> GetPathsAsync(
        IEnumerable<ModelAsset> assets, IProgress<(string Id, double Fraction)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>();
        foreach (var asset in assets)
        {
            var perAsset = progress is null
                ? null
                : new Progress<double>(f => progress.Report((asset.Id, f)));
            result[asset.Id] = await GetPathAsync(asset, perAsset, cancellationToken).ConfigureAwait(false);
        }
        return result;
    }

    private async Task DownloadAsync(
        ModelAsset asset, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        string tmp = destination + ".download";
        try
        {
            using var response = await _http
                .GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? asset.SizeBytes;
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = File.Create(tmp))
            {
                var buffer = new byte[1 << 16];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    copied += read;
                    if (total > 0) progress?.Report((double)copied / total);
                }
            }

            File.Move(tmp, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    internal static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
