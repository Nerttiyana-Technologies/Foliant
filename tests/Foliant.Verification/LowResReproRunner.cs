// ADR-0004 repro — the customer bug, end to end, in synthetic form.
//
// Report: documents containing low-resolution scanned pages show recall 100% while those pages
// emit NO text at all. Root cause (1.4.0): OCR yields nothing on low-DPI scans → empty PageResult;
// no text layer → RecallPercent = null ("undefined, not zero") → recall aggregates see only
// healthy pages → 100%. LowResolution/EffectiveDpi are advisory-only; no Notice is set.
//
// This runner makes that failure measurable WITHOUT customer data:
//   1. sample text-bearing born-digital pages from a corpus (their text layer = exact ground truth),
//   2. render each at --scan-dpi (default 72) and wrap the raster as a REAL image-only PDF
//      (full-page JPEG XObject, ImageOnlyPdf.cs) — the estimator sees a genuine ~72-DPI scan,
//   3. run the shipped pipeline (FoliantProcessor.CreateDefault) on the synthetic scan,
//   4. report the pipeline's own verdict (RecallPercent, LowResolution, Notice) next to the
//      HONEST recall scored against the ORIGINAL page's text layer.
//
// Expected on 1.4.0: many pages with ~0 words, reported recall "—" (null → invisible to any
// aggregate), zero notices — while honest recall says ~0%. That is the bug. The synthetic PDFs
// are saved to <out-dir>/lowres-pdfs/ and double as the Gate 9a recovery corpus.
//
// Usage:
//   dotnet run -c Release --project tests/Foliant.Verification -- --lowres-repro <pdf-dir>
//     [out-dir] [--models <dir>] [--scan-dpi 72] [--pages-per-pdf 2] [--min-truth-words 50]
//     [--max-pages 40]
//
// Ledger-first: this runner never fails the build; it produces the evidence (console + CSV).

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Foliant;
using Foliant.Pipeline;
using SkiaSharp;

namespace Foliant.Verification;

internal static class LowResReproRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine(
                "usage: --lowres-repro <pdf-dir> [out-dir] [--models <dir>] [--scan-dpi N] " +
                "[--pages-per-pdf N] [--min-truth-words N] [--max-pages N] [--no-retry]");
            return 2;
        }

        string pdfDir = Path.GetFullPath(args[0]);
        string outDir = "verification-out";
        string modelsDir = "models";
        int scanDpi = 72, pagesPerPdf = 2, minTruthWords = 50, maxPages = 40;
        bool noRetry = false;         // --no-retry: 1.4.0-equivalent behavior, the Gate 9a A/B baseline
        string? superResModel = null; // --super-res <path>: SR upscaler in the RETRY role (vs classical)
        int superResTile = 128;
        bool superResCuda = false;    // --super-res-cuda: CUDA EP (host needs Microsoft.ML.OnnxRuntime.Gpu)
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--no-retry": noRetry = true; break;
                case "--super-res" when i + 1 < args.Length: superResModel = args[++i]; break;
                case "--super-res-tile" when i + 1 < args.Length: superResTile = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--super-res-cuda": superResCuda = true; break;
                case "--models" when i + 1 < args.Length: modelsDir = args[++i]; break;
                case "--scan-dpi" when i + 1 < args.Length: scanDpi = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--pages-per-pdf" when i + 1 < args.Length: pagesPerPdf = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--min-truth-words" when i + 1 < args.Length: minTruthWords = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--max-pages" when i + 1 < args.Length: maxPages = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default:
                    if (!args[i].StartsWith("--", StringComparison.Ordinal)) outDir = args[i];
                    break;
            }
        }

        string pdfOutDir = Path.Combine(outDir, "lowres-pdfs");
        Directory.CreateDirectory(pdfOutDir);

        // Default = shipped wiring (ClassicalScanUpscaler in the retry role); --super-res swaps in
        // the ML upscaler so the SAME corpus and trigger A/B the two backends' recovery rates.
        IScanUpscaler? upscaler = superResModel is not null
            ? new Foliant.ScanUpscale.SuperResolution.OnnxSuperResolutionUpscaler(superResModel,
                new Foliant.ScanUpscale.SuperResolution.SuperResolutionOptions
                    { UseCuda = superResCuda, FallbackTile = superResTile })
            : null;
        using var processor = FoliantProcessor.CreateDefault(modelsDir, scanUpscaler: upscaler);
        var renderer = new PdfPageRenderer();
        if (superResModel is not null)
            Console.WriteLine($"Mode: --super-res '{superResModel}' in the RETRY role " +
                              $"(tile {superResTile}{(superResCuda ? ", CUDA" : ", CPU")})");

        var pdfs = Directory.GetFiles(pdfDir, "*.pdf", SearchOption.AllDirectories).OrderBy(p => p).ToList();
        if (pdfs.Count == 0) { Console.Error.WriteLine($"lowres-repro: no PDFs in {pdfDir}"); return 2; }

        Console.WriteLine($"\n════ ADR-0004 REPRO — synthetic {scanDpi}-DPI image-only scans" +
                          $"{(noRetry ? " [RETRY OFF — 1.4.0-equivalent baseline]" : "")} ════");
        Console.WriteLine($"sampling up to {pagesPerPdf} text-bearing page(s)/PDF (≥{minTruthWords} truth words), " +
                          $"max {maxPages} pages total.\n");

        var csv = new StringBuilder(
            "pdf,page,scanDpi,words,reportedRecall,effectiveDpi,lowResolution,notice,truthWords,honestRecall,syntheticPdf\n");

        int scored = 0, silentlyEmpty = 0, noticed = 0, errored = 0;
        var reportedRecalls = new List<double>();   // what a caller aggregating RecallPercent sees
        var honestRecalls = new List<double>();     // recall vs the ORIGINAL page's text layer

        int pdfIndex = 0;
        foreach (string pdfPath in pdfs)
        {
            pdfIndex++;
            if (scored >= maxPages) break;
            string name = Path.GetFileName(pdfPath);              // CSV identity — keep clean
            string label = $"[{pdfIndex}/{pdfs.Count}] {name}";   // console progress prefix
            byte[] original;
            int pageCount;
            var pageSizes = new List<(int Page, double W, double H, int Words)>();
            try
            {
                original = await File.ReadAllBytesAsync(pdfPath);
                using var doc = UglyToad.PdfPig.PdfDocument.Open(original);
                pageCount = doc.NumberOfPages;
                for (int p = 1; p <= pageCount; p++)
                {
                    var page = doc.GetPage(p);
                    int words = page.GetWords().Count(w => w.Text.Length >= 3);
                    pageSizes.Add((p, page.Width, page.Height, words));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{label}: unreadable ({ex.Message}) — skipped");
                continue;
            }

            int collected = 0;
            foreach (var (pageNum, widthPts, heightPts, truthWordCount) in pageSizes)
            {
                if (collected >= pagesPerPdf || scored >= maxPages) break;
                if (truthWordCount < minTruthWords) continue;   // not enough ground truth

                PageResult result;
                string synthPath;
                try
                {
                    // Render the born-digital page AT the simulated scanner resolution, so the
                    // embedded image's native pixel size — not a blur transform — carries the
                    // degradation, exactly like a real low-res scan.
                    var raster = renderer.Render(original, pageNum, scanDpi);
                    byte[] jpeg = EncodeJpeg(raster);
                    byte[] synthetic = ImageOnlyPdf.Build(jpeg, raster.Width, raster.Height, widthPts, heightPts);

                    synthPath = Path.Combine(pdfOutDir,
                        $"{Sanitize(Path.GetFileNameWithoutExtension(name))}-p{pageNum}-{scanDpi}dpi.pdf");
                    await File.WriteAllBytesAsync(synthPath, synthetic);

                    // Shipped defaults: TextLayer=Auto routes to OCR (there is no layer), Verify on.
                    var docResult = await processor.ProcessAsync(synthetic,
                        new ProcessingOptions { RetryLowResolutionPages = !noRetry });
                    result = docResult.Pages[0];
                }
                catch (Exception ex)
                {
                    errored++;
                    Console.WriteLine($"{label} p{pageNum}: ERROR {ex.Message}");
                    continue;
                }

                int words = result.Lines.Sum(l =>
                    l.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);

                // What the pipeline REPORTS for the synthetic scan…
                double? reported = result.Verification.RecallPercent;
                if (reported is double r) reportedRecalls.Add(r);

                // …vs the HONEST score against the original page's text layer.
                string extractedText = result.Markdown + "\n" +
                                       string.Join("\n", result.PageFurniture.Select(l => l.Text));
                var (truthWords, truthFound) = TextLayerRecall(original, pageNum, extractedText);
                double honest = truthWords > 0 ? 100.0 * truthFound / truthWords : 0;
                honestRecalls.Add(honest);

                bool empty = words < 3;
                if (empty) silentlyEmpty++;
                if (result.Notice is not null) noticed++;
                scored++; collected++;

                Console.WriteLine(
                    $"{label} p{pageNum}: words={words,4}  reported-recall={(reported is double rr ? rr.ToString("F1", CultureInfo.InvariantCulture) + "%" : "—"),-6}  " +
                    $"effDpi={result.EffectiveDpi,4}  lowRes={result.LowResolution,-5}  notice={(result.Notice is null ? "none" : "SET"),-4}  " +
                    $"honest-recall={honest.ToString("F1", CultureInfo.InvariantCulture)}% ({truthFound}/{truthWords})" +
                    (empty ? "   ← SILENTLY EMPTY" : ""));

                csv.Append(string.Join(",",
                    Csv(name), pageNum, scanDpi, words,
                    reported is double rv ? rv.ToString("F1", CultureInfo.InvariantCulture) : "",
                    result.EffectiveDpi?.ToString(CultureInfo.InvariantCulture) ?? "",
                    result.LowResolution,
                    Csv(result.Notice ?? ""),
                    truthWords, honest.ToString("F1", CultureInfo.InvariantCulture),
                    Csv(Path.GetFileName(synthPath)))).Append('\n');
            }
        }

        string csvPath = Path.Combine(outDir,
            $"lowres-repro-{scanDpi}dpi{(noRetry ? "-noretry" : "")}{(superResModel is not null ? "-sr" : "")}.csv");
        await File.WriteAllTextAsync(csvPath, csv.ToString());

        Console.WriteLine($"\n──── SUMMARY ({scored} synthetic {scanDpi}-DPI scan pages" +
                          $"{(errored > 0 ? $"; {errored} pages ERRORED and are NOT counted below" : "")}) ────");
        if (errored > 0)
            Console.WriteLine($"⚠ {errored} page(s) crashed during processing — fix those before trusting this summary.");
        Console.WriteLine($"silently empty pages (<3 words):   {silentlyEmpty} / {scored}");
        Console.WriteLine($"pages carrying a Notice:           {noticed} / {scored}");
        Console.WriteLine(reportedRecalls.Count == 0
            ? $"reported recall (RecallPercent):   NO SAMPLES — every page is null → an aggregating " +
              $"caller sees ONLY its healthy pages, i.e. 100%. This is the customer's bug."
            : $"reported recall (RecallPercent):   avg {reportedRecalls.Average().ToString("F1", CultureInfo.InvariantCulture)}% " +
              $"over {reportedRecalls.Count} non-null pages (the rest are invisible)");
        Console.WriteLine($"honest recall vs original truth:   avg {(honestRecalls.Count > 0 ? honestRecalls.Average() : 0).ToString("F1", CultureInfo.InvariantCulture)}%");
        Console.WriteLine($"\nCSV: {csvPath}\nsynthetic corpus (reusable for Gate 9a): {pdfOutDir}");
        return 0;
    }

    // Word-level recall of extracted text vs the ORIGINAL page's embedded text layer — a local
    // mirror of ExtractionVerifier.TextLayerRecall (internal to Foliant.Pipeline; visible only to
    // Foliant.Tests): words of length ≥ 3, alphanumeric-normalized, uppercased.
    private static (int TruthWords, int Found) TextLayerRecall(byte[] pdf, int pageNumber, string extractedText)
    {
        static string Normalize(string s) =>
            new string(s.Where(char.IsLetterOrDigit).ToArray());

        using var doc = UglyToad.PdfPig.PdfDocument.Open(pdf);
        var page = doc.GetPage(pageNumber);
        var truth = page.GetWords()
            .Select(w => Normalize(w.Text).ToUpperInvariant())
            .Where(t => t.Length >= 3)
            .ToList();
        if (truth.Count == 0) return (0, 0);

        var extracted = new HashSet<string>(
            extractedText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(w => Normalize(w).ToUpperInvariant()));
        return (truth.Count, truth.Count(t => extracted.Contains(t)));
    }

    private static byte[] EncodeJpeg(PageImage img)
    {
        using var bmp = new SKBitmap(new SKImageInfo(img.Width, img.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        Marshal.Copy(img.PixelsBgra8888, 0, bmp.GetPixels(), img.PixelsBgra8888.Length);
        bmp.NotifyPixelsChanged();
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85)
            ?? throw new InvalidOperationException("JPEG encode failed.");
        return data.ToArray();
    }

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Replace(' ', '_');
    }

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
}
