// Gate 3 (extraction mode) — scores the typed FormFields the pipeline extracts against the
// hand-labeled form-field truth. Unlike Gate 3's anchor mode (does the value appear in the
// Markdown?), this scores the STRUCTURED output: did we extract field X with the right value?
//
// Verdicts per field: CORRECT (value matches), WRONG (extracted a non-matching value — the
// dangerous fabrication case), or MISSING (not extracted). Ledger-first: it reports an accuracy
// breakdown and never fails the build. The number it produces is the deterministic ceiling for
// label-anchored extraction on this form — the evidence for whether to flip ExtractFormFields on,
// and for where an ML form-understanding model would earn its keep.

using Foliant;
using Foliant.Pipeline;

namespace Foliant.Verification;

internal static class Gate3ExtractRunner
{
    private sealed record Truth(string Pdf, int Page, string Name, string Type, string Expected);

    public static async Task<bool> RunAsync(
        DocumentProcessor processor, string pdfDir, string truthCsv, ProcessingOptions options)
    {
        var truths = new List<Truth>();
        foreach (var line in (await File.ReadAllLinesAsync(truthCsv)).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = GateCommon.ParseCsvLine(line);
            if (f.Count < 5) continue;
            truths.Add(new Truth(f[0], int.Parse(f[1]), f[2], f[3].ToLowerInvariant(), f[4]));
        }

        Console.WriteLine($"\n════ GATE 3 (extraction) — typed FormField accuracy ({truths.Count} fields) ════");
        var cache = new Dictionary<(string, int), PageResult?>();
        int correct = 0, wrong = 0, missing = 0;
        int textTotal = 0, textCorrect = 0, boxTotal = 0, boxCorrect = 0;

        foreach (var group in truths.GroupBy(t => (t.Pdf, t.Page)))
        {
            var page = await GateCommon.ProcessPageAsync(
                processor, pdfDir, group.Key.Pdf, group.Key.Page, options, cache);
            var extracted = page?.FormFields ?? (IReadOnlyList<FormField>)Array.Empty<FormField>();
            Console.WriteLine($"\n{group.Key.Pdf} p{group.Key.Page}  ({extracted.Count} fields extracted):");

            foreach (var t in group)
            {
                bool isBox = t.Type == "checkbox";
                if (isBox) boxTotal++; else textTotal++;

                var got = extracted.FirstOrDefault(f => f.Name == t.Name);
                string verdict;
                if (got is null)
                {
                    missing++;
                    verdict = "MISSING";
                }
                else
                {
                    bool ok = isBox
                        ? string.Equals(got.Value, t.Expected, StringComparison.OrdinalIgnoreCase)
                        : ValueMatches(got.Value, t.Expected);
                    if (ok) { correct++; if (isBox) boxCorrect++; else textCorrect++; verdict = "OK"; }
                    else { wrong++; verdict = $"WRONG (got \"{Trim(got.Value)}\", want \"{t.Expected}\")"; }
                }
                Console.WriteLine($"  {t.Name,-22} {verdict}");
            }
        }

        Console.WriteLine($"\n──── GATE 3 EXTRACTION LEDGER ────");
        Console.WriteLine($"correct {correct}/{truths.Count}   wrong {wrong}   missing {missing}");
        if (textTotal > 0)
            Console.WriteLine($"text     : {textCorrect}/{textTotal} ({100.0 * textCorrect / textTotal:0.0}%)");
        if (boxTotal > 0)
            Console.WriteLine($"checkbox : {boxCorrect}/{boxTotal} ({100.0 * boxCorrect / boxTotal:0.0}%)");
        Console.WriteLine("Read as the DETERMINISTIC ceiling for label-anchored extraction; WRONG count is the");
        Console.WriteLine("fabrication risk (must be low to flip ExtractFormFields on). Informational — no build fail.");
        return true;
    }

    // Lenient text match: extraction may carry trailing form furniture ("CODE"), so a containment
    // either way counts. Alphanumeric-uppercase normalized, whitespace-insensitive.
    private static bool ValueMatches(string got, string expected)
    {
        string g = GateCommon.Norm(got), e = GateCommon.Norm(expected);
        return e.Length > 0 && (g.Contains(e) || e.Contains(g));
    }

    private static string Trim(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
