// VLM baseline — replicates the host application's OllamaVlmOcrService call byte-for-byte
// (same prompt, temperature 0, num_ctx 8192, same image normalization) without
// depending on that application's code. Output scored with the same text-layer recall metric
// as the Foliant pipeline, so the comparison is apples to apples.

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using PDFtoImage;
using SkiaSharp;

namespace Foliant.Spike;

public static class VlmBaseline
{
    // Verbatim from the host application's OllamaVlmOcrService.ExtractionPrompt
    private const string ExtractionPrompt = """
        You are a document OCR system extracting content from a page image.

        Strict rules:
        - Extract ALL text exactly as it appears, preserving order.
        - Format tables as proper markdown tables with | and --- separators.
        - Preserve table column headers and row structure precisely.
        - For multi-column layouts, process left column completely, then right column.
        - Include text from images, charts, diagrams, and stamps.
        - Mark unreadable sections as [unreadable].
        - Mark handwriting as [handwritten: text].
        - Do NOT summarize, paraphrase, comment, or add explanation.
        - Output ONLY the extracted content. No preamble, no closing.
        """;

    public static async Task<int> RunAsync(string pdfPath, int pageNumber, string outputDir,
                                           string endpoint, string model)
    {
        var pdfBytes = await File.ReadAllBytesAsync(pdfPath);
        var stem = Path.GetFileNameWithoutExtension(pdfPath);
        Directory.CreateDirectory(outputDir);

        using var stream = new MemoryStream(pdfBytes, writable: false);
        using var bitmap = Conversion.ToImage(
            stream, page: (Index)(pageNumber - 1), options: new RenderOptions(Dpi: 300));

        var png = NormalizeLikeVlm(bitmap);
        Console.WriteLine($"[vlm] {model} @ {endpoint} — page {pageNumber}, image {png.Length / 1024}KB");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        var sw = Stopwatch.StartNew();
        using var resp = await http.PostAsJsonAsync($"{endpoint}/api/generate", new
        {
            model,
            prompt = ExtractionPrompt,
            images = new[] { Convert.ToBase64String(png) },
            stream = false,
            options = new { temperature = 0, num_ctx = 8192 },
        });
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
        {
            Console.Error.WriteLine($"[vlm] HTTP {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
            return 4;
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var text = doc.RootElement.GetProperty("response").GetString() ?? "";

        var outPath = Path.Combine(outputDir, $"{stem}_p{pageNumber:D3}.vlm.md");
        await File.WriteAllTextAsync(outPath, text);

        var (truthWords, found) = SpikePipeline.ScoreTextLayerRecall(pdfBytes, pageNumber, text);
        var recall = truthWords > 0 ? $"{100.0 * found / truthWords:0.0}%" : "n/a (no text layer)";
        Console.WriteLine($"[vlm] {sw.Elapsed.TotalSeconds:0.0}s, recall {recall} → {outPath}");
        return 0;
    }

    /// <summary>VLM ImageNormalization: cap longest side at 1568, round both sides
    /// down to a multiple of 28 (Qwen2.5-VL patch grid), re-encode PNG.</summary>
    private static byte[] NormalizeLikeVlm(SKBitmap bitmap, int maxDimension = 1568)
    {
        const int PatchMultiple = 28;
        int w = bitmap.Width, h = bitmap.Height;

        float scale = Math.Max(w, h) > maxDimension ? (float)maxDimension / Math.Max(w, h) : 1f;
        int targetW = Math.Max(PatchMultiple, (int)(w * scale) / PatchMultiple * PatchMultiple);
        int targetH = Math.Max(PatchMultiple, (int)(h * scale) / PatchMultiple * PatchMultiple);

        SKBitmap toEncode = bitmap;
        SKBitmap? resized = null;
        if (targetW != w || targetH != h)
        {
            resized = bitmap.Resize(new SKImageInfo(targetW, targetH), SKFilterQuality.High)
                      ?? throw new InvalidOperationException("normalize resize failed");
            toEncode = resized;
        }

        try
        {
            using var image = SKImage.FromBitmap(toEncode);
            using var data = image.Encode(SKEncodedImageFormat.Png, 90);
            return data.ToArray();
        }
        finally { resized?.Dispose(); }
    }
}
