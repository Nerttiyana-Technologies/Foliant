// Gate 8 — super-resolution benefit ledger. Like Gate 7, it needs no hand-labeling: it runs on
// BORN-DIGITAL pages whose embedded text layer is exact ground truth, in forced-OCR mode so
// extraction reads pixels while recall is scored against the pristine layer.
//
// It measures one thing: does the classical (bicubic) upscaler recover OCR recall lost to low
// resolution? For each sampled page it simulates a low-DPI scan (ScanDegrader.Downscale to 150/
// 100/72 DPI — detail below the sampling rate is gone), then OCRs that page with NO upscale and
// with the upscaler at 1.5× / 2×, reporting the recall delta of each upscale arm vs the same
// level's no-upscale baseline.
//
// IMPORTANT — why the upscaler is driven via ImageTransform here, not the shipped auto-trigger:
// PageResult.LowResolution is derived from the PDF's EMBEDDED images (a born-digital page has
// none), while these degradations live on the in-memory raster. So the estimator→LowResolution
// path can't fire on this corpus. The auto-trigger is covered by unit tests; this Gate isolates
// the upscaler's intrinsic recall effect, which is the number that decides whether the
// UpscaleLowResolutionScans default flips on.
//
// Ledger-first by design: nothing here fails the build. A positive, consistent delta is the
// evidence to enable the default; a flat/negative one is the evidence to keep it off (and to
// reach for an ML super-resolution backend via the IScanUpscaler seam instead).

using System.Globalization;
using Foliant;
using Foliant.Pipeline;

namespace Foliant.Verification;

internal static class Gate8Runner
{
    // Simulated source resolutions (the rendered page is 300 DPI; these drop detail below it).
    private static readonly int[] LevelsDpi = [150, 100, 72];

    // Upscale arms applied to each low-DPI level. 1.0 = no upscale (the per-level baseline).
    private static readonly float[] Factors = [1.0f, 1.5f, 2.0f];

    private sealed class UpscaleTransform(float factor) : IPageImageTransform
    {
        private static readonly ClassicalScanUpscaler Upscaler = new();
        public PageImage Transform(PageImage image) => Upscaler.Upscale(image, factor);
    }

    private static IPageImageTransform ArmTransform(int levelDpi, float factor) =>
        factor <= 1f
            ? ScanDegrader.Downscale(levelDpi)
            : ScanDegrader.Compose(ScanDegrader.Downscale(levelDpi), new UpscaleTransform(factor));

    /// <param name="pagesPerPdf">How many usable (text-bearing) pages to sample from each PDF.</param>
    /// <param name="minTruthWords">A page needs at least this many text-layer words to be a usable truth source.</param>
    public static async Task<bool> RunAsync(
        DocumentProcessor processor, string pdfDir, string outDir,
        int pagesPerPdf = 2, int minTruthWords = 50)
    {
        var pdfs = Directory.GetFiles(pdfDir, "*.pdf").OrderBy(p => p).ToList();
        if (pdfs.Count == 0) { Console.Error.WriteLine($"gate8: no PDFs in {pdfDir}"); return true; }

        Console.WriteLine($"\n════ GATE 8 — super-resolution benefit ({LevelsDpi.Length} low-DPI levels × {Factors.Length} arms) ════");
        Console.WriteLine($"sampling up to {pagesPerPdf} text-bearing page(s)/PDF from {pdfs.Count} PDFs; forced OCR.\n");

        // recall samples keyed (levelDpi, factor)
        var samples = new Dictionary<(int Dpi, float Factor), List<double>>();
        foreach (int d in LevelsDpi)
            foreach (float f in Factors)
                samples[(d, f)] = new List<double>();
        var pristine = new List<double>();
        var ledger = new List<string>();
        int pagesScored = 0;

        ProcessingOptions OptsFor(int page, IPageImageTransform? t) => new()
        {
            TextLayer = TextLayerMode.Never,    // read the degraded pixels, not the text layer
            Verify = true,                      // compute recall against the pristine layer
            Pages = new[] { page },
            ImageTransform = t,
            PreprocessScans = true,             // measure the pipeline as shipped (deskew/denoise)
            DetectOrientation = false,          // nothing is rotated here; save the 4 thumbnail passes
        };

        foreach (var pdf in pdfs)
        {
            var name = Path.GetFileName(pdf);
            byte[] bytes = await File.ReadAllBytesAsync(pdf);
            int collected = 0;

            for (int page = 1; collected < pagesPerPdf; page++)
            {
                // Pristine pass: establishes whether the page has enough truth, and the ceiling.
                PageResult? basePage;
                try
                {
                    var doc = await processor.ProcessAsync(bytes, OptsFor(page, null));
                    basePage = doc.Pages.Count > 0 ? doc.Pages[0] : null;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{name} p{page}: ERROR {ex.Message}");
                    break;
                }
                if (basePage is null) break; // past last page

                var bv = basePage.Verification;
                if (bv.TruthWords < minTruthWords) continue; // too little ground truth; skip page

                double pristineRecall = bv.RecallPercent ?? 0;
                pristine.Add(pristineRecall);
                ledger.Add(Csv(name, page, 0, 1.0f, pristineRecall, bv.Seconds));
                Console.WriteLine($"{name} p{page}  ({bv.TruthWords} truth words)  pristine OCR recall {pristineRecall:0.0}%");

                foreach (int levelDpi in LevelsDpi)
                {
                    double noUpRecall = double.NaN;
                    foreach (float factor in Factors)
                    {
                        double recall; double secs;
                        try
                        {
                            var doc = await processor.ProcessAsync(bytes, OptsFor(page, ArmTransform(levelDpi, factor)));
                            var pr = doc.Pages.Count > 0 ? doc.Pages[0] : null;
                            if (pr is null) continue;
                            recall = pr.Verification.RecallPercent ?? 0;
                            secs = pr.Verification.Seconds;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"    {levelDpi}dpi ×{factor:0.0}  ERROR {ex.Message}");
                            continue;
                        }
                        samples[(levelDpi, factor)].Add(recall);
                        ledger.Add(Csv(name, page, levelDpi, factor, recall, secs));

                        if (factor <= 1f) { noUpRecall = recall; Console.WriteLine($"    {levelDpi,3}dpi  no-upscale  {recall,5:0.0}%"); }
                        else Console.WriteLine($"    {levelDpi,3}dpi  ×{factor:0.0}        {recall,5:0.0}%   (Δ {Delta(recall - noUpRecall)})");
                    }
                }

                collected++;
                pagesScored++;
            }
        }

        // ── Ledger CSV ────────────────────────────────────────────────────────────
        Directory.CreateDirectory(outDir);
        var csvPath = Path.Combine(outDir, "gate8-ledger.csv");
        await using (var csv = new StreamWriter(csvPath))
        {
            await csv.WriteLineAsync("pdf,page,level_dpi,upscale_factor,recall_pct,seconds");
            foreach (var row in ledger) await csv.WriteLineAsync(row);
        }

        // ── Summary table ─────────────────────────────────────────────────────────
        double pristineAvg = pristine.Count > 0 ? pristine.Average() : 0;
        Console.WriteLine($"\n──── GATE 8 LEDGER  (pages scored: {pagesScored}; pristine OCR recall {pristineAvg:0.0}%) ────");
        Console.WriteLine($"{"level",-9}{"arm",-12}{"pages",6}{"avg recall",12}{"Δ vs no-up",12}");
        foreach (int levelDpi in LevelsDpi)
        {
            var noUp = samples[(levelDpi, 1.0f)];
            double noUpAvg = noUp.Count > 0 ? noUp.Average() : 0;
            foreach (float factor in Factors)
            {
                var s = samples[(levelDpi, factor)];
                if (s.Count == 0) continue;
                double avg = s.Average();
                string arm = factor <= 1f ? "no-upscale" : $"×{factor:0.0}";
                string delta = factor <= 1f ? "—" : Delta(avg - noUpAvg);
                Console.WriteLine($"{levelDpi + "dpi",-9}{arm,-12}{s.Count,6}{avg,11:0.0}%{delta,12}");
            }
        }
        Console.WriteLine($"\nledger → {csvPath}");
        Console.WriteLine("Gate 8 is informational (ledger-first); it does not fail the build.");
        Console.WriteLine("Read it as: a consistent positive Δ at the upscale arms is the evidence to flip the");
        Console.WriteLine("ProcessingOptions.UpscaleLowResolutionScans default on; a flat/negative Δ keeps it off.");
        return true;
    }

    private static string Delta(double d) => d >= 0.05 ? $"+{d:0.0}" : d <= -0.05 ? $"{d:0.0}" : "~0";

    private static string Csv(string pdf, int page, int levelDpi, float factor, double recall, double seconds) =>
        string.Create(CultureInfo.InvariantCulture,
            $"\"{pdf}\",{page},{levelDpi},{factor:0.0},{recall:0.0},{seconds:0.0}");
}
