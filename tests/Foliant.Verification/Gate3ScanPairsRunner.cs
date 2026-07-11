// Gate 3 (scanned holdout) — the learned form-KV extractor scored against MACHINE truth,
// with field identity established by GEOMETRY, not by name text.
//
// Test-Data-41 pairs every digital filled form (live AcroForm = exact name/value/RECT truth) with
// a flattened scanned twin (registered geometry). The holdout variants (13–15) of the
// AcroForm-bearing families (SF2800A, SF2807) are processed through the real pipeline
// (OCR → LiltFormFieldExtractor); each truth field's widget rect is projected into the scan
// raster frame and predictions are assigned to fields BY LOCATION (one-to-one, best overlap).
//
// Why rect-based: v1 of this runner matched predicted names against AcroForm /T names — but /T is
// machine vocabulary ("BS Beginning2") while predictions carry printed-label vocabulary
// ("BEGINNING"); the mismatch was scored as failure. Location is how the form itself defines
// field identity. Name quality is reported as a separate informational column.
//
//   CORRECT         — the prediction in this field's rect carries the right value
//   WRONG-VALUE     — a prediction sits in this field's rect with a DIFFERENT value: fabrication
//   VALUE-ELSEWHERE — the right value was predicted, but outside this field's rect
//   MISSING         — nothing predicted in the rect, value not found elsewhere
//   SPURIOUS        — predictions assigned to no truth field
//
// Ledger-first: reports, never fails the build. Suggested flags: --lilt-emit-unpaired (location
// identity does not need names, so measure full value recall) and --lilt-conf 0.5 for the sweep.
//   dotnet run ... -- data/Test-Data-41/scanned out-dir --gate3-scanpairs data/Test-Data-41 \
//       --lilt-model models/form-kv-lilt --lilt-conf 0.5 --lilt-emit-unpaired

using System.Globalization;
using System.Text.RegularExpressions;
using Foliant;
using Foliant.Pipeline;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Tokens;

namespace Foliant.Verification;

internal static class Gate3ScanPairsRunner
{
    /// <summary>Whole-variant holdout (train used 01–12; manifest-corrected split).</summary>
    private static readonly int[] HoldoutVariants = { 13, 14, 15 };

    /// <summary>
    /// Overlap granularity for the value-aware assignment tie-break: candidate pairs whose
    /// prediction/rect overlap rounds to the same multiple of this are treated as an overlap tie,
    /// and a matching value decides between them. Genuinely different overlap still wins on overlap.
    /// </summary>
    private const float OverlapTieBucket = 0.05f;

    /// <summary>Truth rect is in DIGITAL PDF points (top-left origin, normalized to page size).</summary>
    private sealed record TruthField(
        string ScanPdf, int Page, string Name, string Value,
        float X1, float Y1, float X2, float Y2, float PageWpt, float PageHpt);

    public static async Task<bool> RunAsync(
        DocumentProcessor processor, string td41Dir, ProcessingOptions options,
        string? dumpSpuriousPath = null, string? dumpCrossFieldPath = null, string? dumpMissingPath = null)
    {
        string digitalDir = Path.Combine(td41Dir, "digital");
        string scannedDir = Path.Combine(td41Dir, "scanned");
        if (!Directory.Exists(digitalDir) || !Directory.Exists(scannedDir))
        {
            Console.Error.WriteLine($"--gate3-scanpairs expects digital/ + scanned/ under '{td41Dir}'.");
            return false;
        }

        // ── truth: widget /T + /V + rect from the digital twins (text fields; checkboxes out of scope)
        var truths = new List<TruthField>();
        int pairCount = 0, checkboxes = 0;
        foreach (var digital in Directory.GetFiles(digitalDir, "*.pdf").OrderBy(p => p))
        {
            var m = Regex.Match(Path.GetFileNameWithoutExtension(digital), @"^(?<fam>.+)_(?<var>\d+)$");
            if (!m.Success || !HoldoutVariants.Contains(int.Parse(m.Groups["var"].Value))) continue;
            string scanned = Path.Combine(scannedDir, Path.GetFileNameWithoutExtension(digital) + "_scan.pdf");
            if (!File.Exists(scanned)) continue;

            using var doc = PdfDocument.Open(await File.ReadAllBytesAsync(digital));
            bool any = false;
            for (int p = 1; p <= doc.NumberOfPages; p++)
            {
                var page = doc.GetPage(p);
                float wPt = (float)page.Width, hPt = (float)page.Height;
                foreach (var ann in page.GetAnnotations())
                {
                    if (ann.Type != AnnotationType.Widget) continue;
                    if (ann.Flags.HasFlag(AnnotationFlags.Hidden) || ann.Flags.HasFlag(AnnotationFlags.NoView)) continue;
                    var d = ann.AnnotationDictionary;
                    string? val = ReadTextValue(d);
                    if (val is null)
                    {
                        if (IsCheckedBox(d)) checkboxes++;
                        continue;
                    }
                    string name = ReadPartialName(d);
                    var r = ann.Rectangle;
                    float x1 = (float)r.Left, x2 = (float)r.Right;
                    float y1 = hPt - (float)r.Top, y2 = hPt - (float)r.Bottom;   // → top-left origin
                    truths.Add(new TruthField(Path.GetFileName(scanned), p, name, val.Trim(),
                        Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2), wPt, hPt));
                    any = true;
                }
            }
            if (any) pairCount++;
        }

        if (truths.Count == 0)
        {
            Console.Error.WriteLine("gate3-scanpairs: no AcroForm text-field truth in holdout variants.");
            return false;
        }
        Console.WriteLine($"\n════ GATE 3 (scanned holdout, RECT identity) — {pairCount} pairs, " +
                          $"{truths.Count} text fields ({checkboxes} checkbox truths out of scope) ════");

        // ── predictions: scanned twins through the real pipeline; keep each page's raster frame
        var cache = new Dictionary<(string, int), PageResult?>();
        var pages = new Dictionary<(string, int), (IReadOnlyList<FormField> Fields, int Wpx, int Hpx)>();
        foreach (var group in truths.GroupBy(t => (t.ScanPdf, t.Page)))
        {
            var page = await GateCommon.ProcessPageAsync(
                processor, scannedDir, group.Key.ScanPdf, group.Key.Page, options, cache);
            pages[group.Key] = page is null
                ? (Array.Empty<FormField>(), 1, 1)
                : (page.FormFields ?? (IReadOnlyList<FormField>)Array.Empty<FormField>(), page.WidthPx, page.HeightPx);
        }

        // --gate3-dump-spurious: every spurious prediction at the extractor floor, one CSV row,
        // with its nearest truth rect — the raw material for designing spurious filters
        // (value-shape sanity, KEY-adjacency, garbage detection) from evidence, not guesses.
        List<string>? spuriousDump = dumpSpuriousPath is null ? null : new List<string>
            { "doc,page,confidence,name,value,x1,y1,x2,y2,nearest_truth_name,nearest_dist_px,value_matches_some_truth" };

        // --gate3-dump-crossfield: every CROSS-FIELD case at the extractor floor, one CSV row, with
        // its straddle/solid geometry (here-rect vs home-rect overlap) and the predicted box — the
        // raw material for measuring how far box fidelity moves the straddle sub-count.
        List<string>? crossFieldDump = dumpCrossFieldPath is null ? null : new List<string>
            { "doc,page,confidence,name,got_value,want_value,pred_x1,pred_y1,pred_x2,pred_y2,here_overlap,home_overlap,geometry,home_truth_name" };

        // --gate3-dump-missing: every MISSING truth field at the extractor floor, one CSV row, with the
        // signals that decompose WHY it dropped — how many predictions overlap its rect, whether its
        // value was extracted but credited elsewhere (stolen), the nearest prediction, and a class:
        // value-stolen (assignment) | overlapped-wrong-value (mis-read) | no-pred-recall-gap (model silent).
        List<string>? missingDump = dumpMissingPath is null ? null : new List<string>
            { "doc,page,truth_name,want_value,rect_x1,rect_y1,rect_x2,rect_y2,preds_overlapping_rect,value_extracted_elsewhere,nearest_pred_value,nearest_pred_conf,nearest_pred_dist_px,class" };

        Score(truths, pages, floor: 0f, verbose: true, spuriousDump, crossFieldDump, missingDump);
        if (dumpSpuriousPath is not null && spuriousDump is not null)
        {
            await File.WriteAllLinesAsync(dumpSpuriousPath, spuriousDump);
            Console.WriteLine($"\nspurious dump: {spuriousDump.Count - 1} rows → {dumpSpuriousPath}");
        }
        if (dumpCrossFieldPath is not null && crossFieldDump is not null)
        {
            await File.WriteAllLinesAsync(dumpCrossFieldPath, crossFieldDump);
            Console.WriteLine($"\ncross-field dump: {crossFieldDump.Count - 1} rows → {dumpCrossFieldPath}");
        }
        if (dumpMissingPath is not null && missingDump is not null)
        {
            await File.WriteAllLinesAsync(dumpMissingPath, missingDump);
            Console.WriteLine($"\nmissing dump: {missingDump.Count - 1} rows → {dumpMissingPath}");
        }
        Console.WriteLine("\n──── CONFIDENCE-FLOOR SWEEP ────");
        Console.WriteLine($"{"floor",6} {"correct",8} {"cross-fld",9} {"trunc-src",9} {"garbled",8} {"wrong-oth",9} {"elsewhere",9} {"missing",8} {"spurious",9}");
        for (float floor = 0.50f; floor <= 0.951f; floor += 0.05f)
        {
            var s = Score(truths, pages, floor, verbose: false);
            Console.WriteLine($"{floor,6:0.00} {s.Correct,8} {s.CrossField,9} {s.TruncatedSource,9} {s.Garbled,8} {s.WrongOther,9} {s.Elsewhere,9} {s.Missing,8} {s.Spurious,9}");
        }
        Console.WriteLine("\nCROSS-FIELD is the fabrication number (another field's value claimed in this rect) —");
        Console.WriteLine("promotion bar ~0 at the shipped floor. GARBLED = right field, OCR-mangled transcription");
        Console.WriteLine("(honest failure; the OCR-noise training arm targets it). TRUNCATED-SOURCE = the scan");
        Console.WriteLine("image itself ends mid-value (cell-border clipping in the source; ~7% of TD-41 fields,");
        Console.WriteLine("class confirmed in production scans) — unwinnable by extraction, target of the");
        Console.WriteLine("PossiblyTruncated honesty flag. Informational — no build fail.");
        return true;
    }

    private sealed record Tally(int Correct, int CrossField, int TruncatedSource, int Garbled, int WrongOther, int Elsewhere, int Missing, int Spurious);

    private static Tally Score(
        List<TruthField> truths,
        Dictionary<(string, int), (IReadOnlyList<FormField> Fields, int Wpx, int Hpx)> pages,
        float floor, bool verbose, List<string>? spuriousDump = null, List<string>? crossFieldDump = null,
        List<string>? missingDump = null)
    {
        int correct = 0, crossField = 0, truncatedSource = 0, garbled = 0, wrongOther = 0, elsewhere = 0, missing = 0, spurious = 0;
        int namedOk = 0, namedAny = 0, crossStraddle = 0, crossSolid = 0;
        int flagged = 0, truncFlagged = 0, correctFlagged = 0;   // PossiblyTruncated probe cross-tab

        foreach (var group in truths.GroupBy(t => (t.ScanPdf, t.Page)))
        {
            var (fields, wPx, hPx) = pages[group.Key];
            var preds = fields.Where(f => f.Confidence >= floor && f.Bounds is not null).ToList();
            var used = new bool[preds.Count];
            var truthList = group.ToList();
            var assign = new int[truthList.Count];
            Array.Fill(assign, -1);

            // GLOBAL one-to-one assignment by descending overlap. (The first pass assigned per-truth
            // in list order — truth A could steal a prediction that overlaps truth B more, minting
            // artificial CROSS-FIELDs. Sorting all candidate pairs first is near-optimal matching.)
            // VALUE-AWARE TIE-BREAK (2026-07-07): dense SF-forms + the Project() registration padding
            // make neighbouring truth rects OVERLAP, so a correctly-placed value sits inside two rects
            // at equal overlap and pure-overlap assignment credits it to the wrong one — a scorer
            // false-positive CROSS-FIELD, not a fabrication (Gate-3 cf dump 2026-07-07: all 7 straddle
            // cases had here_ov = home_ov = 1.00 and the got-value matched the home field's truth).
            // Among candidates within OverlapTieBucket of each other, prefer the pair whose predicted
            // VALUE matches the truth value. Guarded: value-match only wins inside the same overlap
            // bucket, so a coincidental match in a rect the prediction barely (or does not) overlap can
            // never steal the assignment — the genuinely misplaced "solid" cross-fields stay CROSS-FIELD.
            var rects = new BoundingBox[truthList.Count];
            for (int ti = 0; ti < truthList.Count; ti++) rects[ti] = Project(truthList[ti], wPx, hPx);
            var candidates = new List<(int Ti, int J, float Ov, bool Vm)>();
            for (int ti = 0; ti < truthList.Count; ti++)
                for (int j = 0; j < preds.Count; j++)
                {
                    float ov = Overlap(rects[ti], preds[j].Bounds!.Value);
                    if (ov > 0f) candidates.Add((ti, j, ov, ValueMatches(preds[j].Value, truthList[ti].Value)));
                }
            var truthTaken = new bool[truthList.Count];
            foreach (var c in candidates
                         .OrderByDescending(c => (int)MathF.Round(c.Ov / OverlapTieBucket))   // coarse overlap level
                         .ThenByDescending(c => c.Vm)                                          // value-match breaks near-ties
                         .ThenByDescending(c => c.Ov))                                         // exact overlap within a bucket
            {
                if (truthTaken[c.Ti] || used[c.J]) continue;
                assign[c.Ti] = c.J; truthTaken[c.Ti] = true; used[c.J] = true;
            }

            if (verbose) Console.WriteLine($"\n{group.Key.ScanPdf} p{group.Key.Page}  ({preds.Count} predicted):");
            for (int ti = 0; ti < truthList.Count; ti++)
            {
                var t = truthList[ti];
                string verdict;
                if (assign[ti] >= 0)
                {
                    var p = preds[assign[ti]];
                    if (ValueMatches(p.Value, t.Value))
                    {
                        // The lenient containment match is directional in what it forgives:
                        // got ⊇ want (label text swept in around a complete value) is a real
                        // CORRECT; but got = strict PREFIX of want is the truncated-source
                        // signature — the scan image itself ends mid-value (flattener clipped
                        // the appearance at the cell border; verified visually on TD-41
                        // 2026-07-06: ink runs flush into the next vertical ruling and stops;
                        // ~7% of holdout fields; same class confirmed in production customer
                        // scans). "$26,320.00" read as "26,320.0" is a WRONG AMOUNT, not a
                        // correct extraction — scoring it CORRECT hid the class. No OCR/model
                        // lever can recover unprinted pixels; the product lever is a
                        // PossiblyTruncated honesty flag. NOTE: this cannibalizes CORRECT vs
                        // pre-2026-07-06 ledgers (reference correct 787 ≈ new correct +
                        // truncated-source). Non-prefix strict substrings (mid/tail) remain
                        // CORRECT for row comparability — revisit if the tail class grows.
                        string gotN = GateCommon.Norm(p.Value);
                        string wantN = GateCommon.Norm(t.Value);
                        if (gotN.Length >= 3 && gotN.Length < wantN.Length
                            && wantN.StartsWith(gotN, StringComparison.Ordinal))
                        {
                            truncatedSource++;
                            if (p.PossiblyTruncated) truncFlagged++;
                            verdict = $"TRUNCATED-SOURCE (got \"{Trim(p.Value)}\", want \"{Trim(t.Value)}\")"
                                      + (p.PossiblyTruncated ? " [flagged]" : " [unflagged]");
                        }
                        else
                        {
                            correct++; verdict = "OK";
                            if (p.PossiblyTruncated) correctFlagged++;
                            if (p.Name.Length > 0) { namedAny++; if (NameMatches(p.Name, t.Name)) namedOk++; }
                        }
                        if (p.PossiblyTruncated) flagged++;
                    }
                    else
                    {
                        // Classification order matters (learned on the money columns: a truncated OCR
                        // tail like "1,002.00" of "$49,002.00" substring-matches SOME other dollar value
                        // on a 30-cell page — containment alone mislabeled GARBLED as CROSS-FIELD):
                        //   1. EXACT normalized equality with another field's value → CROSS-FIELD
                        //      (unless own-similarity is near-identical too — then it's ambiguous garble)
                        //   2. own-similarity ≥ 0.5 → GARBLED (right field, mangled transcription)
                        //   3. containment match to another field → CROSS-FIELD
                        //   4. else WRONG-OTHER
                        // (TRUNCATED-SOURCE never reaches this ladder: a normalized prefix always
                        // satisfies the containment ValueMatches and is intercepted in the branch above.)
                        float simOwn = Similarity(p.Value, t.Value);
                        string gotN = GateCommon.Norm(p.Value);
                        var others = Enumerable.Range(0, truthList.Count).Where(k => k != ti).ToList();
                        var exactHomes = others.Where(k => GateCommon.Norm(truthList[k].Value) == gotN).ToList();
                        var containHomes = others.Where(k => ValueMatches(p.Value, truthList[k].Value)).ToList();

                        if (exactHomes.Count > 0 && simOwn < 0.8f)
                            verdict = CrossFieldVerdict(p, t, truthList, rects, exactHomes, rects[ti], ref crossField, ref crossStraddle, ref crossSolid,
                                                        crossFieldDump, group.Key.ScanPdf, group.Key.Page);
                        else if (simOwn >= 0.5f)
                        {
                            garbled++;
                            verdict = $"GARBLED (got \"{Trim(p.Value)}\", want \"{Trim(t.Value)}\")";
                        }
                        else if (containHomes.Count > 0)
                            verdict = CrossFieldVerdict(p, t, truthList, rects, containHomes, rects[ti], ref crossField, ref crossStraddle, ref crossSolid,
                                                        crossFieldDump, group.Key.ScanPdf, group.Key.Page);
                        else
                        {
                            wrongOther++;
                            verdict = $"WRONG-OTHER (got \"{Trim(p.Value)}\", want \"{Trim(t.Value)}\")";
                        }
                    }
                }
                else if (preds.Where((p, j) => !used[j] && ValueMatches(p.Value, t.Value)).Any())
                {
                    elsewhere++; verdict = "VALUE-ELSEWHERE";
                }
                else
                {
                    missing++; verdict = "MISSING";
                    if (missingDump is not null)
                    {
                        var rect = rects[ti];
                        float rcx = (rect.X1 + rect.X2) / 2f, rcy = (rect.Y1 + rect.Y2) / 2f;
                        int overlapping = preds.Count(pp => Overlap(rect, pp.Bounds!.Value) > 0f);
                        // A used prediction carrying this value = the value WAS extracted but the
                        // one-to-one match credited it to a neighbouring rect (stolen). No overlap
                        // and no such value anywhere = the model never fired here (true recall gap).
                        // Overlap but wrong value = fired on the field, mis-read it.
                        bool valueUsedElsewhere = preds.Any(pp => ValueMatches(pp.Value, t.Value));
                        int nearest = -1; float nearestDist = float.MaxValue;
                        for (int j = 0; j < preds.Count; j++)
                        {
                            var pbb = preds[j].Bounds!.Value;
                            float pcx = (pbb.X1 + pbb.X2) / 2f, pcy = (pbb.Y1 + pbb.Y2) / 2f;
                            float d = MathF.Sqrt((pcx - rcx) * (pcx - rcx) + (pcy - rcy) * (pcy - rcy));
                            if (d < nearestDist) { nearestDist = d; nearest = j; }
                        }
                        string cls = valueUsedElsewhere ? "value-stolen"
                            : overlapping > 0 ? "overlapped-wrong-value"
                            : "no-pred-recall-gap";
                        missingDump.Add(string.Join(",",
                            Csv(group.Key.ScanPdf), group.Key.Page.ToString(CultureInfo.InvariantCulture),
                            Csv(t.Name), Csv(t.Value),
                            ((int)rect.X1).ToString(CultureInfo.InvariantCulture),
                            ((int)rect.Y1).ToString(CultureInfo.InvariantCulture),
                            ((int)rect.X2).ToString(CultureInfo.InvariantCulture),
                            ((int)rect.Y2).ToString(CultureInfo.InvariantCulture),
                            overlapping.ToString(CultureInfo.InvariantCulture),
                            valueUsedElsewhere ? "1" : "0",
                            Csv(nearest >= 0 ? preds[nearest].Value : ""),
                            nearest >= 0 ? preds[nearest].Confidence.ToString("0.000", CultureInfo.InvariantCulture) : "",
                            ((int)nearestDist).ToString(CultureInfo.InvariantCulture),
                            cls));
                    }
                }
                if (verbose) Console.WriteLine($"  {Trim(t.Name),-30} {verdict}");
            }
            int extra = used.Count(u => !u);
            spurious += extra;
            if (verbose && extra > 0) Console.WriteLine($"  (+{extra} spurious prediction(s) in no truth rect)");
            if (spuriousDump is not null)
            {
                for (int j = 0; j < preds.Count; j++)
                {
                    if (used[j]) continue;
                    var p = preds[j];
                    var pb = p.Bounds!.Value;
                    float pcx = (pb.X1 + pb.X2) / 2f, pcy = (pb.Y1 + pb.Y2) / 2f;
                    int nearest = -1; float nearestDist = float.MaxValue;
                    for (int ti = 0; ti < truthList.Count; ti++)
                    {
                        float tcx = (rects[ti].X1 + rects[ti].X2) / 2f, tcy = (rects[ti].Y1 + rects[ti].Y2) / 2f;
                        float d = MathF.Sqrt((pcx - tcx) * (pcx - tcx) + (pcy - tcy) * (pcy - tcy));
                        if (d < nearestDist) { nearestDist = d; nearest = ti; }
                    }
                    // A spurious prediction whose value matches SOME truth on the page is a
                    // duplicate/mislocated read; one matching nothing is fabricated junk.
                    bool valueMatchesSome = truthList.Any(t2 => ValueMatches(p.Value, t2.Value));
                    spuriousDump.Add(string.Join(",",
                        Csv(group.Key.ScanPdf), group.Key.Page.ToString(CultureInfo.InvariantCulture),
                        p.Confidence.ToString("0.000", CultureInfo.InvariantCulture),
                        Csv(p.Name), Csv(p.Value),
                        ((int)pb.X1).ToString(CultureInfo.InvariantCulture),
                        ((int)pb.Y1).ToString(CultureInfo.InvariantCulture),
                        ((int)pb.X2).ToString(CultureInfo.InvariantCulture),
                        ((int)pb.Y2).ToString(CultureInfo.InvariantCulture),
                        Csv(nearest >= 0 ? truthList[nearest].Name : ""),
                        ((int)nearestDist).ToString(CultureInfo.InvariantCulture),
                        valueMatchesSome ? "1" : "0"));
                }
            }
        }

        if (verbose)
        {
            int total = truths.Count;
            Console.WriteLine($"\n──── GATE 3 SCANNED-HOLDOUT LEDGER (rect identity, extractor floor) ────");
            Console.WriteLine($"correct {correct}/{total} ({100.0 * correct / total:0.0}%)   " +
                              $"CROSS-FIELD {crossField}   truncated-source {truncatedSource}   " +
                              $"garbled {garbled}   wrong-other {wrongOther}   " +
                              $"value-elsewhere {elsewhere}   missing {missing}   spurious {spurious}");
            Console.WriteLine($"(truncated-source was scored CORRECT before 2026-07-06: pre-change correct ≈ correct + truncated-source)");
            Console.WriteLine($"name quality on corrects (informational): {namedAny} named, {namedOk} fuzzy-match /T");
            Console.WriteLine($"CROSS-FIELD geometry: {crossStraddle} straddle a boundary (box-fidelity fixable), {crossSolid} solidly misplaced");
            Console.WriteLine($"PossiblyTruncated probe: {flagged} assigned predictions flagged; " +
                              $"{truncFlagged}/{truncatedSource} truncated-source caught (probe recall), " +
                              $"{correctFlagged} flagged among corrects (over-flagging)");
        }
        return new Tally(correct, crossField, truncatedSource, garbled, wrongOther, elsewhere, missing, spurious);
    }

    /// <summary>CROSS-FIELD verdict + geometry instrumentation (straddle vs solidly misplaced).</summary>
    private static string CrossFieldVerdict(
        FormField p, TruthField t, List<TruthField> truthList, BoundingBox[] rects, List<int> homes, BoundingBox here,
        ref int crossField, ref int crossStraddle, ref int crossSolid,
        List<string>? crossFieldDump, string doc, int page)
    {
        crossField++;
        var pb = p.Bounds!.Value;
        float hereOv = Overlap(here, pb);
        int homeK = homes.OrderByDescending(k => Overlap(rects[k], pb)).First();
        float homeOv = Overlap(rects[homeK], pb);
        bool straddle = homeOv > 0f;
        string geo = straddle
            ? $"STRADDLE here {hereOv:0.00}/home {homeOv:0.00}"
            : $"SOLID here {hereOv:0.00}/home 0";
        if (straddle) crossStraddle++; else crossSolid++;

        crossFieldDump?.Add(string.Join(",",
            Csv(doc), page.ToString(CultureInfo.InvariantCulture),
            p.Confidence.ToString("0.000", CultureInfo.InvariantCulture),
            Csv(p.Name), Csv(p.Value), Csv(t.Value),
            ((int)pb.X1).ToString(CultureInfo.InvariantCulture),
            ((int)pb.Y1).ToString(CultureInfo.InvariantCulture),
            ((int)pb.X2).ToString(CultureInfo.InvariantCulture),
            ((int)pb.Y2).ToString(CultureInfo.InvariantCulture),
            hereOv.ToString("0.000", CultureInfo.InvariantCulture),
            homeOv.ToString("0.000", CultureInfo.InvariantCulture),
            straddle ? "straddle" : "solid",
            Csv(truthList[homeK].Name)));

        return $"CROSS-FIELD (got \"{Trim(p.Value)}\", want \"{Trim(t.Value)}\") [{geo}; " +
               $"pred ({pb.X1:0},{pb.Y1:0})-({pb.X2:0},{pb.Y2:0})]";
    }

    /// <summary>Truth rect (digital points, top-left) → scan raster px, padded for registration drift.</summary>
    private static BoundingBox Project(TruthField t, int wPx, int hPx)
    {
        float sx = wPx / Math.Max(1f, t.PageWpt), sy = hPx / Math.Max(1f, t.PageHpt);
        float pad = 0.35f * Math.Max(1f, (t.Y2 - t.Y1) * sy);
        return new BoundingBox(t.X1 * sx - pad, t.Y1 * sy - pad, t.X2 * sx + pad, t.Y2 * sy + pad);
    }

    /// <summary>Fraction of the prediction box inside the truth rect (0..1).</summary>
    private static float Overlap(BoundingBox rect, BoundingBox pred)
    {
        float ix = Math.Max(0, Math.Min(rect.X2, pred.X2) - Math.Max(rect.X1, pred.X1));
        float iy = Math.Max(0, Math.Min(rect.Y2, pred.Y2) - Math.Max(rect.Y1, pred.Y1));
        float area = Math.Max(1f, pred.Width) * Math.Max(1f, pred.Height);
        return ix * iy / area;
    }

    private static string? ReadTextValue(DictionaryToken d)
    {
        if (d.TryGet(NameToken.Create("V"), out StringToken v) && !string.IsNullOrWhiteSpace(v.Data)) return v.Data;
        if (d.TryGet(NameToken.Create("Parent"), out DictionaryToken p)
            && p.TryGet(NameToken.Create("V"), out StringToken pv) && !string.IsNullOrWhiteSpace(pv.Data)) return pv.Data;
        return null;
    }

    private static bool IsCheckedBox(DictionaryToken d) =>
        d.TryGet(NameToken.Create("AS"), out NameToken asTok) && asTok.Data is not "Off";

    private static string ReadPartialName(DictionaryToken d)
    {
        if (d.TryGet(NameToken.Create("T"), out StringToken t)) return t.Data;
        if (d.TryGet(NameToken.Create("Parent"), out DictionaryToken p)
            && p.TryGet(NameToken.Create("T"), out StringToken pt)) return pt.Data;
        return string.Empty;
    }

    /// <summary>Normalized Levenshtein similarity in [0,1] over Norm'd strings (1 = identical).</summary>
    private static float Similarity(string a, string b)
    {
        string x = GateCommon.Norm(a), y = GateCommon.Norm(b);
        if (x.Length == 0 || y.Length == 0) return 0f;
        var prev = new int[y.Length + 1];
        var cur = new int[y.Length + 1];
        for (int j = 0; j <= y.Length; j++) prev[j] = j;
        for (int i = 1; i <= x.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= y.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (x[i - 1] == y[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return 1f - (float)prev[y.Length] / Math.Max(x.Length, y.Length);
    }

    /// <summary>Lenient value match: normalized containment either way.</summary>
    private static bool ValueMatches(string got, string expected)
    {
        string g = GateCommon.Norm(got), e = GateCommon.Norm(expected);
        return e.Length > 0 && g.Length > 0 && (g.Contains(e) || e.Contains(g));
    }

    /// <summary>Fuzzy printed-label vs /T comparison — INFORMATIONAL only.</summary>
    private static bool NameMatches(string predicted, string truth)
    {
        string np = GateCommon.Norm(predicted), nt = GateCommon.Norm(truth);
        if (np.Length == 0 || nt.Length == 0) return false;
        if (np.Contains(nt) || nt.Contains(np)) return true;
        var pw = Words(predicted); var tw = Words(truth);
        if (tw.Count == 0) return false;
        return tw.Count(w => pw.Contains(w)) * 2 >= tw.Count;
    }

    private static HashSet<string> Words(string s)
    {
        var all = Regex.Matches(s.ToLowerInvariant(), "[a-z0-9]+").Select(m => m.Value).ToList();
        var significant = all.Where(w => w.Length >= 3).ToHashSet();
        return significant.Count > 0 ? significant : all.ToHashSet();
    }

    private static string Trim(string s) => s.Length <= 36 ? s : s[..36] + "…";

    /// <summary>CSV field: quote and escape; newlines flattened so one prediction = one row.</summary>
    private static string Csv(string s) =>
        "\"" + s.Replace("\r", " ").Replace("\n", " ").Replace("\"", "\"\"") + "\"";
}
