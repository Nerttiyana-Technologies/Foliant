using System.Globalization;
using System.Text;
using Foliant.Layout.DocLayoutNet;
using Foliant.Models;
using Foliant.Pipeline;
using ZeroDep;                       // PdfAnalyzer
using ZD = ZeroDep.Abstractions;

namespace Foliant.Verification;

/// <summary>
/// ADR-0003 table-probe (the decisive experiment). The routing census found that 99% of the
/// <c>TableOrComplexLayout</c> pages are born-digital — a 67% fast-lane ceiling — but ZeroDep can't tell a
/// genuine data table (where flat text loses the value) from the hint over-firing on lightly-ruled or
/// columnar prose (where flat text is correct and better). This samples those born-digital table-class
/// pages, renders them, runs Foliant's layout detector (DocLayout-YOLO), and reports what fraction actually
/// contains a detected <see cref="RegionType.Table"/> region — the real-table hit rate.
///
/// Low hit rate → the hint over-fires → sharpening it reclaims the ceiling with no fidelity loss.
/// High hit rate → these are genuine tables → reclaiming is a fidelity tradeoff (per-consumer policy only).
///
/// Usage: dotnet run -c Release --project tests/Foliant.Verification -- --table-probe &lt;pdf-dir&gt;
///        [out-dir] [--models &lt;dir&gt;] [--sample N] [--subset-sample M] [--dpi D] [--seed S] [--subset a,b,c]
/// </summary>
internal static class TableProbeRunner
{
    private sealed record Candidate(
        string Pdf, string Corpus, int PageIndex0, int RulingLines, double ColAlign, int TextRuns, bool InSubset);

    private static readonly string[] DefaultSubset =
        { "Test-Data", "Test-Data-4", "Test-Data-7", "Test-Data-17", "Test-Data-26" };

    public static int Run(string[] args)
    {
        if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine("usage: --table-probe <pdf-dir> [out-dir] [--models <dir>] [--sample N] " +
                                    "[--subset-sample M] [--dpi D] [--seed S] [--subset a,b,c]");
            return 2;
        }

        string pdfDir = Path.GetFullPath(args[0]);
        string outDir = "verification-out";
        string modelsDir = "models";
        int sample = 400, subsetSample = 200, dpi = 300, seed = 12345;
        string[] subset = DefaultSubset;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--models" when i + 1 < args.Length: modelsDir = args[++i]; break;
                case "--sample" when i + 1 < args.Length: sample = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--subset-sample" when i + 1 < args.Length: subsetSample = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--dpi" when i + 1 < args.Length: dpi = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seed" when i + 1 < args.Length: seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--subset" when i + 1 < args.Length:
                    subset = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                default:
                    if (!args[i].StartsWith("--", StringComparison.Ordinal)) outDir = args[i];
                    break;
            }
        }
        Directory.CreateDirectory(outDir);

        string layoutModel = Path.Combine(modelsDir, ModelCatalog.LayoutDetection.FileName);
        if (!File.Exists(layoutModel))
        {
            Console.Error.WriteLine($"Layout model not found: {layoutModel}. Run scripts/download-models.sh " +
                                    "or pass --models <dir>.");
            return 2;
        }

        // Phase A — collect born-digital TableOrComplexLayout candidate pages (ZeroDep only, fast, parallel).
        var pdfs = Directory.EnumerateFiles(pdfDir, "*.pdf", SearchOption.AllDirectories).ToList();
        Console.WriteLine($"Scanning {pdfs.Count:N0} PDFs for born-digital table-class pages…");
        var candidates = new System.Collections.Concurrent.ConcurrentBag<Candidate>();
        Parallel.ForEach(pdfs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, pdf =>
        {
            string rel = Path.GetRelativePath(pdfDir, pdf);
            string corpus = rel.Split(Path.DirectorySeparatorChar, 2)[0];
            bool inSubset = subset.Contains(corpus, StringComparer.Ordinal);
            try
            {
                ZD.DocumentAnalysis analysis;
                using (var fs = File.OpenRead(pdf)) analysis = PdfAnalyzer.Analyze(fs);
                foreach (var pc in analysis.Pages)
                {
                    if (pc.Class != ZD.PageContentClass.TableOrComplexLayout) continue;
                    if (pc.Signals.IsImageOnly || pc.Signals.OcrLayerPresent) continue;   // born-digital only
                    candidates.Add(new Candidate(
                        pdf, corpus, pc.PageIndex, pc.Signals.RulingLineCount,
                        pc.Signals.ColumnAlignmentScore, pc.Signals.TextRunCount, inSubset));
                }
            }
            catch { /* failure-isolated: skip unreadable docs */ }
        });

        var all = candidates.ToList();
        Console.WriteLine($"Found {all.Count:N0} born-digital table-class pages " +
                          $"({all.Count(c => c.InSubset):N0} in customer subset).");
        if (all.Count == 0) { Console.Error.WriteLine("No candidates."); return 1; }

        var mainSample = Sample(all, sample, seed);
        var subSample = Sample(all.Where(c => c.InSubset), subsetSample, seed);
        var union = mainSample.Concat(subSample)
            .GroupBy(c => (c.Pdf, c.PageIndex0)).Select(g => g.First()).ToList();

        // Phase B — render + detect on the sample only (models load once; sequential for ONNX safety).
        Console.WriteLine($"Rendering + detecting {union.Count:N0} sampled pages at {dpi} DPI…");
        var results = new Dictionary<(string, int), (bool HasTable, int TableRegions, float MaxConf, bool Failed)>();
        using var detector = new DocLayoutNetDetector(layoutModel);
        var renderer = new PdfPageRenderer();
        int done = 0;
        foreach (var grp in union.GroupBy(c => c.Pdf))
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(grp.Key); }
            catch { foreach (var c in grp) results[(c.Pdf, c.PageIndex0)] = (false, 0, 0, true); continue; }

            foreach (var c in grp)
            {
                try
                {
                    var img = renderer.Render(bytes, c.PageIndex0 + 1, dpi);
                    var tables = detector.Detect(img).Where(r => r.Type == RegionType.Table).ToList();
                    results[(c.Pdf, c.PageIndex0)] =
                        (tables.Count > 0, tables.Count, tables.Count > 0 ? tables.Max(t => t.Confidence) : 0f, false);
                }
                catch { results[(c.Pdf, c.PageIndex0)] = (false, 0, 0, true); }

                if (++done % 50 == 0) Console.WriteLine($"  …{done:N0}/{union.Count:N0}");
            }
        }

        WriteCsv(Path.Combine(outDir, "table-probe.csv"), union, results);
        PrintAndWriteSummary(outDir, all, mainSample, subSample, results, sample, dpi);
        return 0;
    }

    private static List<Candidate> Sample(IEnumerable<Candidate> pool, int n, int seed)
    {
        var ordered = pool.OrderBy(c => c.Pdf, StringComparer.Ordinal).ThenBy(c => c.PageIndex0).ToList();
        var rng = new Random(seed);
        return ordered.OrderBy(_ => rng.Next()).Take(n).ToList();
    }

    private static double HitRate(
        IReadOnlyList<Candidate> s, Dictionary<(string, int), (bool HasTable, int, float, bool Failed)> r)
    {
        var scored = s.Where(c => !r[(c.Pdf, c.PageIndex0)].Failed).ToList();
        return scored.Count == 0 ? 0 : 100.0 * scored.Count(c => r[(c.Pdf, c.PageIndex0)].HasTable) / scored.Count;
    }

    private static void PrintAndWriteSummary(
        string outDir, List<Candidate> all, List<Candidate> mainSample, List<Candidate> subSample,
        Dictionary<(string, int), (bool HasTable, int TableRegions, float MaxConf, bool Failed)> results,
        int requestedSample, int dpi)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== TABLE-PROBE SUMMARY (real-table hit rate on born-digital table-class pages) ===");
        sb.AppendLine($"born-digital table-class pages (population): {all.Count:N0}");
        sb.AppendLine($"render DPI: {dpi}   requested sample: {requestedSample:N0}");
        sb.AppendLine();
        sb.AppendLine($"OVERALL real-table hit rate: {HitRate(mainSample, results):0.0}%  (n={Scored(mainSample, results)})");
        sb.AppendLine($"CUSTOMER-SUBSET hit rate:    {HitRate(subSample, results):0.0}%  (n={Scored(subSample, results)})");
        int failed = mainSample.Concat(subSample).Distinct().Count(c => results[(c.Pdf, c.PageIndex0)].Failed);
        if (failed > 0) sb.AppendLine($"(render/detect failures excluded: {failed})");
        sb.AppendLine();
        sb.AppendLine("Interpretation: LOW hit rate → hint over-fires → sharpen it (reclaim ceiling, no fidelity loss).");
        sb.AppendLine("                HIGH hit rate → genuine tables → reclaim only via per-consumer policy knob.");
        sb.AppendLine();

        // Signal buckets (on the overall sample) — to find a threshold that separates real tables from over-fire.
        sb.AppendLine("Hit rate by RulingLineCount bucket (overall sample):");
        foreach (var (label, lo, hi) in new[] { ("0", 0, 0), ("1-9", 1, 9), ("10-29", 10, 29), ("30+", 30, int.MaxValue) })
            sb.AppendLine($"  ruling {label,-6} {BucketHit(mainSample, results, c => c.RulingLines >= lo && c.RulingLines <= hi)}");
        sb.AppendLine("Hit rate by ColumnAlignmentScore bucket (overall sample):");
        foreach (var (label, lo, hi) in new[] { ("<0.3", 0.0, 0.3), ("0.3-0.6", 0.3, 0.6), ("0.6+", 0.6, 1.01) })
            sb.AppendLine($"  colAlign {label,-7} {BucketHit(mainSample, results, c => c.ColAlign >= lo && c.ColAlign < hi)}");

        string summary = sb.ToString();
        Console.WriteLine();
        Console.WriteLine(summary);
        File.WriteAllText(Path.Combine(outDir, "table-probe-summary.txt"), summary);
    }

    private static int Scored(
        IEnumerable<Candidate> s, Dictionary<(string, int), (bool, int, float, bool Failed)> r)
        => s.Count(c => !r[(c.Pdf, c.PageIndex0)].Failed);

    private static string BucketHit(
        List<Candidate> sample, Dictionary<(string, int), (bool HasTable, int, float, bool Failed)> r,
        Func<Candidate, bool> pred)
    {
        var inBucket = sample.Where(pred).Where(c => !r[(c.Pdf, c.PageIndex0)].Failed).ToList();
        if (inBucket.Count == 0) return "   n/a (0)";
        double hit = 100.0 * inBucket.Count(c => r[(c.Pdf, c.PageIndex0)].HasTable) / inBucket.Count;
        return $"{hit,5:0.0}%  (n={inBucket.Count})";
    }

    private static void WriteCsv(
        string path, List<Candidate> union,
        Dictionary<(string, int), (bool HasTable, int TableRegions, float MaxConf, bool Failed)> results)
    {
        using var w = new StreamWriter(path, append: false, Encoding.UTF8);
        w.WriteLine("path,corpus,page1based,inSubset,rulingLines,colAlign,textRuns,hasTable,tableRegions,maxTableConf,renderFailed");
        foreach (var c in union.OrderBy(c => c.Pdf, StringComparer.Ordinal).ThenBy(c => c.PageIndex0))
        {
            var r = results[(c.Pdf, c.PageIndex0)];
            w.WriteLine(string.Join(',',
                Csv(c.Pdf), Csv(c.Corpus), c.PageIndex0 + 1, c.InSubset, c.RulingLines,
                c.ColAlign.ToString("0.000", CultureInfo.InvariantCulture), c.TextRuns,
                r.HasTable, r.TableRegions, r.MaxConf.ToString("0.000", CultureInfo.InvariantCulture), r.Failed));
        }
    }

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
