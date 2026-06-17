// Full per-page pipeline: render → layout → OCR → tables → reading order → Markdown,
// plus self-verification (OCR coverage invariant + text-layer word recall).
// Engines load once; reuse across pages.

using System.Diagnostics;
using PDFtoImage;
using SkiaSharp;

namespace Foliant.Spike;

public sealed record PageScore(
    string Pdf, int Page, int Lines, int Regions, double Seconds,
    int CoverageMissing, int TruthWords, int TruthFound,
    int OrderAnchors, int OrderInSequence, string? Error)
{
    // Pages reading below this order fidelity are flagged for review even when recall is
    // perfect — that combination ("all the words, wrong order") is the dense-form scramble.
    public const double OrderFlagThreshold = 90.0;

    public double? RecallPct => TruthWords > 0 ? 100.0 * TruthFound / TruthWords : null;

    /// <summary>
    /// Reading-order fidelity: of the anchor words shared with the text layer, the fraction kept
    /// in the page's natural reading order. Null when the page has too few anchors to judge.
    /// This is the axis <see cref="RecallPct"/> is structurally blind to — recall is set
    /// membership, so a permuted line still scores 100%.
    /// </summary>
    public double? OrderPct => OrderAnchors >= 8 ? 100.0 * OrderInSequence / OrderAnchors : null;

    public bool Flagged =>
        Error != null || CoverageMissing > 0 || TruthWords == 0
        || RecallPct < 95.0 || OrderPct < OrderFlagThreshold;

    public string FlagReason =>
        Error != null ? $"error: {Error}"
        : CoverageMissing > 0 ? $"{CoverageMissing} OCR lines lost"
        : TruthWords == 0 ? "no text layer (needs eyeball)"
        : RecallPct < 95.0 ? $"recall {RecallPct:0.0}%"
        : OrderPct < OrderFlagThreshold ? $"reading-order {OrderPct:0.0}% (scramble)"
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

            // Order fidelity uses the composed markdown only (NOT the dropped-furniture tail),
            // because order is about the sequence a reader actually sees in the output.
            var (anchors, inSeq) = TextLayerOrder(pdfBytes, pageNumber, markdown);

            sw.Stop();
            return new PageScore(pdfName, pageNumber, textLines.Count, regions.Count,
                                 sw.Elapsed.TotalSeconds, missing, truthWords, truthFound,
                                 anchors, inSeq, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new PageScore(pdfName, pageNumber, 0, 0, sw.Elapsed.TotalSeconds, 0, 0, 0, 0, 0, ex.Message);
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

    /// <summary>
    /// Reading-order fidelity, the axis <see cref="TextLayerRecall"/> cannot see. PdfPig returns the
    /// page's text-layer words in reading order, so we treat that sequence as ground truth. Using
    /// only words that occur exactly once in the text layer (clean position anchors), we read off the
    /// output's anchor words in output order and measure the longest run that stays in increasing
    /// truth position — a longest-increasing-subsequence (O(n log n)). A page that keeps every word
    /// but permutes it — the dense box-grid scramble — scores ~100% recall yet a low order fraction.
    /// Returns (anchors, inSequence); anchors &lt; 8 ⇒ too sparse to judge (handled by OrderPct).
    /// </summary>
    private static (int Anchors, int InSequence) TextLayerOrder(byte[] pdfBytes, int pageNumber, string output)
    {
        try
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(pdfBytes);
            var truth = doc.GetPage(pageNumber).GetWords()
                .Select(w => Normalize(w.Text)).Where(t => t.Length >= 4).ToList();
            if (truth.Count == 0) return (0, 0);

            // Keep only words unique in the text layer → each is an unambiguous position anchor.
            var counts = new Dictionary<string, int>();
            foreach (var t in truth) counts[t] = counts.GetValueOrDefault(t) + 1;
            var truthPos = new Dictionary<string, int>();
            for (int i = 0; i < truth.Count; i++)
                if (counts[truth[i]] == 1) truthPos[truth[i]] = i;
            if (truthPos.Count < 8) return (0, 0);

            // Output anchors, in output order, mapped to their truth positions.
            var seq = new List<int>();
            var seen = new HashSet<string>();
            foreach (var w in output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var n = Normalize(w);
                if (n.Length >= 4 && seen.Add(n) && truthPos.TryGetValue(n, out var idx))
                    seq.Add(idx);
            }
            if (seq.Count < 8) return (0, 0);
            return (seq.Count, LongestIncreasingSubsequence(seq));
        }
        catch { return (0, 0); }
    }

    /// <summary>Length of the longest strictly-increasing subsequence (patience sorting, O(n log n)).</summary>
    private static int LongestIncreasingSubsequence(List<int> a)
    {
        var tails = new List<int>(a.Count);
        foreach (var x in a)
        {
            int lo = 0, hi = tails.Count;
            while (lo < hi) { int mid = (lo + hi) >> 1; if (tails[mid] < x) lo = mid + 1; else hi = mid; }
            if (lo == tails.Count) tails.Add(x); else tails[lo] = x;
        }
        return tails.Count;
    }

    public static string Normalize(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    public void Dispose() { _layout.Dispose(); _ocr.Dispose(); _tables.Dispose(); }
}
