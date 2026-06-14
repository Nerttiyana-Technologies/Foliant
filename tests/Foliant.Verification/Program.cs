// Quality-gate harness — the spike scorecard (spike/RESULTS.md) rebuilt on the production
// pipeline. Runs locally against a PDF corpus that is never committed (Test-Data/), writes
// per-page Markdown + scorecard.csv into a gitignored output directory, and enforces:
//
//   Gate 1 (corpus recall):  avg word recall ≥ 98% AND ≥95% recall on ≥98% of scored pages
//   Gate 2 (zero text loss): coverage-invariant violations = 0 across the corpus
//
// Usage:
//   dotnet run -c Release --project tests/Foliant.Verification -- <pdf-dir> [out-dir] [--models <dir>]
//
// Defaults: out-dir = verification-out/, models = models/ (relative to current directory).

using System.Globalization;
using Foliant;
using Foliant.Pipeline;
using Foliant.Verification;

string? pdfDir = null;
string outDir = "verification-out";
string modelsDir = "models";
bool ocrOnly = false;
string? gate3Csv = null;
string? gate5Dir = null;
string? gate6Dir = null;
string? gate7Dir = null;
int gate7Pages = 2;
bool orientCheck = false;
int orientPages = 5;
bool noOrientation = false;
string? inspect = null;
var tableBackend = TableBackend.TableTransformer;
var readingOrder = ReadingOrderBackend.XyCutPlusPlus;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--models" && i + 1 < args.Length) { modelsDir = args[++i]; continue; }
    if (args[i] == "--ocr-only") { ocrOnly = true; continue; }
    if (args[i] == "--gate3" && i + 1 < args.Length) { gate3Csv = args[++i]; continue; }
    if (args[i] == "--gate5" && i + 1 < args.Length) { gate5Dir = args[++i]; continue; }
    if (args[i] == "--gate6" && i + 1 < args.Length) { gate6Dir = args[++i]; continue; }
    if (args[i] == "--gate7" && i + 1 < args.Length) { gate7Dir = args[++i]; continue; }
    if (args[i] == "--gate7-pages" && i + 1 < args.Length) { gate7Pages = int.Parse(args[++i]); continue; }
    if (args[i] == "--orient-check") { orientCheck = true; continue; }
    if (args[i] == "--orient-pages" && i + 1 < args.Length) { orientPages = int.Parse(args[++i]); continue; }
    if (args[i] == "--no-orientation") { noOrientation = true; continue; }
    if (args[i] == "--inspect" && i + 1 < args.Length) { inspect = args[++i]; continue; }
    if (args[i] == "--table-backend" && i + 1 < args.Length)
    {
        tableBackend = args[++i].ToLowerInvariant() switch
        {
            "slanet" or "paddlestructure" or "paddle" => TableBackend.PaddleStructure,
            "tt" or "tabletransformer" => TableBackend.TableTransformer,
            var v => throw new ArgumentException($"Unknown --table-backend '{v}' (use tt | slanet)"),
        };
        continue;
    }
    if (args[i] == "--reading-order" && i + 1 < args.Length)
    {
        readingOrder = args[++i].ToLowerInvariant() switch
        {
            "xycut++" or "xy++" or "plusplus" => ReadingOrderBackend.XyCutPlusPlus,
            "xycut" or "xy" => ReadingOrderBackend.XyCut,
            var v => throw new ArgumentException($"Unknown --reading-order '{v}' (use xycut | xycut++)"),
        };
        continue;
    }
    if (pdfDir == null) pdfDir = args[i];
    else outDir = args[i];
}

if (pdfDir == null || !Directory.Exists(pdfDir))
{
    Console.Error.WriteLine(
        "Usage: Foliant.Verification <pdf-dir> [out-dir] [--models <dir>] [--ocr-only] " +
        "[--gate3 <truth.csv>] [--gate5 <truth-dir>] [--gate6 <truth-dir>] " +
        "[--gate7 <born-digital-dir> [--gate7-pages N]] " +
        "[--orient-check [--orient-pages N]] [--no-orientation] " +
        "[--table-backend tt|slanet] [--reading-order xycut|xycut++]");
    return 2;
}

var pdfs = Directory.GetFiles(pdfDir, "*.pdf").OrderBy(p => p).ToList();
if (pdfs.Count == 0) { Console.Error.WriteLine($"No PDFs in {pdfDir}."); return 2; }

Directory.CreateDirectory(outDir);
using var processor = FoliantProcessor.CreateDefault(modelsDir, tableBackend, readingOrder);
if (tableBackend != TableBackend.TableTransformer)
    Console.WriteLine($"Table backend: {tableBackend}");
if (readingOrder != ReadingOrderBackend.XyCutPlusPlus)
    Console.WriteLine($"Reading order: {readingOrder}");

// --ocr-only forces TextLayerMode.Never: on born-digital corpora the default fast path takes
// words FROM the text layer while recall is measured AGAINST it (trivially ~100%, validates
// assembly only). OCR-only recall is the non-circular quality metric (spike baseline: 98.3%).
var options = new ProcessingOptions
{
    TextLayer = ocrOnly ? TextLayerMode.Never : TextLayerMode.Auto,
    DetectOrientation = !noOrientation,
};
if (ocrOnly) Console.WriteLine("Mode: --ocr-only (text layer disabled for extraction; still used as recall truth)");
if (noOrientation) Console.WriteLine("Mode: --no-orientation (page-orientation detection disabled; faster, recall on upright corpora unchanged)");

// Inspect mode: dump one page's geometry for debugging — layout overlay PNG,
// line/region JSON, and the composed Markdown. Usage: --inspect "<pdf-name>:<page>"
if (inspect != null)
{
    int sep = inspect.LastIndexOf(':');
    string inspectPdf = inspect[..sep];
    int inspectPage = int.Parse(inspect[(sep + 1)..]);
    Directory.CreateDirectory(outDir);
    await Inspector.RunAsync(processor, pdfDir, inspectPdf, inspectPage, options, outDir);
    return 0;
}

// Orientation check: report the detected/applied rotation per page on real (possibly rotated) scans.
if (orientCheck)
    return await OrientCheckRunner.RunAsync(processor, pdfDir, orientPages) ? 0 : 1;

// Gate modes process only the truth-referenced pages, no corpus sweep.
if (gate3Csv != null || gate5Dir != null || gate6Dir != null || gate7Dir != null)
{
    bool gatesOk = true;
    if (gate3Csv != null) gatesOk &= await Gate3Runner.RunAsync(processor, pdfDir, gate3Csv, options);
    if (gate5Dir != null) gatesOk &= await Gate5Runner.RunAsync(processor, pdfDir, gate5Dir, options);
    if (gate6Dir != null) gatesOk &= await Gate6Runner.RunAsync(processor, pdfDir, gate6Dir, options);
    // Gate 7 manages its own per-page options (forced OCR + degradation transforms), so it takes
    // the born-digital dir directly rather than the shared `options`/`pdfDir`.
    if (gate7Dir != null) gatesOk &= await Gate7Runner.RunAsync(processor, gate7Dir, outDir, gate7Pages);
    return gatesOk ? 0 : 1;
}

var rows = new List<Row>();
var total = System.Diagnostics.Stopwatch.StartNew();

foreach (var pdf in pdfs)
{
    var name = Path.GetFileName(pdf);
    var stem = Path.GetFileNameWithoutExtension(pdf);
    Console.WriteLine($"\n{name}");

    DocumentResult result;
    try
    {
        result = await processor.ProcessAsync(await File.ReadAllBytesAsync(pdf), options);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ERROR: {ex.Message}");
        rows.Add(new Row(name, 0, 0, 0, 0, 0, 0, 0, null, true, $"error: {ex.Message}"));
        continue;
    }

    foreach (var page in result.Pages)
    {
        var v = page.Verification;
        var (flagged, reason) = Flag(v, page.Notice);
        rows.Add(new Row(name, page.PageNumber, page.Lines.Count, page.Regions.Count,
                         v.Seconds, v.LinesLost, v.TruthWords, v.TruthWordsFound,
                         v.RecallPercent, flagged, reason));

        await File.WriteAllTextAsync(
            Path.Combine(outDir, $"{stem}_p{page.PageNumber:D3}.md"), page.Markdown);

        var recall = v.RecallPercent is { } r ? $"{r:0.0}%" : "n/a (no text layer)";
        var cov = v.LinesLost == 0 ? "OK" : $"LOST {v.LinesLost}";
        var src = page.Source == TextSource.TextLayer ? "layer" : "ocr";
        Console.WriteLine($"  p{page.PageNumber:D3}  {page.Lines.Count,4} lines  {v.Seconds,5:0.0}s  " +
                          $"src:{src}  cov:{cov}  recall:{recall}{(flagged ? "  ⚑" : "")}");
    }
}
total.Stop();

// ── Scorecard CSV ────────────────────────────────────────────────────────────
var csvPath = Path.Combine(outDir, "scorecard.csv");
await using (var csv = new StreamWriter(csvPath))
{
    await csv.WriteLineAsync(
        "pdf,page,lines,regions,seconds,coverage_missing,truth_words,truth_found,recall_pct,flagged,reason");
    foreach (var s in rows)
        await csv.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"\"{s.Pdf}\",{s.Page},{s.Lines},{s.Regions},{s.Seconds:0.0},{s.Lost},{s.TruthWords},{s.TruthFound},{(s.Recall.HasValue ? s.Recall.Value.ToString("0.0", CultureInfo.InvariantCulture) : "")},{s.Flagged},\"{s.Reason}\""));
}

// ── Summary + gates ──────────────────────────────────────────────────────────
var scored = rows.Where(s => s.Recall.HasValue).ToList();
var flaggedRows = rows.Where(s => s.Flagged).ToList();
double avgRecall = scored.Count > 0 ? scored.Average(s => s.Recall!.Value) : 0;
double pct95 = scored.Count > 0 ? 100.0 * scored.Count(s => s.Recall >= 95) / scored.Count : 0;
int totalLost = rows.Sum(s => s.Lost);

Console.WriteLine("\n════ SUMMARY ════");
Console.WriteLine($"pages: {rows.Count}   time: {total.Elapsed.TotalMinutes:0.0} min " +
                  $"({(rows.Count > 0 ? total.Elapsed.TotalSeconds / rows.Count : 0):0.0}s/page)");
if (scored.Count > 0)
    Console.WriteLine($"recall: avg {avgRecall:0.0}%   min {scored.Min(s => s.Recall!.Value):0.0}%   " +
                      $"pages ≥95%: {scored.Count(s => s.Recall >= 95)}/{scored.Count} ({pct95:0.0}%)");
Console.WriteLine($"flagged for review: {flaggedRows.Count}/{rows.Count}");
foreach (var f in flaggedRows.Take(30))
    Console.WriteLine($"  {f.Pdf} p{f.Page}: {f.Reason}");
Console.WriteLine($"scorecard → {csvPath}");

bool gate1 = avgRecall >= 98.0 && pct95 >= 98.0;
bool gate2 = totalLost == 0;
Console.WriteLine("\n════ GATES (RESULTS.md) ════");
Console.WriteLine($"Gate 1 corpus recall   : {(gate1 ? "PASS" : "FAIL")}  (avg {avgRecall:0.0}% / ≥95% on {pct95:0.0}% of pages)");
Console.WriteLine($"Gate 2 zero text loss  : {(gate2 ? "PASS" : "FAIL")}  ({totalLost} lines lost)");

return gate1 && gate2 ? 0 : 1;

static (bool Flagged, string Reason) Flag(PageVerification v, string? notice = null)
{
    if (notice != null) return (true, notice);
    if (v.LinesLost > 0) return (true, $"{v.LinesLost} lines lost");
    if (v.TruthWords == 0) return (true, "no text layer (needs eyeball)");
    if (v.RecallPercent < 95.0) return (true, $"recall {v.RecallPercent:0.0}%");
    return (false, "");
}

internal sealed record Row(
    string Pdf, int Page, int Lines, int Regions, double Seconds,
    int Lost, int TruthWords, int TruthFound, double? Recall, bool Flagged, string Reason);
