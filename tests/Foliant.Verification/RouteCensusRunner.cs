using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Foliant.Orchestration;
using ZeroDep;                       // PdfAnalyzer
using ZD = ZeroDep.Abstractions;

namespace Foliant.Verification;

/// <summary>
/// G1 routing census (ADR-0003). Runs <b>only</b> ZeroDep classification + the orchestrator's router over a
/// corpus — no Foliant rendering or models, so it is fast — and reports how the corpus would route:
/// the fast-lane vs heavy-lane page share (the decision-economics lever), the ZeroDep class distribution,
/// how many documents stop (integrity/decrypt) or whole-document-escalate, a per-corpus breakdown, and a
/// G1b sanity count of "suspect" fast-lane pages (a fast-laned page whose signals say it needs pixels).
///
/// Usage: dotnet run -c Release --project tests/Foliant.Verification -- --route-census &lt;pdf-dir&gt; [out-dir]
/// </summary>
internal static class RouteCensusRunner
{
    private sealed record DocRow(
        string RelPath, int Pages, int Fast, int Heavy, int Stop,
        bool Stopped, bool WholeDocEscalated, int Suspect,
        int TableTotal, int TableBornDigital, string Corpus, string? Error);

    // Default "customer subset" corpora (override with --subset a,b,c). These are the sets the user flagged
    // as the customer/representative mix; reported separately because the corpus blend is scan-heavy overall.
    private static readonly string[] DefaultSubset =
        { "Test-Data", "Test-Data-4", "Test-Data-7", "Test-Data-17", "Test-Data-26" };

    public static int Run(string[] args)
    {
        if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine("usage: --route-census <pdf-dir> [out-dir]");
            return 2;
        }

        string pdfDir = Path.GetFullPath(args[0]);
        string outDir = "verification-out";
        string[] subset = DefaultSubset;
        int reclaimRuling = 0;   // --reclaim-ruling N: fast-lane table-class pages with < N ruling lines
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--subset" && i + 1 < args.Length)
                subset = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            else if (args[i] == "--reclaim-ruling" && i + 1 < args.Length)
                reclaimRuling = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (!args[i].StartsWith("--", StringComparison.Ordinal))
                outDir = args[i];
        }
        Directory.CreateDirectory(outDir);

        if (!Directory.Exists(pdfDir))
        {
            Console.Error.WriteLine($"pdf-dir not found: {pdfDir}");
            return 2;
        }

        var pdfs = Directory.EnumerateFiles(pdfDir, "*.pdf", SearchOption.AllDirectories).ToList();
        Console.WriteLine($"Route census over {pdfs.Count:N0} PDFs in {pdfDir}");

        var options = new OrchestrationOptions { TableRulingLineThreshold = reclaimRuling };
        Console.WriteLine($"Table reclaim threshold: {reclaimRuling} ruling lines (0 = escalate all = baseline)");
        var reader = new ZeroDepClassificationReader();
        var rows = new ConcurrentBag<DocRow>();
        var classCounts = new ConcurrentDictionary<ZD.PageContentClass, int>();
        int done = 0;

        Parallel.ForEach(
            pdfs,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            pdf =>
            {
                string rel = Path.GetRelativePath(pdfDir, pdf);
                string corpus = rel.Split(Path.DirectorySeparatorChar, 2)[0];
                try
                {
                    ZD.DocumentAnalysis analysis;
                    using (var fs = File.OpenRead(pdf))
                        analysis = PdfAnalyzer.Analyze(fs);

                    var plan = RoutingPolicy.BuildPlan(reader.Read(analysis), options);

                    int tableTotal = 0, tableBorn = 0;
                    foreach (var pc in analysis.Pages)
                    {
                        classCounts.AddOrUpdate(pc.Class, 1, (_, c) => c + 1);
                        if (pc.Class == ZD.PageContentClass.TableOrComplexLayout)
                        {
                            tableTotal++;
                            // Born-digital table = embedded text layer, not image-dominant, not OCR-backed →
                            // reclaimable to the fast lane for text/RAG (its cells aren't reconstructed, but
                            // its text is exact). The rest genuinely need pixels.
                            if (!pc.Signals.IsImageOnly && !pc.Signals.OcrLayerPresent) tableBorn++;
                        }
                    }

                    // G1b sanity: a fast-laned page whose ZeroDep signals say it needs pixels.
                    var sigByIndex = analysis.Pages.ToDictionary(p => p.PageIndex);
                    int suspect = plan.Pages.Count(e =>
                        e.Lane == PageLane.Fast
                        && sigByIndex.TryGetValue(e.PageNumber - 1, out var pc)
                        && pc.Signals.IsImageOnly);

                    rows.Add(new DocRow(
                        rel, plan.Pages.Count, plan.FastLaneCount, plan.HeavyLaneCount,
                        plan.Pages.Count(e => e.Lane == PageLane.Stop),
                        plan.DocumentStopped, plan.WholeDocumentEscalated, suspect,
                        tableTotal, tableBorn, corpus, Error: null));
                }
                catch (Exception ex)
                {
                    rows.Add(new DocRow(rel, 0, 0, 0, 0, false, false, 0, 0, 0, corpus,
                        Error: ex.GetType().Name + ": " + ex.Message));
                }

                int n = Interlocked.Increment(ref done);
                if (n % 500 == 0) Console.WriteLine($"  …{n:N0}/{pdfs.Count:N0}");
            });

        WritePerDocCsv(Path.Combine(outDir, "route-census.csv"), rows);
        PrintAndWriteSummary(outDir, rows, classCounts, subset, reclaimRuling);
        return 0;
    }

    private static void WritePerDocCsv(string path, IEnumerable<DocRow> rows)
    {
        using var w = new StreamWriter(path, append: false, Encoding.UTF8);
        w.WriteLine("path,corpus,pages,fast,heavy,stop,stopped,wholeDocEscalated,escalationShare,suspectFastLane,tableTotal,tableBornDigital,error");
        foreach (var r in rows.OrderBy(r => r.RelPath, StringComparer.Ordinal))
        {
            double share = r.Pages > 0 ? (double)r.Heavy / r.Pages : 0;
            w.WriteLine(string.Join(',',
                Csv(r.RelPath), Csv(r.Corpus), r.Pages, r.Fast, r.Heavy, r.Stop,
                r.Stopped, r.WholeDocEscalated, share.ToString("0.000", CultureInfo.InvariantCulture),
                r.Suspect, r.TableTotal, r.TableBornDigital, Csv(r.Error ?? "")));
        }
    }

    private static void PrintAndWriteSummary(
        string outDir, IEnumerable<DocRow> rowsEnum, ConcurrentDictionary<ZD.PageContentClass, int> classCounts,
        string[] subset, int reclaimRuling)
    {
        var rows = rowsEnum.ToList();
        var ok = rows.Where(r => r.Error is null).ToList();
        int errors = rows.Count - ok.Count;

        long pages = ok.Sum(r => (long)r.Pages);
        long fast = ok.Sum(r => (long)r.Fast);
        long heavy = ok.Sum(r => (long)r.Heavy);
        long stop = ok.Sum(r => (long)r.Stop);
        long suspect = ok.Sum(r => (long)r.Suspect);
        int stoppedDocs = ok.Count(r => r.Stopped);
        int wholeDocEsc = ok.Count(r => r.WholeDocEscalated);

        double fastShare = pages > 0 ? 100.0 * fast / pages : 0;
        double heavyShare = pages > 0 ? 100.0 * heavy / pages : 0;

        var sb = new StringBuilder();
        sb.AppendLine("=== ROUTE CENSUS SUMMARY ===");
        sb.AppendLine($"table reclaim threshold: {reclaimRuling} ruling lines (0 = escalate all = baseline)");
        sb.AppendLine($"documents:           {rows.Count:N0}  (ok {ok.Count:N0}, errored {errors:N0})");
        sb.AppendLine($"pages:               {pages:N0}");
        sb.AppendLine($"FAST-LANE share:     {fastShare:0.0}%  ({fast:N0} pages)   <- the decision-economics lever");
        sb.AppendLine($"heavy-lane share:    {heavyShare:0.0}%  ({heavy:N0} pages)");
        sb.AppendLine($"stop pages:          {stop:N0}");
        sb.AppendLine($"docs stopped:        {stoppedDocs:N0}  (integrity/decrypt)");
        sb.AppendLine($"docs whole-doc-esc:  {wholeDocEsc:N0}  (>= {0.80:0%} pages heavy → ran whole-doc)");
        sb.AppendLine($"suspect fast-lane:   {suspect:N0}  (G1b: fast page with IsImageOnly — expect 0)");
        sb.AppendLine();
        sb.AppendLine("ZeroDep page-class distribution:");
        long classTotal = classCounts.Values.Sum();
        foreach (var kv in classCounts.OrderByDescending(k => k.Value))
            sb.AppendLine($"  {kv.Key,-22} {kv.Value,10:N0}  {(classTotal > 0 ? 100.0 * kv.Value / classTotal : 0),5:0.0}%");
        sb.AppendLine();
        sb.AppendLine("Per-corpus fast-lane share:");
        foreach (var g in ok.GroupBy(r => r.Corpus).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            long gp = g.Sum(r => (long)r.Pages), gf = g.Sum(r => (long)r.Fast);
            sb.AppendLine($"  {g.Key,-20} {(gp > 0 ? 100.0 * gf / gp : 0),5:0.0}%  ({gf:N0}/{gp:N0} pages, {g.Count():N0} docs)");
        }

        // The ceiling lever: how much of TableOrComplexLayout is born-digital (reclaimable to the fast lane
        // for text/RAG) vs genuinely scanned.
        long tableAll = ok.Sum(r => (long)r.TableTotal);
        long tableBornAll = ok.Sum(r => (long)r.TableBornDigital);
        long tableScanAll = tableAll - tableBornAll;
        sb.AppendLine();
        sb.AppendLine("=== TABLE/COMPLEX COMPOSITION (the fast-lane ceiling lever) ===");
        sb.AppendLine($"TableOrComplexLayout pages:  {tableAll:N0}");
        sb.AppendLine($"  born-digital (text layer):  {tableBornAll:N0}  {(tableAll > 0 ? 100.0 * tableBornAll / tableAll : 0),5:0.0}%   <- reclaimable to fast lane (text/RAG)");
        sb.AppendLine($"  scanned / OCR-backed:       {tableScanAll:N0}  {(tableAll > 0 ? 100.0 * tableScanAll / tableAll : 0),5:0.0}%   <- genuinely need pixels");
        if (reclaimRuling == 0)
            sb.AppendLine($"PROJECTED fast-lane ceiling if born-digital tables fast-laned: " +
                          $"{(pages > 0 ? 100.0 * (fast + tableBornAll) / pages : 0):0.0}%   (now {fastShare:0.0}%)");
        else
            sb.AppendLine($"(projection omitted — a reclaim threshold is active; the {fastShare:0.0}% fast-lane share above already reflects it)");

        // Customer/representative subset, reported separately (the blend is scan-heavy overall).
        var sub = ok.Where(r => subset.Contains(r.Corpus, StringComparer.Ordinal)).ToList();
        if (sub.Count > 0)
        {
            long sp = sub.Sum(r => (long)r.Pages), sf = sub.Sum(r => (long)r.Fast);
            long stt = sub.Sum(r => (long)r.TableTotal), stb = sub.Sum(r => (long)r.TableBornDigital);
            sb.AppendLine();
            sb.AppendLine($"=== CUSTOMER SUBSET [{string.Join(", ", subset)}] ===");
            sb.AppendLine($"docs / pages:                {sub.Count:N0} / {sp:N0}");
            sb.AppendLine($"fast-lane share now:         {(sp > 0 ? 100.0 * sf / sp : 0):0.0}%  ({sf:N0} pages)");
            sb.AppendLine($"table born-digital:          {stb:N0} of {stt:N0} table pages");
            if (reclaimRuling == 0)
                sb.AppendLine($"PROJECTED ceiling:           {(sp > 0 ? 100.0 * (sf + stb) / sp : 0):0.0}%");
        }

        string summary = sb.ToString();
        Console.WriteLine();
        Console.WriteLine(summary);
        File.WriteAllText(Path.Combine(outDir, "route-census-summary.txt"), summary);
    }

    private static string Csv(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
}
