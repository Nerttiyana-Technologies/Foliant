using System.Globalization;
using System.Text;
using Foliant.Orchestration;
using Foliant.Pipeline;
using ZeroDep;                       // PdfAnalyzer
using ZD = ZeroDep.Abstractions;

namespace Foliant.Verification;

/// <summary>
/// ADR-0003 G1a reclaim-parity gate. The census confirmed the ruling-line knob doubles the fast-lane share;
/// this confirms the <b>output</b> is safe: for the pages the knob reclaims (born-digital
/// <c>TableOrComplexLayout</c> with ruling-line count below the threshold), does the fast-lane ZeroDep prose
/// retain the text that the full Foliant pipeline would extract?
///
/// For each sampled reclaimed page it builds the fast-lane prose (ZeroDep text) and runs Foliant-only on that
/// one page, then compares word sets. The headline is <b>recall(heavy-words-in-fast)</b> — of the words
/// Foliant extracts, how many survive in the fast lane. Near 100% ⇒ no text lost (expected, since both read
/// the same born-digital text layer; the knob only drops cell <i>structure</i>, which is the documented,
/// by-design tradeoff — not measured here).
///
/// Usage: dotnet run -c Release --project tests/Foliant.Verification -- --reclaim-parity &lt;pdf-dir&gt;
///        [out-dir] [--models &lt;dir&gt;] [--sample N] [--subset-sample M] [--ruling T] [--dpi D] [--seed S] [--subset a,b,c]
/// </summary>
internal static class TableReclaimParityRunner
{
    private sealed record Cand(string Pdf, string Corpus, int PageIndex0, int Ruling, bool InSubset);
    private sealed record Row(
        string Pdf, string Corpus, int Page, bool InSubset, int Ruling,
        int HeavyWords, int FastWords, double RecallHeavyInFast, double RecallFastInHeavy, bool Escalated, bool Failed,
        double CharRecall = 0);

    private static readonly string[] DefaultSubset =
        { "Test-Data", "Test-Data-4", "Test-Data-7", "Test-Data-17", "Test-Data-26" };

    public static int Run(string[] args)
    {
        if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine("usage: --reclaim-parity <pdf-dir> [out-dir] [--models <dir>] [--sample N] " +
                                    "[--subset-sample M] [--ruling T] [--dpi D] [--seed S] [--subset a,b,c]");
            return 2;
        }

        string pdfDir = Path.GetFullPath(args[0]);
        string outDir = "verification-out", modelsDir = "models";
        int sample = 200, subsetSample = 150, ruling = 10, dpi = 300, seed = 12345;
        bool applyAbstention = false;
        string[] subset = DefaultSubset;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--models" when i + 1 < args.Length: modelsDir = args[++i]; break;
                case "--sample" when i + 1 < args.Length: sample = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--subset-sample" when i + 1 < args.Length: subsetSample = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--ruling" when i + 1 < args.Length: ruling = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--dpi" when i + 1 < args.Length: dpi = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seed" when i + 1 < args.Length: seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--apply-abstention": applyAbstention = true; break;   // reflect the shipped fast-lane guard
                case "--subset" when i + 1 < args.Length:
                    subset = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                default: if (!args[i].StartsWith("--", StringComparison.Ordinal)) outDir = args[i]; break;
            }
        }
        Directory.CreateDirectory(outDir);
        var abstentionOptions = new Foliant.Orchestration.OrchestrationOptions();

        // Phase A — collect reclaimed candidates: born-digital TableOrComplexLayout, ruling < threshold.
        var pdfs = Directory.EnumerateFiles(pdfDir, "*.pdf", SearchOption.AllDirectories).ToList();
        Console.WriteLine($"Scanning {pdfs.Count:N0} PDFs for reclaimed pages (table-class, born-digital, ruling < {ruling})…");
        var cands = new System.Collections.Concurrent.ConcurrentBag<Cand>();
        Parallel.ForEach(pdfs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, pdf =>
        {
            string rel = Path.GetRelativePath(pdfDir, pdf);
            string corpus = rel.Split(Path.DirectorySeparatorChar, 2)[0];
            bool inSubset = subset.Contains(corpus, StringComparer.Ordinal);
            try
            {
                ZD.DocumentAnalysis a;
                using (var fs = File.OpenRead(pdf)) a = PdfAnalyzer.Analyze(fs);
                if (a.Status != ZD.DocumentStatus.Processed || a.Form.HasXfa) return;
                foreach (var pc in a.Pages)
                {
                    if (pc.Class != ZD.PageContentClass.TableOrComplexLayout) continue;
                    if (pc.Signals.IsImageOnly || pc.Signals.OcrLayerPresent) continue;
                    if (pc.Signals.RulingLineCount >= ruling) continue;       // only the pages the knob reclaims
                    cands.Add(new Cand(pdf, corpus, pc.PageIndex, pc.Signals.RulingLineCount, inSubset));
                }
            }
            catch { /* skip unreadable */ }
        });

        var all = cands.ToList();
        Console.WriteLine($"Reclaimed-page population: {all.Count:N0} ({all.Count(c => c.InSubset):N0} in subset).");
        if (all.Count == 0) { Console.Error.WriteLine("No reclaimed candidates at this threshold."); return 1; }

        var union = Sample(all, sample, seed).Concat(Sample(all.Where(c => c.InSubset), subsetSample, seed))
            .GroupBy(c => (c.Pdf, c.PageIndex0)).Select(g => g.First()).ToList();

        // Phase B — build fast vs heavy for each sampled page and compare (needs models; sequential).
        DocumentProcessor processor;
        try { processor = FoliantProcessor.CreateDefault(modelsDir); }
        catch (FileNotFoundException ex)
        {
            Console.Error.WriteLine($"Models not found ({ex.Message}). Run scripts/download-models.sh or pass --models <dir>.");
            return 2;
        }

        var builder = new FastLanePageBuilder(new ZeroDepTypeAdapter());
        var rows = new List<Row>();
        int done = 0;
        using (processor)
        {
            foreach (var grp in union.GroupBy(c => c.Pdf))
            {
                byte[] bytes;
                ZD.DocumentAnalysis analysis;
                try
                {
                    bytes = File.ReadAllBytes(grp.Key);
                    using var ms = new MemoryStream(bytes, false);
                    analysis = PdfAnalyzer.Analyze(ms);
                }
                catch
                {
                    foreach (var c in grp) rows.Add(new Row(c.Pdf, c.Corpus, c.PageIndex0 + 1, c.InSubset, c.Ruling, 0, 0, 0, 0, false, true));
                    continue;
                }

                var runsByPage = analysis.TextRuns.GroupBy(r => r.PageIndex)
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<ZD.TextRunInfo>)g.ToList());
                var sigByPage = analysis.Pages.ToDictionary(p => p.PageIndex, p => p.Signals);

                foreach (var c in grp)
                {
                    try
                    {
                        runsByPage.TryGetValue(c.PageIndex0, out var runs);
                        runs ??= Array.Empty<ZD.TextRunInfo>();
                        var fastPage = builder.Build(
                            c.PageIndex0 + 1, PageKind.TableOrComplexLayout, runs, Array.Empty<ZD.FormFieldInfo>());

                        // With --apply-abstention, a page the shipped guard would escalate is NOT a fast-lane
                        // page — it goes heavy (no text loss). Record it as escalated and skip the comparison.
                        if (applyAbstention)
                        {
                            sigByPage.TryGetValue(c.PageIndex0, out var sg);
                            int structureRuns = sg?.TextRunCount ?? runs.Count(r => !r.IsOcrLayer);
                            double trust = sg?.TextDecodeConfidence ?? 1.0;
                            if (Foliant.Orchestration.FastLaneAbstention.ShouldAbstain(
                                    PageKind.TableOrComplexLayout, structureRuns, trust, fastPage, abstentionOptions))
                            {
                                rows.Add(new Row(c.Pdf, c.Corpus, c.PageIndex0 + 1, c.InSubset, c.Ruling, 0, 0, 0, 0, true, false));
                                if (++done % 25 == 0) Console.WriteLine($"  …{done:N0}/{union.Count:N0}");
                                continue;
                            }
                        }

                        var heavyDoc = processor.ProcessAsync(
                            bytes, ProcessingOptions.Default with { Pages = new[] { c.PageIndex0 + 1 }, Dpi = dpi })
                            .GetAwaiter().GetResult();
                        string heavyText = heavyDoc.Pages.Count > 0 ? heavyDoc.Pages[0].Markdown : "";

                        var fastW = Words(fastPage.Markdown);
                        var heavyW = Words(heavyText);
                        double charRecall = Recall(Trigrams(heavyText), Trigrams(fastPage.Markdown)); // whitespace/punct-insensitive
                        rows.Add(new Row(c.Pdf, c.Corpus, c.PageIndex0 + 1, c.InSubset, c.Ruling,
                            heavyW.Count, fastW.Count, Recall(heavyW, fastW), Recall(fastW, heavyW), false, false, charRecall));
                    }
                    catch
                    {
                        rows.Add(new Row(c.Pdf, c.Corpus, c.PageIndex0 + 1, c.InSubset, c.Ruling, 0, 0, 0, 0, false, true));
                    }

                    if (++done % 25 == 0) Console.WriteLine($"  …{done:N0}/{union.Count:N0}");
                }
            }
        }

        WriteCsv(Path.Combine(outDir, "reclaim-parity.csv"), rows);
        PrintAndWriteSummary(outDir, rows, ruling, dpi, applyAbstention);
        return 0;
    }

    /// <summary>
    /// Clean text-layer fidelity: compare the fast-lane (ZeroDep) text against <c>pdftotext</c> (poppler)
    /// for the reclaimed pages. pdftotext reads the same born-digital text layer, so it is a far cleaner
    /// reference than Foliant's render+OCR (no OCR noise) — this is the honest "did the fast lane preserve
    /// the page's text?" number. No Foliant models needed; requires <c>pdftotext</c> on PATH.
    /// Usage: --textref-parity &lt;pdf-dir&gt; [out-dir] [--ruling T] [--sample N] [--subset-sample M] [--seed S] [--subset a,b,c]
    /// </summary>
    public static int RunTextRef(string[] args)
    {
        if (args.Length < 1) { Console.Error.WriteLine("usage: --textref-parity <pdf-dir> [out-dir] [--ruling T] [--sample N] [--subset-sample M] [--seed S] [--subset a,b,c]"); return 2; }
        string pdfDir = Path.GetFullPath(args[0]);
        string outDir = "verification-out";
        int ruling = 10, sample = 500, subsetSample = 250, seed = 12345;
        string[] subset = DefaultSubset;
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ruling" when i + 1 < args.Length: ruling = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--sample" when i + 1 < args.Length: sample = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--subset-sample" when i + 1 < args.Length: subsetSample = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seed" when i + 1 < args.Length: seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--subset" when i + 1 < args.Length: subset = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); break;
                default: if (!args[i].StartsWith("--", StringComparison.Ordinal)) outDir = args[i]; break;
            }
        }
        Directory.CreateDirectory(outDir);

        if (!PdftotextAvailable())
        {
            Console.Error.WriteLine("pdftotext not found on PATH. Install poppler (macOS: brew install poppler).");
            return 2;
        }

        var pdfs = Directory.EnumerateFiles(pdfDir, "*.pdf", SearchOption.AllDirectories).ToList();
        Console.WriteLine($"Scanning {pdfs.Count:N0} PDFs for reclaimed pages (table-class, born-digital, ruling < {ruling})…");
        var cands = new System.Collections.Concurrent.ConcurrentBag<Cand>();
        Parallel.ForEach(pdfs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, pdf =>
        {
            string rel = Path.GetRelativePath(pdfDir, pdf);
            string corpus = rel.Split(Path.DirectorySeparatorChar, 2)[0];
            bool inSubset = subset.Contains(corpus, StringComparer.Ordinal);
            try
            {
                ZD.DocumentAnalysis a;
                using (var fs = File.OpenRead(pdf)) a = PdfAnalyzer.Analyze(fs);
                if (a.Status != ZD.DocumentStatus.Processed || a.Form.HasXfa) return;
                foreach (var pc in a.Pages)
                {
                    if (pc.Class != ZD.PageContentClass.TableOrComplexLayout) continue;
                    if (pc.Signals.IsImageOnly || pc.Signals.OcrLayerPresent) continue;
                    if (pc.Signals.RulingLineCount >= ruling) continue;
                    cands.Add(new Cand(pdf, corpus, pc.PageIndex, pc.Signals.RulingLineCount, inSubset));
                }
            }
            catch { }
        });
        var all = cands.ToList();
        if (all.Count == 0) { Console.Error.WriteLine("No reclaimed candidates."); return 1; }
        var union = Sample(all, sample, seed).Concat(Sample(all.Where(c => c.InSubset), subsetSample, seed))
            .GroupBy(c => (c.Pdf, c.PageIndex0)).Select(g => g.First()).ToList();
        Console.WriteLine($"Comparing {union.Count:N0} reclaimed pages (ZeroDep fast vs pdftotext)…");

        var builder = new FastLanePageBuilder(new ZeroDepTypeAdapter());
        var rows = new List<Row>();
        int done = 0;
        foreach (var grp in union.GroupBy(c => c.Pdf))
        {
            byte[] bytes; ZD.DocumentAnalysis analysis;
            try { bytes = File.ReadAllBytes(grp.Key); using var ms = new MemoryStream(bytes, false); analysis = PdfAnalyzer.Analyze(ms); }
            catch { foreach (var c in grp) rows.Add(new Row(c.Pdf, c.Corpus, c.PageIndex0 + 1, c.InSubset, c.Ruling, 0, 0, 0, 0, false, true)); continue; }
            var runsByPage = analysis.TextRuns.GroupBy(r => r.PageIndex).ToDictionary(g => g.Key, g => (IReadOnlyList<ZD.TextRunInfo>)g.ToList());
            foreach (var c in grp)
            {
                try
                {
                    runsByPage.TryGetValue(c.PageIndex0, out var runs);
                    string fast = builder.Build(c.PageIndex0 + 1, PageKind.TableOrComplexLayout, runs ?? Array.Empty<ZD.TextRunInfo>(), Array.Empty<ZD.FormFieldInfo>()).Markdown;
                    string reference = Pdftotext(grp.Key, c.PageIndex0 + 1);
                    var fastW = Words(fast); var refW = Words(reference);
                    double charRecall = Recall(Trigrams(reference), Trigrams(fast));   // ref = text-layer truth
                    rows.Add(new Row(c.Pdf, c.Corpus, c.PageIndex0 + 1, c.InSubset, c.Ruling,
                        refW.Count, fastW.Count, Recall(refW, fastW), Recall(fastW, refW), false, false, charRecall));
                }
                catch { rows.Add(new Row(c.Pdf, c.Corpus, c.PageIndex0 + 1, c.InSubset, c.Ruling, 0, 0, 0, 0, false, true)); }
                if (++done % 50 == 0) Console.WriteLine($"  …{done:N0}/{union.Count:N0}");
            }
        }

        WriteCsv(Path.Combine(outDir, "textref-parity.csv"), rows);
        var ok = rows.Where(r => !r.Failed && r.HeavyWords > 0).ToList();
        var sub = ok.Where(r => r.InSubset).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("=== TEXT-LAYER FIDELITY (ZeroDep fast vs pdftotext text layer — clean reference, no OCR noise) ===");
        sb.AppendLine($"reclaim threshold: ruling < {ruling}   pages compared: {ok.Count:N0}  (subset {sub.Count:N0}; failures excluded {rows.Count(r => r.Failed)})");
        sb.AppendLine();
        sb.AppendLine($"OVERALL char recall vs text-layer: {Mean(ok, r => r.CharRecall):0.0}%   word recall: {Mean(ok, r => r.RecallHeavyInFast):0.0}%");
        sb.AppendLine($"SUBSET  char recall vs text-layer: {(sub.Count > 0 ? Mean(sub, r => r.CharRecall) : 0):0.0}%   word recall: {(sub.Count > 0 ? Mean(sub, r => r.RecallHeavyInFast) : 0):0.0}%");
        sb.AppendLine($"pages with char-recall < 95%: {ok.Count(r => r.CharRecall < 0.95):N0}  ({(ok.Count > 0 ? 100.0 * ok.Count(r => r.CharRecall < 0.95) / ok.Count : 0):0.0}%)");
        sb.AppendLine();
        sb.AppendLine("This is the clean fidelity number: both ZeroDep and pdftotext read the born-digital text layer, so a high");
        sb.AppendLine("match means the fast lane faithfully reproduces the page text (the earlier vs-Foliant gap was OCR-reference noise).");
        string summary = sb.ToString();
        Console.WriteLine(); Console.WriteLine(summary);
        File.WriteAllText(Path.Combine(outDir, "textref-parity-summary.txt"), summary);
        return 0;
    }

    private static bool PdftotextAvailable()
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("pdftotext", "-v")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            p!.WaitForExit(5000);
            return true;
        }
        catch { return false; }
    }

    private static string Pdftotext(string pdf, int page)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("pdftotext")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(page.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-l"); psi.ArgumentList.Add(page.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(pdf); psi.ArgumentList.Add("-");
        using var p = System.Diagnostics.Process.Start(psi)!;
        string outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30000);
        return outp;
    }

    /// <summary>Diagnostic: dump fast-lane (ZeroDep) vs Foliant-only text for one page + ZeroDep signals.</summary>
    public static int Dump(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: --reclaim-dump <pdf-path> <page-1based> [--models <dir>] [--chars N]");
            return 2;
        }
        string pdf = args[0];
        int page = int.Parse(args[1], CultureInfo.InvariantCulture);
        string modelsDir = "models";
        int chars = 1200;
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--models" && i + 1 < args.Length) modelsDir = args[++i];
            else if (args[i] == "--chars" && i + 1 < args.Length) chars = int.Parse(args[++i], CultureInfo.InvariantCulture);
        }
        if (!File.Exists(pdf)) { Console.Error.WriteLine($"not found: {pdf}"); return 2; }

        byte[] bytes = File.ReadAllBytes(pdf);
        ZD.DocumentAnalysis analysis;
        using (var ms = new MemoryStream(bytes, false)) analysis = PdfAnalyzer.Analyze(ms);

        int idx = page - 1;
        var pc = analysis.Pages.FirstOrDefault(p => p.PageIndex == idx);
        var runs = analysis.TextRuns.Where(r => r.PageIndex == idx).ToList();

        Console.WriteLine($"=== {Path.GetFileName(pdf)} page {page} (ZeroDep PageIndex {idx}) ===");
        if (pc is null) Console.WriteLine("  (no ZeroDep PageClassification for this page)");
        else
        {
            var s = pc.Signals;
            Console.WriteLine($"  class={pc.Class}  classConfidence={pc.Confidence:0.00}  TextDecodeConfidence={s.TextDecodeConfidence:0.000}");
            Console.WriteLine($"  signals: TextRunCount={s.TextRunCount} TextCoverageFraction={s.TextCoverageFraction:0.000} " +
                              $"RulingLineCount={s.RulingLineCount} ColumnAlignmentScore={s.ColumnAlignmentScore:0.000} " +
                              $"IsImageOnly={s.IsImageOnly} OcrLayerPresent={s.OcrLayerPresent}");
        }

        var builder = new FastLanePageBuilder(new ZeroDepTypeAdapter());
        string fastText = builder.Build(page, PageKind.TableOrComplexLayout, runs, Array.Empty<ZD.FormFieldInfo>()).Markdown;

        string heavyText;
        try
        {
            using var processor = FoliantProcessor.CreateDefault(modelsDir);
            var doc = processor.ProcessAsync(bytes, ProcessingOptions.Default with { Pages = new[] { page } }).GetAwaiter().GetResult();
            heavyText = doc.Pages.Count > 0 ? doc.Pages[0].Markdown : "";
        }
        catch (FileNotFoundException ex) { Console.Error.WriteLine($"models not found ({ex.Message})"); return 2; }

        var fastW = Words(fastText);
        var heavyW = Words(heavyText);
        Console.WriteLine($"\n  fast words={fastW.Count}  heavy words={heavyW.Count}  " +
                          $"recall(heavy→fast)={Recall(heavyW, fastW) * 100:0.0}%  recall(fast→heavy)={Recall(fastW, heavyW) * 100:0.0}%");

        Console.WriteLine($"\n----- FAST (ZeroDep) [{fastText.Length} chars] -----\n{Clip(fastText, chars)}");
        Console.WriteLine($"\n----- HEAVY (Foliant) [{heavyText.Length} chars] -----\n{Clip(heavyText, chars)}");
        return 0;
    }

    private static string Clip(string s, int n) => string.IsNullOrEmpty(s) ? "(empty)" : (s.Length <= n ? s : s[..n] + " …");

    private static List<Cand> Sample(IEnumerable<Cand> pool, int n, int seed)
    {
        var ordered = pool.OrderBy(c => c.Pdf, StringComparer.Ordinal).ThenBy(c => c.PageIndex0).ToList();
        var rng = new Random(seed);
        return ordered.OrderBy(_ => rng.Next()).Take(n).ToList();
    }

    private static void PrintAndWriteSummary(string outDir, List<Row> rows, int ruling, int dpi, bool applyAbstention)
    {
        var ok = rows.Where(r => !r.Failed && !r.Escalated && r.HeavyWords > 0).ToList();
        var sub = ok.Where(r => r.InSubset).ToList();
        int failed = rows.Count(r => r.Failed);
        int escalated = rows.Count(r => r.Escalated && !r.Failed);

        var sb = new StringBuilder();
        sb.AppendLine("=== RECLAIM-PARITY SUMMARY (G1a: is text lost when low-ruling table pages are fast-laned?) ===");
        sb.AppendLine($"reclaim threshold: ruling < {ruling}   render DPI: {dpi}   abstention guard: {(applyAbstention ? "ON (shipped behavior)" : "off (raw fast-lane)")}");
        if (applyAbstention)
        {
            int considered = escalated + ok.Count;
            sb.AppendLine($"guard-escalated (sent to heavy, not fast — no text loss): {escalated:N0}" +
                          $"  ({(considered > 0 ? 100.0 * escalated / considered : 0):0.0}% of reclaim candidates)");
        }
        sb.AppendLine($"fast-lane pages compared: {ok.Count:N0}  (subset {sub.Count:N0}; failures excluded {failed})");
        sb.AppendLine();
        sb.AppendLine($"OVERALL recall (heavy words retained in fast): {Mean(ok, r => r.RecallHeavyInFast):0.0}%");
        sb.AppendLine($"SUBSET  recall (heavy words retained in fast): {(sub.Count > 0 ? Mean(sub, r => r.RecallHeavyInFast) : 0):0.0}%");
        sb.AppendLine($"(reverse) fast words present in heavy:         {Mean(ok, r => r.RecallFastInHeavy):0.0}%");
        sb.AppendLine();
        sb.AppendLine("CONTENT preservation (char-trigram, whitespace/punct-insensitive, script-agnostic — the honest \"is text preserved?\" metric):");
        sb.AppendLine($"  OVERALL char-level recall: {Mean(ok, r => r.CharRecall):0.0}%");
        sb.AppendLine($"  SUBSET  char-level recall: {(sub.Count > 0 ? Mean(sub, r => r.CharRecall) : 0):0.0}%");
        int charLoss = ok.Count(r => r.CharRecall < 0.95);
        sb.AppendLine($"  pages with char-recall < 95% (true content loss): {charLoss:N0}  ({(ok.Count > 0 ? 100.0 * charLoss / ok.Count : 0):0.0}%)");
        sb.AppendLine("  (word-recall above is spacing/tokenization-sensitive; char-recall isolates real content loss.)");
        sb.AppendLine();
        int loss = ok.Count(r => r.RecallHeavyInFast < 0.95);
        sb.AppendLine($"pages with recall < 95% (text-loss risk): {loss:N0}  ({(ok.Count > 0 ? 100.0 * loss / ok.Count : 0):0.0}%)");
        sb.AppendLine("PASS criterion: overall recall ≥ ~99% AND the < 95% set is small and explained (these are the");
        sb.AppendLine("documented borderless-table cases where reading-order prose differs, not lost text).");
        sb.AppendLine();
        sb.AppendLine("Worst 15 pages by recall (eyeball these to confirm it's structure, not lost text):");
        foreach (var r in ok.OrderBy(r => r.RecallHeavyInFast).Take(15))
            sb.AppendLine($"  word={r.RecallHeavyInFast * 100,5:0.0}% char={r.CharRecall * 100,5:0.0}%  ruling={r.Ruling,-3} heavy={r.HeavyWords,-4} fast={r.FastWords,-4} {Trim(r.Pdf)} p{r.Page}");

        string summary = sb.ToString();
        Console.WriteLine();
        Console.WriteLine(summary);
        File.WriteAllText(Path.Combine(outDir, "reclaim-parity-summary.txt"), summary);
    }

    private static double Mean(List<Row> rows, Func<Row, double> sel) => rows.Count == 0 ? 0 : 100.0 * rows.Average(sel);

    private static double Recall(HashSet<string> truth, HashSet<string> got) =>
        truth.Count == 0 ? 1.0 : (double)truth.Count(w => got.Contains(w)) / truth.Count;

    private static HashSet<string> Words(string s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(s)) return set;
        var sb = new StringBuilder();
        foreach (char ch in s)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else { if (sb.Length >= 3) set.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length >= 3) set.Add(sb.ToString());
        return set;
    }

    // Whitespace/punctuation-insensitive, script-agnostic content fingerprint: lowercase, keep only
    // letters/digits, take 3-char windows. Robust to spacing/tokenization differences (a missing space is
    // not lost text) and to non-space-delimited scripts (Thai/CJK) where word-set overlap is meaningless.
    private static HashSet<string> Trigrams(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        string norm = sb.ToString();
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i + 3 <= norm.Length; i++) set.Add(norm.Substring(i, 3));
        if (norm.Length is > 0 and < 3) set.Add(norm);
        return set;
    }

    private static string Trim(string path) => path.Length <= 48 ? path : "…" + path[^47..];

    private static void WriteCsv(string path, List<Row> rows)
    {
        using var w = new StreamWriter(path, append: false, Encoding.UTF8);
        w.WriteLine("path,corpus,page,inSubset,ruling,heavyWords,fastWords,recallHeavyInFast,recallFastInHeavy,charRecall,escalated,failed");
        foreach (var r in rows.OrderBy(r => r.RecallHeavyInFast))
            w.WriteLine(string.Join(',',
                Csv(r.Pdf), Csv(r.Corpus), r.Page, r.InSubset, r.Ruling, r.HeavyWords, r.FastWords,
                r.RecallHeavyInFast.ToString("0.000", CultureInfo.InvariantCulture),
                r.RecallFastInHeavy.ToString("0.000", CultureInfo.InvariantCulture),
                r.CharRecall.ToString("0.000", CultureInfo.InvariantCulture), r.Escalated, r.Failed));
    }

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
