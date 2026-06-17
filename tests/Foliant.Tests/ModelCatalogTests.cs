using Foliant.Models;
using Xunit;

namespace Foliant.Tests;

public class ModelCatalogTests
{
    [Fact]
    public void AllAssets_HaveWellFormedMetadata()
    {
        foreach (var asset in ModelCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(asset.Id));
            Assert.False(string.IsNullOrWhiteSpace(asset.FileName));
            Assert.StartsWith("https://huggingface.co/", asset.Url);
            Assert.Matches("^[0-9a-f]{64}$", asset.Sha256);
            Assert.True(asset.SizeBytes > 0);
        }
    }

    [Fact]
    public void Ids_AndFileNames_AreUnique()
    {
        Assert.Equal(ModelCatalog.All.Count, ModelCatalog.All.Select(a => a.Id).Distinct().Count());
        Assert.Equal(ModelCatalog.All.Count, ModelCatalog.All.Select(a => a.FileName).Distinct().Count());
    }

    [Fact]
    public void DefaultPipeline_IsSubsetOfAll()
    {
        Assert.All(ModelCatalog.DefaultPipeline, a => Assert.Contains(a, ModelCatalog.All));
    }

    [Fact]
    public async Task ModelCache_RejectsChecksumMismatch()
    {
        string dir = Path.Combine(Path.GetTempPath(), "foliant-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            // Pre-place a file with wrong contents; cache must not accept it silently.
            var asset = ModelCatalog.OcrRecognitionEnglishDict with
            {
                Url = "https://huggingface.co/invalid/never-fetched",
            };
            await File.WriteAllTextAsync(Path.Combine(dir, asset.FileName), "tampered");

            var cache = new ModelCache(dir, new HttpClient(new FailingHandler()));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => cache.GetPathAsync(asset));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline test");
    }
}
