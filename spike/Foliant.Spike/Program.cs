// Foliant Spike — Phase 0 throwaway pipeline.
//
// Single page (debug artifacts: layout overlay PNG + OCR json):
//   dotnet run -- <pdf-path> <page-number-1-based> [output-dir]
//
// Batch scorecard over a folder of PDFs (all pages):
//   dotnet run -- --batch <pdf-dir> [output-dir]

using Foliant.Spike;

var modelsDir = Path.Combine(Environment.CurrentDirectory, "models");
foreach (var required in new[] { "layout_doclayout_yolo.onnx", "ocr_det_v5.onnx",
                                 "ocr_rec_en.onnx", "ocr_rec_en.dict.txt", "table_structure.onnx" })
{
    if (!File.Exists(Path.Combine(modelsDir, required)))
    {
        Console.Error.WriteLine($"Model missing: models/{required} — run ./scripts/download-models.sh");
        return 2;
    }
}

if (args.Length >= 2 && args[0] == "--batch")
    return RunBatch(args[1], args.Length > 2 ? args[2] : Path.Combine(Environment.CurrentDirectory, "spike-out", "batch"));

if (args.Length >= 3 && args[0] == "--vlm")
    return await VlmBaseline.RunAsync(
        args[1], int.Parse(args[2]),
        Path.Combine(Environment.CurrentDirectory, "spike-out"),
        endpoint: args.Length > 3 ? args[3]
                  : Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434",
        model: args.Length > 4 ? args[4]
               : Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "qwen2.5vl:7b");

if (args.Length >= 2)
    return RunSingle(args[0], int.Parse(args[1]),
                     args.Length > 2 ? args[2] : Path.Combine(Environment.CurrentDirectory, "spike-out"));

Console.Error.WriteLine("Usage: Foliant.Spike <pdf> <page> [outdir] | --batch <pdf-dir> [outdir]");
return 1;

// ─────────────────────────────────────────────────────────────────────────────
int RunSingle(string pdfPath, int pageNumber, string outputDir)
{
    if (!File.Exists(pdfPath)) { Console.Error.WriteLine($"PDF not found: {pdfPath}"); return 2; }

    using var pipeline = new SpikePipeline(modelsDir);
    var score = pipeline.ProcessPage(File.ReadAllBytes(pdfPath), Path.GetFileName(pdfPath),
                                     pageNumber, outputDir, debugArtifacts: true);
    PrintScore(score);
    return score.Error == null ? 0 : 3;
}

int RunBatch(string pdfDir, string outputDir)
{
    if (!Directory.Exists(pdfDir)) { Console.Error.WriteLine($"Directory not found: {pdfDir}"); return 2; }

    var pdfs = Directory.GetFiles(pdfDir, "*.pdf").OrderBy(p => p).ToList();
    if (pdfs.Count == 0) { Console.Error.WriteLine("No PDFs found."); return 2; }

    Directory.CreateDirectory(outputDir);
    using var pipeline = new SpikePipeline(modelsDir);
    var scores = new List<PageScore>();
    var total = System.Diagnostics.Stopwatch.StartNew();

    foreach (var pdf in pdfs)
    {
        var name = Path.GetFileName(pdf);
        var bytes = File.ReadAllBytes(pdf);

        int pageCount;
        try { using var doc = UglyToad.PdfPig.PdfDocument.Open(bytes); pageCount = doc.NumberOfPages; }
        catch (Exception ex) { Console.WriteLine($"SKIP {name}: {ex.Message}"); continue; }

        Console.WriteLine($"\n{name} — {pageCount} pages");
        for (int p = 1; p <= pageCount; p++)
        {
            var score = pipeline.ProcessPage(bytes, name, p, outputDir);
            scores.Add(score);
            PrintScore(score, compact: true);
        }
    }
    total.Stop();

    // Scorecard CSV
    var csvPath = Path.Combine(outputDir, "scorecard.csv");
    using (var csv = new StreamWriter(csvPath))
    {
        csv.WriteLine("pdf,page,lines,regions,seconds,coverage_missing,truth_words,truth_found,recall_pct,flagged,reason");
        foreach (var s in scores)
            csv.WriteLine($"\"{s.Pdf}\",{s.Page},{s.Lines},{s.Regions},{s.Seconds:0.0}," +
                          $"{s.CoverageMissing},{s.TruthWords},{s.TruthFound}," +
                          $"{(s.RecallPct.HasValue ? s.RecallPct.Value.ToString("0.0") : "")}," +
                          $"{s.Flagged},\"{s.FlagReason}\"");
    }

    // Summary
    var scored = scores.Where(s => s.RecallPct.HasValue).ToList();
    var flagged = scores.Where(s => s.Flagged).ToList();
    Console.WriteLine($"\n════ SUMMARY ════");
    Console.WriteLine($"pages: {scores.Count}   time: {total.Elapsed.TotalMinutes:0.0} min " +
                      $"({(scores.Count > 0 ? total.Elapsed.TotalSeconds / scores.Count : 0):0.0}s/page)");
    if (scored.Count > 0)
        Console.WriteLine($"recall: avg {scored.Average(s => s.RecallPct!.Value):0.0}%   " +
                          $"min {scored.Min(s => s.RecallPct!.Value):0.0}%   " +
                          $"pages ≥95%: {scored.Count(s => s.RecallPct >= 95)}/{scored.Count}");
    Console.WriteLine($"flagged for review: {flagged.Count}/{scores.Count}");
    foreach (var f in flagged.Take(30))
        Console.WriteLine($"  {f.Pdf} p{f.Page}: {f.FlagReason}");
    Console.WriteLine($"scorecard → {csvPath}");
    return 0;
}

void PrintScore(PageScore s, bool compact = false)
{
    if (s.Error != null)
    {
        Console.WriteLine($"  p{s.Page:D3} ERROR: {s.Error}");
        return;
    }
    var recall = s.RecallPct.HasValue ? $"{s.RecallPct:0.0}%" : "n/a (no text layer)";
    var cov = s.CoverageMissing == 0 ? "OK" : $"LOST {s.CoverageMissing}";
    var flag = s.Flagged ? "  ⚑" : "";
    Console.WriteLine(compact
        ? $"  p{s.Page:D3}  {s.Lines,4} lines  {s.Seconds,5:0.0}s  cov:{cov}  recall:{recall}{flag}"
        : $"{s.Pdf} p{s.Page}: {s.Lines} lines, {s.Regions} regions, {s.Seconds:0.0}s, coverage {cov}, recall {recall}{flag}");
}
