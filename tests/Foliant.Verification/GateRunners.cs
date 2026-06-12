// Gate 3 (form-field truthfulness), Gate 5 (table cell correctness) and Gate 6
// (reading-order correctness) runners. Truth files are hand-labeled once
// (Test-Data*/truth*, gitignored) and scored mechanically here on every release.

using System.Text.RegularExpressions;
using Foliant;
using Foliant.Pipeline;

namespace Foliant.Verification;

internal static class GateCommon
{
    /// <summary>Alphanumeric-uppercase normalization; whitespace-insensitive matching.</summary>
    public static string Norm(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Standalone X mark (not part of a word) — the checkbox glyph in flattened text.</summary>
    public static bool HasCheckMark(string line) =>
        Regex.IsMatch(line, @"(?<![A-Za-z0-9])[xX☒✓](?![A-Za-z0-9])");

    /// <summary>Minimal CSV line parser with double-quote support.</summary>
    public static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString().Trim());
        return fields;
    }

    /// <summary>Processes one page and returns (markdown incl. furniture, raw markdown, page result).</summary>
    public static async Task<PageResult?> ProcessPageAsync(
        DocumentProcessor processor, string pdfDir, string pdfName, int page,
        ProcessingOptions options, Dictionary<(string, int), PageResult?> cache)
    {
        var key = (pdfName, page);
        if (cache.TryGetValue(key, out var cached)) return cached;

        string path = Path.Combine(pdfDir, pdfName);
        PageResult? result = null;
        if (File.Exists(path))
        {
            var doc = await processor.ProcessAsync(
                await File.ReadAllBytesAsync(path), options with { Pages = new[] { page } });
            result = doc.Pages.Count > 0 ? doc.Pages[0] : null;
        }
        cache[key] = result;
        return result;
    }

    public static string FullText(PageResult page) =>
        page.Markdown + "\n" + string.Join("\n", page.PageFurniture.Select(l => l.Text));
}

internal static class Gate3Runner
{
    private sealed record Field(string Pdf, int Page, string Name, string Type, string Expected, string Anchor);

    public static async Task<bool> RunAsync(
        DocumentProcessor processor, string pdfDir, string truthCsv, ProcessingOptions options)
    {
        var fields = new List<Field>();
        foreach (var line in (await File.ReadAllLinesAsync(truthCsv)).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = GateCommon.ParseCsvLine(line);
            if (f.Count < 6) { Console.Error.WriteLine($"gate3: bad row skipped: {line}"); continue; }
            fields.Add(new Field(f[0], int.Parse(f[1]), f[2], f[3].ToLowerInvariant(), f[4], f[5]));
        }

        Console.WriteLine($"\n════ GATE 3 — form-field truthfulness ({fields.Count} fields) ════");
        var cache = new Dictionary<(string, int), PageResult?>();
        int correct = 0, fabricated = 0, missingPages = 0;

        foreach (var group in fields.GroupBy(f => (f.Pdf, f.Page)))
        {
            var page = await GateCommon.ProcessPageAsync(
                processor, pdfDir, group.Key.Pdf, group.Key.Page, options, cache);
            Console.WriteLine($"\n{group.Key.Pdf} p{group.Key.Page}:");
            if (page == null)
            {
                Console.WriteLine("  PAGE NOT PROCESSABLE — all fields fail");
                missingPages += group.Count();
                continue;
            }

            string fullNorm = GateCommon.Norm(GateCommon.FullText(page));
            var mdLines = page.Markdown.Split('\n')
                .Concat(page.PageFurniture.Select(l => l.Text)).ToList();

            foreach (var f in group)
            {
                string verdict;
                if (f.Type == "checkbox")
                {
                    string anchorNorm = GateCommon.Norm(f.Anchor);
                    var hits = mdLines.Where(l => GateCommon.Norm(l).Contains(anchorNorm)).ToList();
                    if (hits.Count == 0) verdict = "ANCHOR NOT FOUND";
                    else
                    {
                        bool marked = hits.Any(l => Gate3Scoring.HasMarkNearAnchor(l, anchorNorm));
                        bool expectChecked = f.Expected.Equals("checked", StringComparison.OrdinalIgnoreCase);
                        if (marked == expectChecked) { verdict = "OK"; correct++; }
                        else if (marked) { verdict = "FABRICATED MARK (expected unchecked)"; fabricated++; }
                        else verdict = "MARK MISSING (expected checked)";
                    }
                }
                else
                {
                    bool found = fullNorm.Contains(GateCommon.Norm(f.Expected));
                    if (found) { verdict = "OK"; correct++; }
                    else verdict = "VALUE MISSING";
                }
                Console.WriteLine($"  {f.Name,-22} {verdict}");
            }
        }

        Console.WriteLine($"\nGate 3 result: {correct}/{fields.Count} correct, {fabricated} fabricated" +
                          (missingPages > 0 ? $", {missingPages} on unprocessable pages" : ""));
        Console.WriteLine("Pass condition: zero fabricated values; correctness compared against the VLM");
        Console.WriteLine("baseline once that is scored on the same sheet (RESULTS.md Gate 3).");
        bool pass = fabricated == 0 && missingPages == 0;
        Console.WriteLine($"Gate 3 (zero-fabrication): {(pass ? "PASS" : "FAIL")}");
        return pass;
    }
}

internal static class Gate3Scoring
{
    /// <summary>
    /// Checkbox mark detection with cell-level proximity. Forms like the SF-33 render two
    /// side-by-side TOC column groups into ONE table row, so a whole-line X check misreads
    /// the right entry's mark as belonging to the left entry. For table rows (lines with
    /// '|' cells), the X must sit in the anchor's own cell or within two cells to its left
    /// (the (X) column position). Non-table lines fall back to the whole-line check.
    /// </summary>
    public static bool HasMarkNearAnchor(string line, string anchorNorm)
    {
        if (line.Contains('|'))
        {
            var cells = line.Split('|');
            for (int i = 0; i < cells.Length; i++)
            {
                if (!GateCommon.Norm(cells[i]).Contains(anchorNorm)) continue;
                for (int j = Math.Max(0, i - 2); j <= i; j++)
                    if (GateCommon.HasCheckMark(cells[j])) return true;
                return false;
            }
            // Anchor not inside a single cell (split across cells) — fall through.
        }
        return GateCommon.HasCheckMark(line);
    }
}

/// <summary>
/// Gate 6 — reading-order correctness. Truth files are text files, one per page:
/// first line "#pdf=&lt;name&gt;;page=&lt;n&gt;", then short snippets (≥3 words, unique on the
/// page) one per line in TRUE reading order. The runner locates each snippet in the
/// composed Markdown and scores Kendall's tau between the truth order and the order
/// of the snippets' positions in the output. Tau = 1.0 means perfect order; 0 means
/// uncorrelated; negative means reversed. Labeling truth is "read the page and type
/// the first words of each block in order" — no geometry needed, and the same truth
/// scores any reading-order backend (run with --reading-order xycut vs xycut++ to A/B).
/// </summary>
internal static class Gate6Runner
{
    public static async Task<bool> RunAsync(
        DocumentProcessor processor, string pdfDir, string truthDir, ProcessingOptions options)
    {
        var truthFiles = Directory.GetFiles(truthDir, "*.txt").OrderBy(p => p).ToList();
        if (truthFiles.Count == 0)
        {
            Console.Error.WriteLine($"gate6: no truth .txt files in {truthDir}");
            return false;
        }

        Console.WriteLine($"\n════ GATE 6 — reading-order correctness ({truthFiles.Count} pages) ════");
        var cache = new Dictionary<(string, int), PageResult?>();
        var taus = new List<double>();
        bool allScored = true;
        int totalSnippets = 0, totalFound = 0;

        foreach (var file in truthFiles)
        {
            var lines = await File.ReadAllLinesAsync(file);
            string name = Path.GetFileNameWithoutExtension(file);
            if (lines.Length < 2 || !lines[0].StartsWith('#'))
            {
                Console.WriteLine($"\n{name}: SKIPPED (missing #pdf=...;page=... metadata)");
                allScored = false;
                continue;
            }

            var meta = lines[0].TrimStart('#').Split(';')
                .Select(kv => kv.Split('=', 2))
                .Where(kv => kv.Length == 2)
                .ToDictionary(kv => kv[0].Trim(), kv => kv[1].Trim());
            string pdfName = meta["pdf"];
            int pageNo = int.Parse(meta["page"]);

            var snippets = lines.Skip(1)
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#') && !l.Contains("PREFILL-ME"))
                .Select(GateCommon.Norm)
                .Where(s => s.Length >= 6)
                .ToList();
            if (snippets.Count < 3)
            {
                Console.WriteLine($"\n{name}: SKIPPED (needs ≥3 usable snippets — finish the truth file)");
                allScored = false;
                continue;
            }

            var page = await GateCommon.ProcessPageAsync(processor, pdfDir, pdfName, pageNo, options, cache);
            if (page == null)
            {
                Console.WriteLine($"\n{name}: PAGE NOT PROCESSABLE");
                allScored = false;
                continue;
            }

            // Main-flow order only: page furniture is excluded from the reading flow by
            // design, so a snippet landing there counts as not-found (reported distinctly).
            string mdNorm = GateCommon.Norm(page.Markdown);
            string furnitureNorm = GateCommon.Norm(string.Join(" ", page.PageFurniture.Select(l => l.Text)));

            var positions = new List<int>();
            int notFound = 0, inFurniture = 0, ambiguous = 0;
            foreach (var s in snippets)
            {
                int idx = mdNorm.IndexOf(s, StringComparison.Ordinal);
                if (idx < 0)
                {
                    if (furnitureNorm.Contains(s)) inFurniture++; else notFound++;
                    continue;
                }
                if (mdNorm.IndexOf(s, idx + 1, StringComparison.Ordinal) >= 0) ambiguous++;
                positions.Add(idx);
            }

            totalSnippets += snippets.Count;
            totalFound += positions.Count;

            if (positions.Count < 3)
            {
                Console.WriteLine($"\n{name}: UNSCORED — only {positions.Count}/{snippets.Count} snippets found " +
                                  $"({inFurniture} in furniture, {notFound} missing)");
                allScored = false;
                continue;
            }

            double tau = KendallTau(positions);
            taus.Add(tau);
            string warn = (inFurniture + notFound > 0 ? $"  ({inFurniture} furniture, {notFound} missing)" : "") +
                          (ambiguous > 0 ? $"  ({ambiguous} ambiguous — make snippets longer)" : "");
            Console.WriteLine($"{name}: tau {tau:0.000}  ({positions.Count}/{snippets.Count} snippets){warn}");
        }

        if (taus.Count > 0)
        {
            Console.WriteLine($"\nGate 6 result: avg tau {taus.Average():0.000}  min {taus.Min():0.000}  " +
                              $"pages tau=1.0: {taus.Count(t => t >= 0.9995)}/{taus.Count}  " +
                              $"snippet coverage {totalFound}/{totalSnippets}");
        }
        Console.WriteLine("Pass condition: compare backends on the same truth set (--reading-order xycut");
        Console.WriteLine("vs xycut++); the default flips when the candidate wins. Reported; not yet enforced.");
        return allScored && taus.Count > 0;
    }

    /// <summary>
    /// Kendall's tau for a sequence whose truth order is the list order: concordant pairs
    /// have increasing positions, discordant decreasing. Ties (equal positions — snippets
    /// matching at the same offset) count neither way.
    /// </summary>
    internal static double KendallTau(IReadOnlyList<int> positions)
    {
        int n = positions.Count, concordant = 0, discordant = 0;
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                if (positions[j] > positions[i]) concordant++;
                else if (positions[j] < positions[i]) discordant++;
            }
        int pairs = n * (n - 1) / 2;
        return pairs == 0 ? 1.0 : (double)(concordant - discordant) / pairs;
    }
}

internal static class Gate5Runner
{
    public static async Task<bool> RunAsync(
        DocumentProcessor processor, string pdfDir, string truthDir, ProcessingOptions options)
    {
        var truthFiles = Directory.GetFiles(truthDir, "*.csv").OrderBy(p => p).ToList();
        if (truthFiles.Count == 0)
        {
            Console.Error.WriteLine($"gate5: no truth CSVs in {truthDir}");
            return false;
        }

        Console.WriteLine($"\n════ GATE 5 — table cell correctness ({truthFiles.Count} tables) ════");
        var cache = new Dictionary<(string, int), PageResult?>();
        bool allScored = true;
        var scores = new List<double>();

        foreach (var file in truthFiles)
        {
            var lines = await File.ReadAllLinesAsync(file);
            if (lines.Length < 2 || !lines[0].StartsWith('#'))
            {
                Console.WriteLine($"\n{Path.GetFileName(file)}: SKIPPED (missing #pdf=...;page=... metadata)");
                allScored = false;
                continue;
            }

            var meta = lines[0].TrimStart('#').Split(';')
                .Select(kv => kv.Split('=', 2))
                .Where(kv => kv.Length == 2)
                .ToDictionary(kv => kv[0].Trim(), kv => kv[1].Trim());
            string pdfName = meta["pdf"];
            int pageNo = int.Parse(meta["page"]);
            string name = meta.GetValueOrDefault("name", Path.GetFileNameWithoutExtension(file));

            var truthRows = lines.Skip(1)
                .Where(l => !string.IsNullOrWhiteSpace(l) && !l.Contains("PREFILL-ME"))
                .Select(GateCommon.ParseCsvLine)
                .ToList();
            int truthCells = truthRows.Sum(r => r.Count(c => c.Length > 0));
            if (truthCells == 0)
            {
                Console.WriteLine($"\n{name}: SKIPPED (no labeled cells — finish the truth file)");
                allScored = false;
                continue;
            }

            var page = await GateCommon.ProcessPageAsync(processor, pdfDir, pdfName, pageNo, options, cache);
            if (page == null)
            {
                Console.WriteLine($"\n{name}: PAGE NOT PROCESSABLE");
                allScored = false;
                continue;
            }

            // Extracted rows: every table region's cells grouped by row, plus non-table region
            // lines as single-cell rows (tolerates layout calling a table region "text").
            var extractedRows = new List<List<string>>();
            foreach (var region in page.Regions)
            {
                if (region.Table is { } t)
                    extractedRows.AddRange(t.Cells.GroupBy(c => c.Row).OrderBy(g => g.Key)
                        .Select(g => g.OrderBy(c => c.Column).Select(c => c.Text).ToList()));
                else
                    extractedRows.AddRange(region.Text.Split('\n').Select(l => new List<string> { l }));
            }

            int matched = 0;
            foreach (var truthRow in truthRows)
            {
                var want = truthRow.Where(c => c.Length > 0).Select(GateCommon.Norm).ToList();
                if (want.Count == 0) continue;
                // Best extracted row: the one containing the most of this row's cells.
                int best = extractedRows.Count == 0 ? 0 : extractedRows.Max(row =>
                {
                    string rowNorm = GateCommon.Norm(string.Join(" ", row));
                    return want.Count(w => rowNorm.Contains(w));
                });
                matched += best;
            }

            double pct = 100.0 * matched / truthCells;
            scores.Add(pct);
            Console.WriteLine($"\n{name}: {matched}/{truthCells} cells ({pct:0.0}%)");
        }

        if (scores.Count > 0)
            Console.WriteLine($"\nGate 5 result: avg cell correctness {scores.Average():0.0}% across {scores.Count} tables");
        Console.WriteLine("Pass condition: ≥ VLM baseline per table (RESULTS.md Gate 5) — record the");
        Console.WriteLine("baseline once, then compare. Reported here; not yet auto-enforced.");
        return allScored && scores.Count > 0;
    }
}
