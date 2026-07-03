// ADR-0004 — real-scan validation corpus (complements LowResReproRunner's synthetic 72-DPI corpus).
//
// The synthetic repro proves the mechanism; REAL degraded scans prove the fix matters. This wraps
// loose scan images (JPG/PNG — e.g. the categorized document-scan corpus in Test-Data-36) into
// image-only PDFs at their natural effective DPI, then censuses how the shipped pipeline does on
// them. Real scans have no text-layer ground truth, so the census metric is the Gate 9a-real pair:
// words extracted + honesty flags — after the retry ladder ships, re-running the census on the same
// wrapped corpus must show empty pages recovering words (and unrecovered ones carrying a Notice).
//
//   --wrap-scans <img-dir> [out-dir] [--sample N] [--seed S] [--paper-width-in 8.5]
//       Wrap images into one-page image-only PDFs (ImageOnlyPdf). The page is paper-width inches
//       wide (default letter 8.5), aspect preserved, so a w-pixel-wide scan lands at
//       effectiveDpi = w / 8.5 — exactly how PdfImageScanResolutionEstimator will see it.
//       Prints the resulting effective-DPI distribution (the < MinScanDpi share is the low-res class).
//
//   --scan-census <pdf-dir> [out-dir] [--models <dir>] [--max-pages N]
//       Run the shipped pipeline (CreateDefault, default options) over wrapped scan PDFs and
//       report per page: words, EffectiveDpi, LowResolution, Notice. Summary + CSV = the 1.4.0
//       baseline ledger. Ledger-first: never fails the build.
//
// License note (constraint #4): wrapped corpora are LOCAL EVAL ONLY. Nothing here may feed
// published stats, models, or the README ledger without an explicit license check + approval.

using System.Globalization;
using System.Text;
using Foliant;
using Foliant.Pipeline;
using SkiaSharp;

namespace Foliant.Verification;

internal static class RealScanRunner
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png"];

    public static async Task<int> WrapScansAsync(string[] args)
    {
        if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine(
                "usage: --wrap-scans <img-dir> [out-dir] [--sample N] [--seed S] [--paper-width-in W]");
            return 2;
        }

        string imgDir = Path.GetFullPath(args[0]);
        string outDir = "verification-out/real-scan-pdfs";
        int sample = 500, seed = 12345;
        double paperWidthIn = 8.5;
        int onlyBelowDpi = int.MaxValue;   // --only-below-dpi N: keep only genuinely low-res scans
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--sample" when i + 1 < args.Length: sample = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seed" when i + 1 < args.Length: seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--paper-width-in" when i + 1 < args.Length: paperWidthIn = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--only-below-dpi" when i + 1 < args.Length: onlyBelowDpi = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default:
                    if (!args[i].StartsWith("--", StringComparison.Ordinal)) outDir = args[i];
                    break;
            }
        }
        Directory.CreateDirectory(outDir);

        var images = ImageExtensions
            .SelectMany(ext => Directory.GetFiles(imgDir, "*" + ext, SearchOption.AllDirectories))
            .Distinct().OrderBy(p => p).ToList();
        if (images.Count == 0) { Console.Error.WriteLine($"wrap-scans: no JPG/PNG under {imgDir}"); return 2; }

        // Low-res filter: effective DPI is width / paperWidthIn, known from the header alone —
        // SKCodec reads dimensions without decoding pixels, so filtering the full corpus is cheap.
        if (onlyBelowDpi != int.MaxValue)
        {
            int before = images.Count;
            images = images.Where(p =>
            {
                using var codec = SKCodec.Create(p);
                return codec is not null && (int)Math.Round(codec.Info.Width / paperWidthIn) < onlyBelowDpi;
            }).ToList();
            Console.WriteLine($"--only-below-dpi {onlyBelowDpi}: {images.Count}/{before} images qualify");
            if (images.Count == 0) { Console.Error.WriteLine("wrap-scans: nothing below that DPI"); return 2; }
        }

        // Seeded shuffle → stable sample across runs (same corpus in, same corpus out).
        var rng = new Random(seed);
        var picked = images.OrderBy(_ => rng.Next()).Take(sample).OrderBy(p => p).ToList();

        Console.WriteLine($"\n════ WRAP-SCANS — {picked.Count}/{images.Count} images → image-only PDFs ════");
        var dpiBuckets = new SortedDictionary<int, int>();   // bucketed by 25-DPI steps
        int wrapped = 0, failed = 0, lowRes = 0;

        foreach (string img in picked)
        {
            try
            {
                using var decoded = SKBitmap.Decode(img);
                if (decoded is null) { failed++; continue; }

                // Normalize to BGRA before encoding. Scan corpora are mostly GRAYSCALE JPEGs;
                // encoding those directly yields a 1-component JPEG, which contradicts the
                // /ColorSpace /DeviceRGB declared by ImageOnlyPdf — pdfium then renders the page
                // BLANK and the whole census reads empty. Drawing onto a BGRA canvas guarantees a
                // 3-component YCbCr JPEG that matches the declaration.
                using var bmp = new SKBitmap(new SKImageInfo(decoded.Width, decoded.Height,
                    SKColorType.Bgra8888, SKAlphaType.Opaque));
                using (var canvas = new SKCanvas(bmp))
                {
                    canvas.Clear(SKColors.White);
                    canvas.DrawBitmap(decoded, 0, 0);
                }

                byte[] jpeg;
                using (var image = SKImage.FromBitmap(bmp))
                using (var data = image.Encode(SKEncodedImageFormat.Jpeg, 90))
                    jpeg = data?.ToArray() ?? throw new InvalidOperationException("JPEG encode failed");

                // Paper-width page, aspect preserved → dpiX == dpiY == width / paperWidthIn.
                double pageW = paperWidthIn * 72.0;
                double pageH = pageW * bmp.Height / bmp.Width;
                int effDpi = (int)Math.Round(bmp.Width / paperWidthIn);

                byte[] pdf = ImageOnlyPdf.Build(jpeg, bmp.Width, bmp.Height, pageW, pageH);

                string rel = Path.GetRelativePath(imgDir, img);
                string name = Sanitize(Path.ChangeExtension(rel, null)) + $"-{effDpi}dpi.pdf";
                await File.WriteAllBytesAsync(Path.Combine(outDir, name), pdf);

                dpiBuckets[effDpi / 25 * 25] = dpiBuckets.GetValueOrDefault(effDpi / 25 * 25) + 1;
                if (effDpi < 150) lowRes++;   // ProcessingOptions.MinScanDpi default
                wrapped++;
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  {Path.GetFileName(img)}: {ex.Message}");
            }
        }

        Console.WriteLine($"\nwrapped {wrapped}, failed {failed} → {outDir}");
        Console.WriteLine($"low-resolution class (< 150 effective DPI): {lowRes}/{wrapped}");
        Console.WriteLine("effective-DPI distribution (25-DPI buckets):");
        foreach (var (bucket, count) in dpiBuckets)
            Console.WriteLine($"  {bucket,4}–{bucket + 24,-4}: {count}");
        Console.WriteLine("\nnext: --scan-census on the output dir for the 1.4.0 baseline ledger.");
        return 0;
    }

    public static async Task<int> CensusAsync(string[] args)
    {
        if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine("usage: --scan-census <pdf-dir> [out-dir] [--models <dir>] [--max-pages N]");
            return 2;
        }

        string pdfDir = Path.GetFullPath(args[0]);
        string outDir = "verification-out";
        string modelsDir = "models";
        int maxPages = int.MaxValue;
        string? superResModel = null;   // --super-res <path>: SR upscaler in the retry role
        int superResTile = 128;
        bool superResCuda = false;      // --super-res-cuda: CUDA EP (host needs Microsoft.ML.OnnxRuntime.Gpu)
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--models" when i + 1 < args.Length: modelsDir = args[++i]; break;
                case "--max-pages" when i + 1 < args.Length: maxPages = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--super-res" when i + 1 < args.Length: superResModel = args[++i]; break;
                case "--super-res-tile" when i + 1 < args.Length: superResTile = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--super-res-cuda": superResCuda = true; break;
                default:
                    if (!args[i].StartsWith("--", StringComparison.Ordinal)) outDir = args[i];
                    break;
            }
        }
        Directory.CreateDirectory(outDir);

        var pdfs = Directory.GetFiles(pdfDir, "*.pdf", SearchOption.AllDirectories).OrderBy(p => p).ToList();
        if (pdfs.Count == 0) { Console.Error.WriteLine($"scan-census: no PDFs in {pdfDir}"); return 2; }

        IScanUpscaler? upscaler = superResModel is not null
            ? new Foliant.ScanUpscale.SuperResolution.OnnxSuperResolutionUpscaler(superResModel,
                new Foliant.ScanUpscale.SuperResolution.SuperResolutionOptions
                    { UseCuda = superResCuda, FallbackTile = superResTile })
            : null;
        using var processor = FoliantProcessor.CreateDefault(modelsDir, scanUpscaler: upscaler);
        if (superResModel is not null)
            Console.WriteLine($"Mode: --super-res '{superResModel}' in the RETRY role " +
                              $"(tile {superResTile}{(superResCuda ? ", CUDA" : ", CPU")})");

        int total = Math.Min(pdfs.Count, maxPages);
        Console.WriteLine($"\n════ SCAN CENSUS — {total} real-scan pages, shipped defaults ════");
        Console.WriteLine("(full pipeline per page — expect a few seconds each on CPU; " +
                          "only EMPTY pages print below, healthy ones are silent)\n");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var csv = new StringBuilder("pdf,words,effectiveDpi,lowResolution,notice\n");
        int scored = 0, empty = 0, lowRes = 0, emptyLowRes = 0, noticed = 0, errors = 0;

        foreach (string pdfPath in pdfs)
        {
            if (scored >= maxPages) break;
            string name = Path.GetFileName(pdfPath);
            PageResult p;
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(pdfPath);
                var doc = await processor.ProcessAsync(bytes, new ProcessingOptions());
                p = doc.Pages[0];
            }
            catch (Exception ex)
            {
                errors++;
                Console.WriteLine($"{name}: ERROR {ex.Message}");
                continue;
            }

            int words = p.Lines.Sum(l =>
                l.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
            bool isEmpty = words < 3;   // ADR-0004 LowResolutionRetryMinWords

            scored++;
            if (isEmpty) empty++;
            if (p.LowResolution) lowRes++;
            if (isEmpty && p.LowResolution) emptyLowRes++;
            if (p.Notice is not null) noticed++;

            if (scored % 25 == 0)
                Console.WriteLine($"  … {scored}/{total} pages ({sw.Elapsed.TotalMinutes:F1} min, " +
                                  $"{empty} empty so far)");

            if (isEmpty)
                Console.WriteLine($"{name}: words={words}  effDpi={p.EffectiveDpi,4}  lowRes={p.LowResolution,-5}  " +
                                  $"notice={(p.Notice is null ? "none" : "SET")}   ← EMPTY");

            csv.Append(string.Join(",", Csv(name), words,
                p.EffectiveDpi?.ToString(CultureInfo.InvariantCulture) ?? "",
                p.LowResolution, Csv(p.Notice ?? ""))).Append('\n');
        }

        string csvPath = Path.Combine(outDir, $"scan-census{(superResModel is not null ? "-sr" : "")}.csv");
        await File.WriteAllTextAsync(csvPath, csv.ToString());

        Console.WriteLine($"\n──── SUMMARY ({scored} pages, {errors} errors, " +
                          $"{sw.Elapsed.TotalMinutes:F1} min ≈ {sw.Elapsed.TotalSeconds / Math.Max(1, scored):F1}s/page) ────");
        Console.WriteLine($"flagged low-resolution:            {lowRes}/{scored}");
        Console.WriteLine($"empty pages (<3 words):            {empty}/{scored}");
        Console.WriteLine($"  of which flagged low-res:        {emptyLowRes}   ← the retry ladder's target class");
        Console.WriteLine($"pages carrying a Notice:           {noticed}/{scored}");
        Console.WriteLine($"\nCSV: {csvPath}");
        Console.WriteLine("Gate 9a-real: after the retry ladder, re-run this census — empty low-res pages must " +
                          "gain words or carry a NeedsReview Notice; no page may lose words.");
        return 0;
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        return sb.ToString();
    }

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
}
