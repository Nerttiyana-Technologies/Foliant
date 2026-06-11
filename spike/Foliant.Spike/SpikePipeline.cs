// Full per-page pipeline: render → layout → OCR → tables → reading order → Markdown,
// plus self-verification (OCR coverage invariant + text-layer word recall).
// Engines load once; reuse across pages.

using System.Diagnostics;
using PDFtoImage;
using SkiaSharp;

namespace Foliant.Spike;

public sealed record PageScore(
    string Pdf, int Page, int Lines, int Regions, double Seconds,
    int CoverageMissing, int TruthWords, int TruthFound, string? Error)
{
    public double? RecallPct => TruthWords > 0 ? 100.0 * TruthFound / TruthWords : null;

    public bool Flagged =>
        Error != null || CoverageMissing > 0 || TruthWords == 0 || RecallPct < 95.0;

    public string FlagReason =>
        Error != null ? $"error: {Error}"
        : CoverageMissing > 0 ? $"{CoverageMissing} OCR lines lost"
        : TruthWords == 0 ? "no text layer (needs eyeball)"
        : RecallPct < 95.0 ? $"recall {RecallPct:0.0}%"
        : "";
}

public sealed class SpikePipeline : IDisposable
{
    private readonly DocLayoutYoloDetector _layout;
    private readonly PaddleOcrEngine _ocr;
    private readonly TableStructureExtractor _tables;

    public SpikePipeline(string modelsDir)
    {
        string M(string f) => Path.Combine(modelsDir, f);
        _layout = new DocLayoutYoloDetector(M("layout_doclayout_yolo.onnx"));
        _ocr = new PaddleOcrEngine(M("ocr_det_v5.onnx"), M("ocr_rec_en.onnx"), M("ocr_rec_en.dict.txt"));
        _tables = new TableStructureExtractor(M("table_structure.onnx"));
    }

    public PageScore ProcessPage(byte[] pdfBytes, string pdfName, int pageNumber,
                                 string outputDir, bool debugArtifacts = false)
    {
        var sw = Stopwatch.StartNew();
        var stem = Path.GetFileNameWithoutExtension(pdfName);
        try
        {
            using var stream = new MemoryStream(pdfBytes, writable: false);
            using var bitmap = Conversion.ToImage(
                stream, page: (Index)(pageNumber - 1), options: new RenderOptions(Dpi: 300));

            var regions = _layout.Detect(bitmap);
            var textLines = _ocr.Recognize(bitmap);
            var markdown = BuildMarkdown(bitmap, regions, textLines, out var droppedOnPurpose);

            Directory.CreateDirectory(outputDir);
            File.WriteAllText(Path.Combine(outputDir, $"{stem}_p{pageNumber:D3}.md"), markdown);

            if (debugArtifacts)
            {
                DocLayoutYoloDetector.DrawOverlay(bitmap, regions,
                    Path.Combine(outputDir, $"{stem}_p{pageNumber:D3}_layout.png"));
                File.WriteAllText(Path.Combine(outputDir, $"{stem}_p{pageNumber:D3}_ocr.json"),
                    System.Text.Json.JsonSerializer.Serialize(textLines,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }

            int missing = textLines.Count(l =>
                l.Text.Length > 2 && !droppedOnPurpose.Contains(l) &&
                !markdown.Contains(l.Text, StringComparison.Ordinal) &&
                !markdown.Contains(l.Text.Replace("|", "\\|"), StringComparison.Ordinal));

            // Recall measures extraction fidelity, so intentionally dropped page
            // furniture still counts as "extracted" — otherwise blank pages score 0%.
            var recallText = markdown + "\n" + string.Join("\n", droppedOnPurpose.Select(l => l.Text));
            var (truthWords, truthFound) = TextLayerRecall(pdfBytes, pageNumber, recallText);

            sw.Stop();
            return new PageScore(pdfName, pageNumber, textLines.Count, regions.Count,
                                 sw.Elapsed.TotalSeconds, missing, truthWords, truthFound, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new PageScore(pdfName, pageNumber, 0, 0, sw.Elapsed.TotalSeconds, 0, 0, 0, ex.Message);
        }
    }

    private string BuildMarkdown(SKBitmap bitmap,
                                 IReadOnlyList<LayoutRegion> rawRegions,
                                 IReadOnlyList<TextLine> textLines,
                                 out HashSet<TextLine> droppedOnPurpose)
    {
        var ordered = ReadingOrder.Order(ReadingOrder.SuppressDuplicates(rawRegions));
        var assigned = new HashSet<TextLine>();
        var blocks = new List<(float Y, string Md)>();
        droppedOnPurpose = new HashSet<TextLine>();

        foreach (var region in ordered)
        {
            var regionLines = textLines
                .Where(l => (l.X1 + l.X2) / 2 >= region.X1 && (l.X1 + l.X2) / 2 <= region.X2 &&
                            (l.Y1 + l.Y2) / 2 >= region.Y1 && (l.Y1 + l.Y2) / 2 <= region.Y2)
                .ToList();
            foreach (var l in regionLines) assigned.Add(l);
            if (region.Label == "abandon")
                foreach (var l in regionLines) droppedOnPurpose.Add(l);

            string? block = region.Label switch
            {
                "abandon" => null,
                "title" => $"## {string.Join(" ", regionLines.Select(l => l.Text))}",
                "table" => _tables.ExtractMarkdown(bitmap, region, textLines),
                "table_caption" or "figure_caption" =>
                    $"*{string.Join(" ", regionLines.Select(l => l.Text))}*",
                _ => string.Join("\n",
                    ReadingOrder.GroupIntoVisualLines(regionLines).Select(g => g.Text)),
            };
            if (!string.IsNullOrWhiteSpace(block)) blocks.Add((region.Y1, block));
        }

        var orphans = textLines.Where(l => !assigned.Contains(l)).ToList();
        foreach (var (y, _, text) in ReadingOrder.GroupIntoVisualLines(orphans))
        {
            int idx = blocks.FindIndex(b => b.Y > y);
            blocks.Insert(idx < 0 ? blocks.Count : idx, (y, text));
        }

        var md = new System.Text.StringBuilder();
        foreach (var (_, text) in blocks) md.AppendLine(text).AppendLine();
        return md.ToString();
    }

    public static (int TruthWords, int Found) ScoreTextLayerRecall(byte[] pdfBytes, int pageNumber, string markdown)
        => TextLayerRecall(pdfBytes, pageNumber, markdown);

    private static (int TruthWords, int Found) TextLayerRecall(byte[] pdfBytes, int pageNumber, string markdown)
    {
        try
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(pdfBytes);
            var truth = doc.GetPage(pageNumber).GetWords()
                .Select(w => Normalize(w.Text)).Where(t => t.Length >= 3).ToList();
            if (truth.Count == 0) return (0, 0);

            var mdWords = new HashSet<string>(
                markdown.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Normalize));
            return (truth.Count, truth.Count(t => mdWords.Contains(t)));
        }
        catch { return (0, 0); }
    }

    public static string Normalize(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    public void Dispose() { _layout.Dispose(); _ocr.Dispose(); _tables.Dispose(); }
}
