// Opt-in local harvester (ADR-0001): turns a verification pass into a license-clean reading-order
// training set. For each born-digital page, the PDF's own text-layer word order (via PdfPig) is the
// ground-truth reading order; we map each detected Region to its gold rank (median text-layer
// position of its words) and emit a JSONL record {regions + geometry + text, gold_order}.
//
// Invariants (binding, see ADR-0001 governance): writes to the LOCAL filesystem only, makes NO
// network call, emits NO telemetry, and is active ONLY when --emit-reading-order is passed. Every
// label derives from the document's own embedded data — never from a third-party (non-commercial)
// model. The output stays on the user's machine; nothing here publishes anything.

using System.Text.Json;
using Foliant;

namespace Foliant.Verification;

internal static class ReadingOrderEmitter
{
    private sealed record RegionDto(int id, string type, float[] bbox, double gold_rank, string text);

    /// <summary>Appends one JSONL training record for the page, or does nothing when the page has no
    /// usable text-layer order (scanned page, too few anchor words). Returns true if a record was written.</summary>
    public static bool Append(string outDir, byte[] pdfBytes, string pdfName, PageResult page)
    {
        var (regions, gold) = BuildGold(pdfBytes, page);
        if (regions is null || gold is null) return false;

        var record = new
        {
            pdf = pdfName,
            page = page.PageNumber,
            width_px = page.WidthPx,
            height_px = page.HeightPx,
            dpi = page.Dpi,
            // Provenance tag (same convention as form-kv/scan-pairs): the HF build script's
            // license guard rejects untagged records, so harvests self-tag. Default local-only;
            // a permissive tag is a per-run, deliberate choice via FOLIANT_RO_LICENSE.
            license = Environment.GetEnvironmentVariable("FOLIANT_RO_LICENSE") ?? "local-only",
            regions,
            gold_order = gold,
        };

        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, "reading-order.jsonl");
        using var w = new StreamWriter(path, append: true);
        w.WriteLine(JsonSerializer.Serialize(record));
        return true;
    }

    private static (List<RegionDto>? Regions, List<int>? Gold) BuildGold(byte[] pdfBytes, PageResult page)
    {
        List<string> truth;
        try
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(pdfBytes);
            if (page.PageNumber < 1 || page.PageNumber > doc.NumberOfPages) return (null, null);
            truth = doc.GetPage(page.PageNumber).GetWords()
                .Select(x => Normalize(x.Text)).Where(t => t.Length >= 4).ToList();
        }
        catch { return (null, null); }
        if (truth.Count < 8) return (null, null);

        // Words unique in the text layer are clean position anchors.
        var counts = new Dictionary<string, int>();
        foreach (var t in truth) counts[t] = counts.GetValueOrDefault(t) + 1;
        var pos = new Dictionary<string, int>();
        for (int i = 0; i < truth.Count; i++)
            if (counts[truth[i]] == 1) pos[truth[i]] = i;
        if (pos.Count < 8) return (null, null);

        var dtos = new List<RegionDto>(page.Regions.Count);
        for (int r = 0; r < page.Regions.Count; r++)
        {
            var reg = page.Regions[r];
            var positions = new List<int>();
            var seen = new HashSet<string>();
            foreach (var word in (reg.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var n = Normalize(word);
                if (n.Length >= 4 && seen.Add(n) && pos.TryGetValue(n, out var idx)) positions.Add(idx);
            }
            double rank = positions.Count > 0 ? Median(positions) : double.MaxValue;
            dtos.Add(new RegionDto(
                r, reg.Type.ToString(),
                new[] { reg.Bounds.X1, reg.Bounds.Y1, reg.Bounds.X2, reg.Bounds.Y2 },
                rank, reg.Text ?? ""));
        }

        // Gold order = anchored regions sorted by text-layer rank (stable by id for ties).
        var gold = dtos.Where(d => d.gold_rank < double.MaxValue)
                       .OrderBy(d => d.gold_rank).ThenBy(d => d.id)
                       .Select(d => d.id).ToList();
        if (gold.Count < 2) return (null, null);   // nothing to learn an order from
        return (dtos, gold);
    }

    private static double Median(List<int> xs)
    {
        var s = xs.OrderBy(x => x).ToList();
        int n = s.Count;
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2.0;
    }

    private static string Normalize(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}
