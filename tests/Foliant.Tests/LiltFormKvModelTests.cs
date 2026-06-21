using Foliant;
using Foliant.Forms.Lilt;
using Xunit;

namespace Foliant.Tests;

// ONNX inference smoke test (#9 LiLT integration, Stage 3). Loads the real model.onnx and runs one
// forward pass to prove the wiring — input names, int64 tensor shapes, logits parsing, token->word
// aggregation. It deliberately does NOT assert which words are values: prediction QUALITY on real forms
// is measured by the Gate-3 eval, not a unit test on synthetic input.
public sealed class LiltFormKvModelTests
{
    [Fact]
    public void PredictValueWords_RunsEndToEnd_AndReturnsValidWordIndices()
    {
        using var model = new LiltFormKvModel(FindModelDir());

        // Empty input short-circuits before any session work.
        Assert.Empty(model.PredictValueWords(Array.Empty<string>(), Array.Empty<BoundingBox>(), 1000, 1000));

        var words = new[] { "Solicitation", "Number", "ABC123-25-R-00001", "Date", "03/14/2026" };
        var boxes = new[]
        {
            new BoundingBox(20, 20, 120, 40),
            new BoundingBox(120, 20, 200, 40),
            new BoundingBox(220, 20, 420, 40),
            new BoundingBox(20, 60, 80, 80),
            new BoundingBox(120, 60, 240, 80),
        };

        var valueWords = model.PredictValueWords(words, boxes, pageWidth: 1000, pageHeight: 1000);

        // Wiring-level guarantees only: valid, de-duplicated word indices.
        Assert.All(valueWords, wi => Assert.InRange(wi, 0, words.Length - 1));
        Assert.Equal(valueWords.Distinct().Count(), valueWords.Count);
    }

    private static string FindModelDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            string candidate = Path.Combine(d.FullName, "models", "form-kv-lilt");
            if (Directory.Exists(candidate)) return candidate;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException("Could not find models/form-kv-lilt above the test output dir.");
    }
}
