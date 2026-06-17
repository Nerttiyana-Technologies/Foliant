// Gate 7 — degradation robustness. Unlike Gates 3/5/6 (hand-labeled truth), Gate 7 needs no
// labeling: it runs on BORN-DIGITAL pages whose embedded text layer is exact ground truth.
// For each sampled page it processes a baseline (no degradation) plus a matrix of synthetic
// scan degradations, ALWAYS in forced-OCR mode (TextLayerMode.Never) so extraction reads the
// degraded pixels — while recall is still scored against the pristine text layer. The output
// is a ledger: average word recall per degradation level, and the drop from baseline. That
// drop is the measured cost of each artifact, and the yardstick the scanned-doc features
// (orientation detection, dewarp, super-res) are judged against in later releases.
//
// Ledger-first by design: nothing here fails the build. Thresholds get set once we have real
// numbers, not guessed up front.

using System.Globalization;
using Foliant;
using Foliant.Pipeline;

namespace Foliant.Verification;

internal static class Gate7Runner
{
    private sealed record Variant(string Family, string Label, IPageImageTransform Transform);

    // The degradation matrix (all four families). Levels are ordered mild → severe within a family.
    private static readonly Variant[] Variants =
    [
        new("baseline", "baseline",      ScanDegrader.Identity),

        new("skew",     "skew +1°",      ScanDegrader.Rotate(1)),
        new("skew",     "skew +3°",      ScanDegrader.Rotate(3)),
        new("skew",     "skew +7°",      ScanDegrader.Rotate(7)),

        new("orient",   "rotate 90°",    ScanDegrader.Rotate(90)),
        new("orient",   "rotate 180°",   ScanDegrader.Rotate(180)),
        new("orient",   "rotate 270°",   ScanDegrader.Rotate(270)),

        new("jpeg",     "jpeg q75",      ScanDegrader.JpegRecompress(75)),
        new("jpeg",     "jpeg q40",      ScanDegrader.JpegRecompress(40)),
        new("jpeg",     "jpeg q20",      ScanDegrader.JpegRecompress(20)),

        new("noise",    "noise σ8",      ScanDegrader.GaussianNoise(8)),
        new("noise",    "noise σ20",     ScanDegrader.GaussianNoise(20)),

        new("blur",     "blur σ1.0",     ScanDegrader.GaussianBlur(1.0f)),
        new("blur",     "blur σ2.5",     ScanDegrader.GaussianBlur(2.5f)),

        new("lowdpi",   "downscale 150", ScanDegrader.Downscale(150)),
        new("lowdpi",   "downscale 100", ScanDegrader.Downscale(100)),
        new("lowdpi",   "downscale 72",  ScanDegrader.Downscale(72)),

        new("fade",     "fade keep .6",  ScanDegrader.FadeContrast(0.6)),
        new("fade",     "fade keep .35", ScanDegrader.FadeContrast(0.35)),
    ];

    /// <param name="pagesPerPdf">How many usable (text-bearing) pages to sample from each PDF.</param>
    /// <param name="minTruthWords">A page needs at least this many text-layer words to be a usable truth source.</param>
    public static async Task<bool> RunAsync(
        DocumentProcessor processor, string pdfDir, string outDir,
        int pagesPerPdf = 2, int minTruthWords = 50)
    {
        var pdfs = Directory.GetFiles(pdfDir, "*.pdf").OrderBy(p => p).ToList();
        if (pdfs.Count == 0) { Console.Error.WriteLine($"gate7: no PDFs in {pdfDir}"); return true; }

        Console.WriteLine($"\n════ GATE 7 — degradation robustness ({Variants.Length - 1} degradations) ════");
        Console.WriteLine($"sampling up to {pagesPerPdf} text-bearing page(s)/PDF from {pdfs.Count} PDFs; forced OCR.\n");

        // recall samples per variant label, in matrix order
        var samples = Variants.ToDictionary(v => v.Label, _ => new List<double>());
        var ledger = new List<string>();
        int pagesScored = 0;

        // Forced OCR so extraction reads the degraded pixels; verify on so recall is computed.
        ProcessingOptions OptsFor(int page, IPageImageTransform t) => new()
        {
            TextLayer = TextLayerMode.Never,
            Verify = true,
            Pages = new[] { page },
            ImageTransform = t,
            // Measure the pipeline AS SHIPPED: fine deskew/denoise AND coarse orientation
            // correction on. With this on, the rotate-90/180/270 rows should recover toward
            // baseline — that recovery is the proof orientation detection works. (It also makes
            // each page cost ~5 OCR passes; keep --gate7-pages small.)
            PreprocessScans = true,
            DetectOrientation = true,
        };

        foreach (var pdf in pdfs)
        {
            var name = Path.GetFileName(pdf);
            byte[] bytes = await File.ReadAllBytesAsync(pdf);
            int collected = 0;

            for (int page = 1; collected < pagesPerPdf; page++)
            {
                // Baseline pass also tells us whether the page has enough truth to be usable.
                PageResult? basePage;
                try
                {
                    var doc = await processor.ProcessAsync(bytes, OptsFor(page, ScanDegrader.Identity));
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

                double baseRecall = basePage.Verification.RecallPercent ?? 0;
                samples["baseline"].Add(baseRecall);
                ledger.Add(Csv(name, page, "baseline", "baseline", baseRecall, bv.Seconds, bv.LinesLost));
                Console.WriteLine($"{name} p{page}  ({bv.TruthWords} truth words)  baseline OCR recall {baseRecall:0.0}%");

                foreach (var v in Variants.Where(v => v.Family != "baseline"))
                {
                    double recall; double secs; int lost;
                    try
                    {
                        var doc = await processor.ProcessAsync(bytes, OptsFor(page, v.Transform));
                        var pr = doc.Pages.Count > 0 ? doc.Pages[0] : null;
                        if (pr is null) continue;
                        recall = pr.Verification.RecallPercent ?? 0;
                        secs = pr.Verification.Seconds;
                        lost = pr.Verification.LinesLost;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    {v.Label,-14} ERROR {ex.Message}");
                        continue;
                    }
                    samples[v.Label].Add(recall);
                    ledger.Add(Csv(name, page, v.Family, v.Label, recall, secs, lost));
                    Console.WriteLine($"    {v.Label,-14} {recall,5:0.0}%   (Δ {Delta(recall - baseRecall)})");
                }

                collected++;
                pagesScored++;
            }
        }

        // ── Ledger CSV ────────────────────────────────────────────────────────────
        Directory.CreateDirectory(outDir);
        var csvPath = Path.Combine(outDir, "gate7-ledger.csv");
        await using (var csv = new StreamWriter(csvPath))
        {
            await csv.WriteLineAsync("pdf,page,family,variant,recall_pct,seconds,coverage_missing");
            foreach (var row in ledger) await csv.WriteLineAsync(row);
        }

        // ── Summary table ─────────────────────────────────────────────────────────
        double baseAvg = samples["baseline"].Count > 0 ? samples["baseline"].Average() : 0;
        Console.WriteLine($"\n──── GATE 7 LEDGER  (pages scored: {pagesScored}; baseline OCR recall {baseAvg:0.0}%) ────");
        Console.WriteLine($"{"degradation",-16}{"pages",6}{"avg recall",12}{"Δ vs base",12}");
        foreach (var v in Variants)
        {
            var s = samples[v.Label];
            if (s.Count == 0) continue;
            double avg = s.Average();
            string delta = v.Label == "baseline" ? "—" : Delta(avg - baseAvg);
            Console.WriteLine($"{v.Label,-16}{s.Count,6}{avg,11:0.0}%{delta,12}");
        }
        Console.WriteLine($"\nledger → {csvPath}");
        Console.WriteLine("Gate 7 is informational (ledger-first); it does not fail the build.");
        return true;
    }

    // Signed delta with a clean leading +/- (avoids the "-+0.0" the two-section format produced).
    private static string Delta(double d) => d >= 0.05 ? $"+{d:0.0}" : d <= -0.05 ? $"{d:0.0}" : "~0";

    private static string Csv(string pdf, int page, string family, string variant,
                              double recall, double seconds, int lost) =>
        string.Create(CultureInfo.InvariantCulture,
            $"\"{pdf}\",{page},{family},\"{variant}\",{recall:0.0},{seconds:0.0},{lost}");
}
