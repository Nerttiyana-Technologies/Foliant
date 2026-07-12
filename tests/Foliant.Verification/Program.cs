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
using System.Text.Json;
using System.Text.Json.Serialization;
using Foliant;
using Foliant.Forms.Lilt;
using Foliant.Pipeline;
using Foliant.Templates;
using Foliant.ScanUpscale.SuperResolution;
using Foliant.Verification;

// ADR-0002 milestone 1 — born-digital forms → labeled locator dataset (images + labels.jsonl).
// Self-contained; short-circuits before the gate-harness arg parsing below.
if (args.Length > 0 && args[0] == "--emit-form-dataset")
    return FormDatasetEmitter.Run(args[1..]);

// ADR-0003 G1 — ZeroDep routing census (ZeroDep-only, no Foliant models). Reports the fast-lane page share
// and routing distribution over a corpus. Short-circuits before the gate-harness arg parsing below.
if (args.Length > 0 && args[0] == "--route-census")
    return RouteCensusRunner.Run(args[1..]);

// ADR-0003 — table-probe: sample born-digital TableOrComplexLayout pages, render + run the layout detector,
// report the real-table hit rate (is the table hint over-firing?). Needs the layout model (--models).
if (args.Length > 0 && args[0] == "--table-probe")
    return TableProbeRunner.Run(args[1..]);

// ADR-0003 G1a — reclaim-parity: for low-ruling table pages the knob reclaims, compare fast-lane ZeroDep
// prose vs Foliant-only word recall (is any text lost?). Needs the full pipeline models (--models).
if (args.Length > 0 && args[0] == "--reclaim-parity")
    return TableReclaimParityRunner.Run(args[1..]);

// ADR-0003 G1a diagnostic — dump the fast-lane (ZeroDep) text vs Foliant-only text for ONE page, plus that
// page's ZeroDep classification/signals, to see WHY a page scored low recall (real garbage vs artifact).
if (args.Length > 0 && args[0] == "--reclaim-dump")
    return TableReclaimParityRunner.Dump(args[1..]);

// ADR-0003 G1a — clean text-layer fidelity: fast-lane (ZeroDep) vs pdftotext (poppler text layer), the
// noise-free reference for born-digital pages. No Foliant models; requires pdftotext on PATH.
if (args.Length > 0 && args[0] == "--textref-parity")
    return TableReclaimParityRunner.RunTextRef(args[1..]);

// ADR-0004 repro — synthesize low-DPI image-only "scans" from born-digital pages and measure the
// 1.4.0 silent failure: pages emit ~no text while RecallPercent is null (invisible to aggregates)
// and no Notice is set. The emitted synthetic PDFs double as the Gate 9a recovery corpus.
if (args.Length > 0 && args[0] == "--lowres-repro")
    return await LowResReproRunner.RunAsync(args[1..]);

// ADR-0004 — real-scan corpus: wrap loose scan images (JPG/PNG) into image-only PDFs at their
// natural effective DPI, and census how the shipped pipeline does on them (Gate 9a-real baseline).
if (args.Length > 0 && args[0] == "--wrap-scans")
    return await RealScanRunner.WrapScansAsync(args[1..]);

// Paired-corpus harvester: scan ↔ digital-twin truth pairs (local-only; license-tagged).
if (args.Length > 0 && args[0] == "--emit-scan-pairs")
    return ScanPairsEmitter.Run(args[1..]);
if (args.Length > 0 && args[0] == "--scan-census")
    return await RealScanRunner.CensusAsync(args[1..]);

string? pdfDir = null;
string outDir = "verification-out";
string modelsDir = "models";
bool ocrOnly = false;
string? gate3Csv = null;
string? gate3ExtractCsv = null;
string? gate5Dir = null;
string? gate6Dir = null;
string? gate7Dir = null;
int gate7Pages = 2;
string? gate8Dir = null;
int gate8Pages = 2;
bool orientCheck = false;
int orientPages = 5;
bool noOrientation = false;
bool enumeratorOrder = false;
string? inspect = null;
string? emitReadingOrderDir = null;   // ADR-0001 harvester: opt-in, local-only, no network
string? emitFormKvDir = null;         // ADR-0001 Lever 2 harvester: opt-in, local-only, no network
string kvLicense = "local-only";      // provenance tag per record; only "public-domain" may feed the base model
bool emitFormKvOverlay = false;       // draw field widget rects on the page for transform verification
string? synthFormKvDir = null;        // synthetic-fill mode: fill blank templates → form-kv records
int synthVariants = 1;                // synthetic filled variants generated per template page
bool formKvEval = false;              // LiLT Gate-3 eval: score value-word predictions on filled AcroForms
string liltModelDir = "models/form-kv-lilt";   // LiLT model dir (model.onnx + tokenizer files)
bool liltExtract = false;             // --lilt-extract: add the learned LiLT arm to the Gate-3 extractor chain
bool liltOnly = false;                // --lilt-only: learned arm ONLY (attribution runs; no AcroForm/profile)
float liltConf = 0.65f;               // --lilt-conf: learned-arm confidence floor (abstain below)
bool liltEmitUnpaired = false;        // --lilt-emit-unpaired: emit VALUE spans with no KEY (empty Name) — diagnostic
string? gate3ScanPairsDir = null;     // --gate3-scanpairs <TD-41 dir>: scanned-holdout Gate 3 vs AcroForm truth
string? gate3DumpSpurious = null;     // --gate3-dump-spurious <csv>: dump spurious predictions (filter design)
string? gate3DumpCrossField = null;   // --gate3-dump-crossfield <csv>: dump CROSS-FIELD cases w/ straddle geometry
string? gate3DumpMissing = null;      // --gate3-dump-missing <csv>: dump MISSING truth fields w/ recall-gap class
string? gate3DumpGarbled = null;      // --gate3-dump-garbled <csv>: dump GARBLED cases w/ similarity (rec-lever design)
bool refineWordBoxes = false;         // --refine-word-boxes: ink-trim emitted value boxes (box-fidelity rig;
                                      // OUTPUT geometry only, model input unchanged; default OFF, measured on Gate 3)
bool dumpWidgetFields = false;        // wire WidgetFormFieldExtractor + dump per-page FormFields (quality check)
string? emitFormTemplate = null;      // --emit-form-template <blank.pdf>: emit a draft FormLayout JSON for review
string? matchExtractPdf = null;       // --match-extract <filled.pdf>: validate template-aware extraction
string? matchExtractTemplate = null;  //   ...against <template.json> (deterministic, per page)
string? routePdf = null;              // --route <upload.pdf>: per-page router over BUNDLED templates
string? routeDb = null;               // --templates-db <file>: also include customer-registered templates
bool withTemplates = false;           // --with-templates: wire the bundled router into the full pipeline run
string? recModel = null;              // --rec-model <path>: override the OCR recognition model (A/B a stronger rec)
string? recDict = null;               // --rec-dict <path>: dict for --rec-model (default = catalog English dict)
string? superResModel = null;         // --super-res <path>: ML super-resolution ONNX for low-DPI scans (A/B on Gate 8)
int superResTile = 256;               // --super-res-tile N: tile edge (px) for super-res inference
bool superResCuda = false;            // --super-res-cuda: CUDA execution provider (host needs Microsoft.ML.OnnxRuntime.Gpu)
bool gate8Dump = false;               // --gate8-dump: save each arm's OCR-input image (degraded / upscaled) as PNG
string? regBlank = null, regDb = null;        // --register <blank.pdf> <db>: register a customer template
string? listTplDb = null;             // --list-templates <db>
string[]? exportTpl = null;           // --export-template <db> <id> <out.json>
string[]? importTpl = null;           // --import-template <db> <reviewed.json>
string[]? unregTpl = null;            // --unregister <db> <id>
bool noRetryLadder = false;           // --no-retry-ladder: disable the ADR-0004 low-res retry (A/B)
bool noImageRecovery = false;         // --no-image-recovery: disable the ADR-0004 mixed-page merge (A/B)
bool rowSplit = false;                // --row-split: enable merged-row det-box splitting (measured
                                      // net-negative on Gate 3 2026-07-06: spurious 325→543 for
                                      // garbled 95→94; default OFF, kept as a measurement rig)
int samplePdfs = 0;                   // --sample-pdfs N: seeded random N-PDF subset (0 = all)
int sampleSeed = 12345;               // --sample-seed S: reproducible subset across runs
var tableBackend = TableBackend.TableTransformer;
var readingOrder = ReadingOrderBackend.XyCutPlusPlus;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--models" && i + 1 < args.Length) { modelsDir = args[++i]; continue; }
    if (args[i] == "--ocr-only") { ocrOnly = true; continue; }
    if (args[i] == "--gate3" && i + 1 < args.Length) { gate3Csv = args[++i]; continue; }
    if (args[i] == "--gate3-extract" && i + 1 < args.Length) { gate3ExtractCsv = args[++i]; continue; }
    if (args[i] == "--gate5" && i + 1 < args.Length) { gate5Dir = args[++i]; continue; }
    if (args[i] == "--gate6" && i + 1 < args.Length) { gate6Dir = args[++i]; continue; }
    if (args[i] == "--gate7" && i + 1 < args.Length) { gate7Dir = args[++i]; continue; }
    if (args[i] == "--gate7-pages" && i + 1 < args.Length) { gate7Pages = int.Parse(args[++i]); continue; }
    if (args[i] == "--gate8" && i + 1 < args.Length) { gate8Dir = args[++i]; continue; }
    if (args[i] == "--gate8-pages" && i + 1 < args.Length) { gate8Pages = int.Parse(args[++i]); continue; }
    if (args[i] == "--orient-check") { orientCheck = true; continue; }
    if (args[i] == "--orient-pages" && i + 1 < args.Length) { orientPages = int.Parse(args[++i]); continue; }
    if (args[i] == "--no-orientation") { noOrientation = true; continue; }
    if (args[i] == "--enumerator-order") { enumeratorOrder = true; continue; }
    if (args[i] == "--inspect" && i + 1 < args.Length) { inspect = args[++i]; continue; }
    if (args[i] == "--emit-reading-order" && i + 1 < args.Length) { emitReadingOrderDir = args[++i]; continue; }
    if (args[i] == "--emit-form-kv" && i + 1 < args.Length) { emitFormKvDir = args[++i]; continue; }
    if (args[i] == "--kv-license" && i + 1 < args.Length) { kvLicense = args[++i]; continue; }
    if (args[i] == "--form-kv-overlay") { emitFormKvOverlay = true; continue; }
    if (args[i] == "--synth-form-kv" && i + 1 < args.Length) { synthFormKvDir = args[++i]; continue; }
    if (args[i] == "--synth-variants" && i + 1 < args.Length) { synthVariants = int.Parse(args[++i]); continue; }
    if (args[i] == "--form-kv-eval") { formKvEval = true; continue; }
    if (args[i] == "--lilt-model" && i + 1 < args.Length) { liltModelDir = args[++i]; continue; }
    if (args[i] == "--lilt-extract") { liltExtract = true; continue; }
    if (args[i] == "--lilt-only") { liltExtract = true; liltOnly = true; continue; }
    if (args[i] == "--lilt-conf" && i + 1 < args.Length) { liltConf = float.Parse(args[++i], CultureInfo.InvariantCulture); continue; }
    if (args[i] == "--lilt-emit-unpaired") { liltEmitUnpaired = true; continue; }
    if (args[i] == "--gate3-scanpairs" && i + 1 < args.Length) { gate3ScanPairsDir = args[++i]; liltExtract = true; liltOnly = true; continue; }
    if (args[i] == "--gate3-dump-spurious" && i + 1 < args.Length) { gate3DumpSpurious = args[++i]; continue; }
    if (args[i] == "--gate3-dump-crossfield" && i + 1 < args.Length) { gate3DumpCrossField = args[++i]; continue; }
    if (args[i] == "--gate3-dump-missing" && i + 1 < args.Length) { gate3DumpMissing = args[++i]; continue; }
    if (args[i] == "--gate3-dump-garbled" && i + 1 < args.Length) { gate3DumpGarbled = args[++i]; continue; }
    if (args[i] == "--refine-word-boxes") { refineWordBoxes = true; continue; }
    if (args[i] == "--widget-form-fields") { dumpWidgetFields = true; continue; }
    if (args[i] == "--emit-form-template" && i + 1 < args.Length) { emitFormTemplate = args[++i]; continue; }
    if (args[i] == "--match-extract" && i + 2 < args.Length) { matchExtractPdf = args[++i]; matchExtractTemplate = args[++i]; continue; }
    if (args[i] == "--route" && i + 1 < args.Length) { routePdf = args[++i]; continue; }
    if (args[i] == "--templates-db" && i + 1 < args.Length) { routeDb = args[++i]; continue; }
    if (args[i] == "--with-templates") { withTemplates = true; continue; }
    if (args[i] == "--rec-model" && i + 1 < args.Length) { recModel = args[++i]; continue; }
    if (args[i] == "--rec-dict" && i + 1 < args.Length) { recDict = args[++i]; continue; }
    if (args[i] == "--super-res" && i + 1 < args.Length) { superResModel = args[++i]; continue; }
    if (args[i] == "--super-res-tile" && i + 1 < args.Length) { superResTile = int.Parse(args[++i]); continue; }
    if (args[i] == "--super-res-cuda") { superResCuda = true; continue; }
    if (args[i] == "--gate8-dump") { gate8Dump = true; continue; }
    if (args[i] == "--no-retry-ladder") { noRetryLadder = true; continue; }
    if (args[i] == "--no-image-recovery") { noImageRecovery = true; continue; }
    if (args[i] == "--row-split") { rowSplit = true; continue; }
    if (args[i] == "--sample-pdfs" && i + 1 < args.Length) { samplePdfs = int.Parse(args[++i]); continue; }
    if (args[i] == "--sample-seed" && i + 1 < args.Length) { sampleSeed = int.Parse(args[++i]); continue; }
    if (args[i] == "--register" && i + 2 < args.Length) { regBlank = args[++i]; regDb = args[++i]; continue; }
    if (args[i] == "--list-templates" && i + 1 < args.Length) { listTplDb = args[++i]; continue; }
    if (args[i] == "--export-template" && i + 3 < args.Length) { exportTpl = new[] { args[++i], args[++i], args[++i] }; continue; }
    if (args[i] == "--import-template" && i + 2 < args.Length) { importTpl = new[] { args[++i], args[++i] }; continue; }
    if (args[i] == "--unregister" && i + 2 < args.Length) { unregTpl = new[] { args[++i], args[++i] }; continue; }
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

// Template ingestion: read a BLANK form PDF, generate a draft FormLayout (widget geometry + auto-paired
// labels + fingerprint), and write it as editable JSON next to the input for human review of dense blocks.
if (emitFormTemplate != null)
{
    if (!File.Exists(emitFormTemplate))
    {
        Console.Error.WriteLine($"--emit-form-template: file not found: {emitFormTemplate}");
        return 2;
    }
    byte[] templateBytes = await File.ReadAllBytesAsync(emitFormTemplate);
    string templateId = Path.GetFileNameWithoutExtension(emitFormTemplate);
    var layout = FormLayoutGenerator.Generate(templateBytes, templateId, templateId);
    string layoutJson = JsonSerializer.Serialize(layout,
        new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
    string templateOut = Path.ChangeExtension(emitFormTemplate, ".template.json");
    await File.WriteAllTextAsync(templateOut, layoutJson);
    int checkboxes = layout.Elements.Count(e => e.Kind == FormElementKind.Checkbox);
    Console.WriteLine($"Template draft: {layout.Elements.Count} elements ({checkboxes} checkboxes) " +
                      $"across {layout.Elements.Select(e => e.Page).DefaultIfEmpty(0).Max()} page(s) → {templateOut}");
    Console.WriteLine("Review the labels (especially dense checkbox blocks), then bundle/register it.");
    return 0;
}

if (matchExtractPdf != null && matchExtractTemplate != null)
{
    if (!File.Exists(matchExtractPdf)) { Console.Error.WriteLine($"--match-extract: pdf not found: {matchExtractPdf}"); return 2; }
    if (!File.Exists(matchExtractTemplate)) { Console.Error.WriteLine($"--match-extract: template not found: {matchExtractTemplate}"); return 2; }

    var jsonOpts = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
    var template = JsonSerializer.Deserialize<FormLayout>(await File.ReadAllTextAsync(matchExtractTemplate), jsonOpts)!;
    byte[] uploadBytes = await File.ReadAllBytesAsync(matchExtractPdf);

    using var probe = UglyToad.PdfPig.PdfDocument.Open(uploadBytes);
    int pages = probe.NumberOfPages;
    Console.WriteLine($"Upload: {Path.GetFileName(matchExtractPdf)} ({pages} page(s)) vs template '{template.Name}'");

    int totalFields = 0;
    for (int pg = 1; pg <= pages; pg++)
    {
        var match = FormMatcher.MatchPage(uploadBytes, pg, new[] { template });
        if (match is null) { Console.WriteLine($"  page {pg}: no template match → DEFAULT pipeline"); continue; }

        var fields = TemplateExtractor.Extract(uploadBytes, pg, match.Template, match.TemplatePage);
        Console.WriteLine($"  page {pg}: MATCH template page {match.TemplatePage} (score {match.Score:F3}) → {fields.Count} field(s)");
        foreach (var f in fields.Where(f => f.Kind == FieldKind.Checkbox))
            Console.WriteLine($"      [X] {f.Value}");
        foreach (var f in fields.Where(f => f.Kind == FieldKind.Text))
            Console.WriteLine($"      {f.Name} = {f.Value}");
        totalFields += fields.Count;
    }
    Console.WriteLine($"Total deterministic fields extracted: {totalFields}");
    return 0;
}

if (routePdf != null)
{
    if (!File.Exists(routePdf)) { Console.Error.WriteLine($"--route: pdf not found: {routePdf}"); return 2; }

    TemplateStore? store = routeDb != null ? new TemplateStore(routeDb) : null;
    var registry = new TemplateRegistry(store);
    Console.WriteLine($"Registry: {registry.Bundled.Count} bundled template(s)" +
                      (store != null ? $" + customer store '{routeDb}' → {registry.All().Count} total" : "") + ".");

    var router = new TemplateRouter(registry);
    byte[] uploadBytes = await File.ReadAllBytesAsync(routePdf);
    var routes = router.RouteDocument(uploadBytes);

    int matched = 0, fieldTotal = 0;
    foreach (var r in routes)
    {
        if (r.Matched)
        {
            matched++; fieldTotal += r.Fields.Count;
            Console.WriteLine($"  page {r.Page}: → TEMPLATE '{r.Match!.Template.TemplateId}' " +
                              $"(score {r.Match.Score:F3}, page {r.Match.TemplatePage}) → {r.Fields.Count} field(s)");
        }
        else Console.WriteLine($"  page {r.Page}: → DEFAULT pipeline (no confident match)");
    }
    Console.WriteLine($"{matched}/{routes.Count} page(s) template-routed; {fieldTotal} deterministic field(s).");
    store?.Dispose();
    return 0;
}

// ── Customer template library (BYO templates) management ──────────────────────────────
if (regBlank != null && regDb != null)
{
    if (!File.Exists(regBlank)) { Console.Error.WriteLine($"--register: blank pdf not found: {regBlank}"); return 2; }
    using var lib = new TemplateLibrary(regDb);
    string id = Path.GetFileNameWithoutExtension(regBlank);
    var draft = lib.Register(await File.ReadAllBytesAsync(regBlank), id, id);
    int cb = draft.Elements.Count(e => e.Kind == FormElementKind.Checkbox);
    Console.WriteLine($"Registered '{id}' → {draft.Elements.Count} elements ({cb} checkboxes) into {regDb}.");
    Console.WriteLine($"Draft labels are auto-paired. Review them:");
    Console.WriteLine($"  --export-template {regDb} {id} {id}.review.json   (edit labels)   --import-template {regDb} {id}.review.json");
    return 0;
}

if (listTplDb != null)
{
    using var lib = new TemplateLibrary(listTplDb);
    var cust = lib.CustomerTemplates();
    Console.WriteLine($"Customer templates in {listTplDb}: {cust.Count}");
    foreach (var t in cust)
        Console.WriteLine($"  {t.TemplateId,-20} \"{t.Name}\"  {t.Elements.Count} elements  fp={(t.Fingerprint is { Length: >= 8 } f ? f[..8] : "—")}");
    Console.WriteLine($"(+ {lib.AllTemplates().Count - cust.Count} bundled federal templates also routable)");
    return 0;
}

if (exportTpl != null)
{
    using var lib = new TemplateLibrary(exportTpl[0]);
    var t = lib.Get(exportTpl[1]);
    if (t is null) { Console.Error.WriteLine($"--export-template: '{exportTpl[1]}' not found in {exportTpl[0]}"); return 2; }
    await File.WriteAllTextAsync(exportTpl[2], TemplateLibrary.ToJson(t));
    Console.WriteLine($"Exported '{exportTpl[1]}' → {exportTpl[2]}. Edit the labels, then --import-template {exportTpl[0]} {exportTpl[2]}");
    return 0;
}

if (importTpl != null)
{
    if (!File.Exists(importTpl[1])) { Console.Error.WriteLine($"--import-template: file not found: {importTpl[1]}"); return 2; }
    using var lib = new TemplateLibrary(importTpl[0]);
    var reviewed = TemplateLibrary.FromJson(await File.ReadAllTextAsync(importTpl[1]));
    lib.Update(reviewed);
    Console.WriteLine($"Imported reviewed template '{reviewed.TemplateId}' into {importTpl[0]}.");
    return 0;
}

if (unregTpl != null)
{
    using var lib = new TemplateLibrary(unregTpl[0]);
    Console.WriteLine(lib.Unregister(unregTpl[1])
        ? $"Unregistered '{unregTpl[1]}' from {unregTpl[0]}."
        : $"'{unregTpl[1]}' not found in {unregTpl[0]}.");
    return 0;
}

if (pdfDir == null || !Directory.Exists(pdfDir))
{
    Console.Error.WriteLine(
        "Usage: Foliant.Verification <pdf-dir> [out-dir] [--models <dir>] [--ocr-only] " +
        "[--gate3 <truth.csv>] [--gate3-extract <truth.csv>] [--gate3-scanpairs <td41-dir>] [--gate3-dump-crossfield <csv>] [--gate3-dump-missing <csv>] [--gate3-dump-garbled <csv>] [--refine-word-boxes] [--lilt-extract] [--lilt-only] [--lilt-conf <f>] [--gate5 <truth-dir>] [--gate6 <truth-dir>] " +
        "[--gate7 <born-digital-dir> [--gate7-pages N]] " +
        "[--gate8 <born-digital-dir> [--gate8-pages N]] " +
        "[--orient-check [--orient-pages N]] [--no-orientation] [--enumerator-order] " +
        "[--emit-reading-order <dir>] [--emit-form-kv <dir> [--kv-license <tag>] [--form-kv-overlay]] " +
        "[--synth-form-kv <out-dir> [--kv-license <tag>] [--synth-variants N]] " +
        "[--emit-form-template <blank.pdf>] [--match-extract <filled.pdf> <template.json>] " +
        "[--route <upload.pdf> [--templates-db <file>]] " +
        "[--register <blank.pdf> <db>] [--list-templates <db>] [--export-template <db> <id> <out.json>] " +
        "[--import-template <db> <reviewed.json>] [--unregister <db> <id>] " +
        "[--with-templates] [--rec-model <rec.onnx> [--rec-dict <dict.txt>]] " +
        "[--super-res <model.onnx> [--super-res-tile N]] " +
        "[--table-backend tt|slanet] [--reading-order xycut|xycut++]");
    return 2;
}

var pdfs = Directory.GetFiles(pdfDir, "*.pdf", SearchOption.AllDirectories).OrderBy(p => p).ToList();
if (pdfs.Count == 0) { Console.Error.WriteLine($"No PDFs in {pdfDir}."); return 2; }

// --sample-pdfs N: seeded random subset for big corpora (same seed → same subset, so a re-run
// after a fix measures the identical slice). 0 = the whole corpus.
if (samplePdfs > 0 && samplePdfs < pdfs.Count)
{
    var sampleRng = new Random(sampleSeed);
    pdfs = pdfs.OrderBy(_ => sampleRng.Next()).Take(samplePdfs).OrderBy(p => p).ToList();
    Console.WriteLine($"Mode: --sample-pdfs {samplePdfs} (seed {sampleSeed}) of the full corpus");
}

// Synthetic-fill mode (ADR-0001 Lever 2 value signal): fill blank templates in <pdf-dir> with
// synthetic values → license-clean form-kv records. Renderer-only; short-circuits before models.
if (synthFormKvDir != null)
    return await SyntheticFormFiller.RunAsync(pdfDir, synthFormKvDir, kvLicense, synthVariants, new ProcessingOptions().Dpi)
        ? 0 : 1;

// LiLT Gate-3 eval (born-digital quick read): score the model's VALUE-word predictions on filled
// AcroForms against the widget /V regions. Pure PdfPig + LiLT, no rendering — short-circuits the
// model-loading pipeline below. NOTE: this is a sanity read, not LiLT's production niche (forms with
// /V are handled exactly by AcroFormFieldExtractor); the flattened/OCR eval is the real Gate-3 test.
if (formKvEval)
    return await FormKvEvalRunner.RunAsync(pdfDir, liltModelDir) ? 0 : 1;

Directory.CreateDirectory(outDir);
// Gate 3 extraction mode wires the composite form-field extractor (AcroForm + the SF-33 geometric
// profile); every other mode uses the default (AcroForm only). --lilt-extract appends the learned
// LiLT arm (last in the chain: exact sources win, the model only sees pages they abstain on);
// --lilt-only runs the learned arm alone for attribution.
using LiltFormKvModel? liltKvModel = liltExtract
    ? new LiltFormKvModel(liltModelDir)
    : null;
IFormFieldExtractor? formExtractor =
    dumpWidgetFields ? new WidgetFormFieldExtractor()
    : liltOnly ? new LiltFormFieldExtractor(liltKvModel!) { MinConfidence = liltConf, EmitUnpairedValues = liltEmitUnpaired, RefineWordBoxes = refineWordBoxes }
    : gate3ExtractCsv != null
        ? new CompositeFormFieldExtractor(
            new IFormFieldExtractor[]
                {
                    new AcroFormFieldExtractor(),
                    new GeometricFormFieldExtractor(new[] { SampleProfiles.Sf33Solicitation }),
                }
                .Concat(liltKvModel is not null
                    ? new IFormFieldExtractor[] { new LiltFormFieldExtractor(liltKvModel) { MinConfidence = liltConf, EmitUnpairedValues = liltEmitUnpaired, RefineWordBoxes = refineWordBoxes } }
                    : Array.Empty<IFormFieldExtractor>())
                .ToArray())
        : liltKvModel is not null ? new LiltFormFieldExtractor(liltKvModel) { MinConfidence = liltConf, EmitUnpairedValues = liltEmitUnpaired, RefineWordBoxes = refineWordBoxes } : null;
if (liltExtract) Console.WriteLine($"Mode: --lilt-{(liltOnly ? "only" : "extract")} (learned form-KV arm; model: {liltModelDir})");
// --with-templates wires the per-page router over the bundled federal templates into the full pipeline,
// so matched pages get deterministic, label-bound fields + an appended template-field Markdown section.
IPageTemplateRouter? pipelineRouter = withTemplates
    ? new TemplateRouter(new TemplateRegistry(routeDb != null ? new TemplateStore(routeDb) : null))
    : null;
IScanUpscaler? superResUpscaler = superResModel != null
    ? new OnnxSuperResolutionUpscaler(superResModel, new SuperResolutionOptions
        { UseCuda = superResCuda, FallbackTile = superResTile })
    : null;
using var processor = FoliantProcessor.CreateDefault(modelsDir, tableBackend, readingOrder, formExtractor, pipelineRouter,
    recognitionModelPath: recModel, recognitionDictPath: recDict, scanUpscaler: superResUpscaler,
    splitMergedOcrRows: rowSplit);
if (recModel != null) Console.WriteLine($"Mode: --rec-model '{recModel}' (A/B recognition model; dict: {recDict ?? "catalog default"})");
if (superResModel != null) Console.WriteLine($"Mode: --super-res '{superResModel}' (ML super-resolution on low-DPI scans; tile {superResTile})");
if (withTemplates) Console.WriteLine("Mode: --with-templates (per-page template routing on; bundled federal templates)");
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
    EnumeratorReadingOrder = enumeratorOrder,
    ExtractFormFields = gate3ExtractCsv != null || dumpWidgetFields || liltExtract,
    UpscaleLowResolutionScans = superResModel != null,   // --super-res turns on the low-DPI upscale path
    RetryLowResolutionPages = !noRetryLadder,            // --no-retry-ladder: ADR-0004 Gate 9b A/B
    RecoverEmbeddedImageText = !noImageRecovery,         // --no-image-recovery: ADR-0004 Gate 9b A/B
};
// Scanned-holdout Gate 3: short-circuits the corpus sweep — pairs are enumerated from the TD-41
// dir itself (digital twins = truth, scanned twins = input through the learned arm wired above).
if (gate3ScanPairsDir != null)
    return await Gate3ScanPairsRunner.RunAsync(processor, gate3ScanPairsDir, options, gate3DumpSpurious, gate3DumpCrossField, gate3DumpMissing, gate3DumpGarbled) ? 0 : 1;

if (enumeratorOrder) Console.WriteLine("Mode: --enumerator-order (numbered-mosaic reading-order post-pass on)");
if (ocrOnly) Console.WriteLine("Mode: --ocr-only (text layer disabled for extraction; still used as recall truth)");
if (noRetryLadder) Console.WriteLine("Mode: --no-retry-ladder (ADR-0004 low-res retry OFF — 1.4.0-equivalent A/B arm)");
if (noImageRecovery) Console.WriteLine("Mode: --no-image-recovery (ADR-0004 mixed-page OCR merge OFF — 1.4.0-equivalent A/B arm)");
if (rowSplit) Console.WriteLine("Mode: --row-split (merged-row OCR det-box splitting ON — measured net-negative Gate 3 2026-07-06; experiment arm)");
if (refineWordBoxes) Console.WriteLine("Mode: --refine-word-boxes (ink-trim emitted value boxes — box-fidelity rig; OUTPUT geometry only, model input unchanged)");
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
if (gate3Csv != null || gate3ExtractCsv != null || gate5Dir != null || gate6Dir != null || gate7Dir != null || gate8Dir != null)
{
    bool gatesOk = true;
    if (gate3Csv != null) gatesOk &= await Gate3Runner.RunAsync(processor, pdfDir, gate3Csv, options);
    if (gate3ExtractCsv != null) gatesOk &= await Gate3ExtractRunner.RunAsync(processor, pdfDir, gate3ExtractCsv, options);
    if (gate5Dir != null) gatesOk &= await Gate5Runner.RunAsync(processor, pdfDir, gate5Dir, options);
    if (gate6Dir != null) gatesOk &= await Gate6Runner.RunAsync(processor, pdfDir, gate6Dir, options);
    // Gates 7 and 8 manage their own per-page options (forced OCR + degradation transforms), so
    // they take the born-digital dir directly rather than the shared `options`/`pdfDir`.
    if (gate7Dir != null) gatesOk &= await Gate7Runner.RunAsync(processor, gate7Dir, outDir, gate7Pages);
    if (gate8Dir != null) gatesOk &= await Gate8Runner.RunAsync(processor, gate8Dir, outDir, gate8Pages, upscaler: superResUpscaler, dumpImages: gate8Dump);
    return gatesOk ? 0 : 1;
}

var rows = new List<Row>();
int emitted = 0;   // ADR-0001 reading-order records harvested (when --emit-reading-order is set)
int kvEmitted = 0; // ADR-0001 Lever 2 form-K-V records harvested (when --emit-form-kv is set)
int needsReviewPages = 0;                 // ADR-0004: pages flagged NeedsReview across the corpus
int sensitivityPages = 0;                 // pages carrying CUI/legacy/classification banner markings
var gate9Violations = new List<string>(); // ADR-0004 Gate 9: OCR page, ~zero words, NO notice — must be empty
var total = System.Diagnostics.Stopwatch.StartNew();

int pdfIndex = 0;
foreach (var pdf in pdfs)
{
    pdfIndex++;
    var name = Path.GetFileName(pdf);
    var stem = Path.GetFileNameWithoutExtension(pdf);
    // Progress + ETA from the running average — long corpus runs should always say where they are.
    string eta = pdfIndex > 1
        ? $", ~{total.Elapsed.TotalMinutes / (pdfIndex - 1) * (pdfs.Count - pdfIndex + 1):0} min left"
        : "";
    Console.WriteLine($"\n[{pdfIndex}/{pdfs.Count}] {name}  ({total.Elapsed.TotalMinutes:0} min elapsed{eta})");

    DocumentResult result;
    byte[] pdfBytes;
    try
    {
        pdfBytes = await File.ReadAllBytesAsync(pdf);
        result = await processor.ProcessAsync(pdfBytes, options);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ERROR: {ex.Message}");
        rows.Add(new Row(name, 0, 0, 0, 0, 0, 0, 0, null, 0, 0, true, $"error: {ex.Message}"));
        continue;
    }

    foreach (var page in result.Pages)
    {
        var v = page.Verification;
        // Reading-order fidelity from the SHIPPING pipeline's own per-page Markdown — the axis
        // recall is blind to (recall is set membership; a permuted page still scores 100%).
        var (orderAnchors, orderInSeq) = OrderScore.Measure(pdfBytes, page.PageNumber, page.Markdown);
        double? orderPct = orderAnchors >= 8 ? 100.0 * orderInSeq / orderAnchors : null;
        var (flagged, reason) = Flag(v, orderPct, page.Notice);
        rows.Add(new Row(name, page.PageNumber, page.Lines.Count, page.Regions.Count,
                         v.Seconds, v.LinesLost, v.TruthWords, v.TruthWordsFound,
                         v.RecallPercent, orderAnchors, orderInSeq, flagged, reason,
                         page.SensitivityMarking ?? ""));

        // ── Sensitivity markings (advisory): warn loudly so controlled content never flows
        //    into downstream systems unnoticed. ──────────────────────────────────────────────
        if (page.SensitivityMarking is { } marking)
        {
            sensitivityPages++;
            Console.WriteLine($"  ⚠ p{page.PageNumber:D3}  SENSITIVITY MARKING: {marking}");
        }

        // ── ADR-0004 Gate 9: no silent empty OCR page ─────────────────────────────────────
        // A page whose text came from pixels, produced ~nothing, AND has no text-layer truth
        // vouching for it (RecallPercent null → invisible to recall aggregates) MUST carry a
        // Notice (NeedsReview or recovered-via-retry). A ~2-word page WITH truth (a stamp-only
        // page whose sparse layer scored it) is visible to the recall metric and is not silent.
        if (page.NeedsReview) needsReviewPages++;
        int gate9Words = page.Lines.Sum(l =>
            l.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
        if (page.Source == TextSource.Ocr && gate9Words < 3 && v.TruthWords == 0 && page.Notice is null)
            gate9Violations.Add($"{name} p{page.PageNumber} ({gate9Words} words, no truth, no notice)");

        await File.WriteAllTextAsync(
            Path.Combine(outDir, $"{stem}_p{page.PageNumber:D3}.md"), page.Markdown);

        // Quality check for the widget+geometry structured-form path: dump each page's recovered
        // FormFields as "label :: value" so we can eyeball label-pairing accuracy on real forms.
        if (dumpWidgetFields && page.FormFields is { Count: > 0 })
        {
            await File.WriteAllTextAsync(
                Path.Combine(outDir, $"{stem}_p{page.PageNumber:D3}.fields.txt"),
                string.Join(Environment.NewLine, page.FormFields.Select(f => $"{f.Name}  ::  {f.Value}")));
            Console.WriteLine($"  form-fields: {page.FormFields.Count}");
        }

        // ADR-0001 harvester (opt-in, local-only): emit a reading-order training record from the
        // page's own text-layer order. License-clean; nothing leaves the machine.
        if (emitReadingOrderDir != null && ReadingOrderEmitter.Append(emitReadingOrderDir, pdfBytes, name, page))
            emitted++;

        // ADR-0001 Lever 2 harvester (opt-in, local-only): emit a form-K-V record from the page's
        // own AcroForm dictionary. License-clean; each record tagged with --kv-license provenance.
        if (emitFormKvDir != null && FormKvEmitter.Append(emitFormKvDir, kvLicense, pdfBytes, name, page.PageNumber, options.Dpi, emitFormKvOverlay))
            kvEmitted++;

        var recall = v.RecallPercent is { } r ? $"{r:0.0}%" : "n/a (no text layer)";
        var order = orderPct is { } o ? $"{o:0.0}%" : "n/a";
        var cov = v.LinesLost == 0 ? "OK" : $"LOST {v.LinesLost}";
        var src = page.Source == TextSource.TextLayer ? "layer" : "ocr";
        Console.WriteLine($"  p{page.PageNumber:D3}  {page.Lines.Count,4} lines  {v.Seconds,5:0.0}s  " +
                          $"src:{src}  cov:{cov}  recall:{recall}  order:{order}{(flagged ? "  ⚑" : "")}");
    }
}
total.Stop();

// ── Scorecard CSV ────────────────────────────────────────────────────────────
var csvPath = Path.Combine(outDir, "scorecard.csv");
await using (var csv = new StreamWriter(csvPath))
{
    await csv.WriteLineAsync(
        "pdf,page,lines,regions,seconds,coverage_missing,truth_words,truth_found,recall_pct,order_anchors,order_pct,flagged,reason,sensitivity");
    foreach (var s in rows)
        await csv.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"\"{s.Pdf}\",{s.Page},{s.Lines},{s.Regions},{s.Seconds:0.0},{s.Lost},{s.TruthWords},{s.TruthFound},{(s.Recall.HasValue ? s.Recall.Value.ToString("0.0", CultureInfo.InvariantCulture) : "")},{s.OrderAnchors},{(s.OrderPct.HasValue ? s.OrderPct.Value.ToString("0.0", CultureInfo.InvariantCulture) : "")},{s.Flagged},\"{s.Reason}\",\"{s.Sensitivity.Replace("\"", "\"\"")}\""));
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
var orderedRows = rows.Where(s => s.OrderPct.HasValue).ToList();
if (orderedRows.Count > 0)
    Console.WriteLine($"order:  avg {orderedRows.Average(s => s.OrderPct!.Value):0.0}%   " +
                      $"min {orderedRows.Min(s => s.OrderPct!.Value):0.0}%   " +
                      $"pages ≥{OrderScore.FlagThreshold:0}%: " +
                      $"{orderedRows.Count(s => s.OrderPct >= OrderScore.FlagThreshold)}/{orderedRows.Count}");
if (sensitivityPages > 0)
    Console.WriteLine($"⚠ SENSITIVITY MARKINGS (CUI/legacy/classification): {sensitivityPages} pages — " +
                      "this corpus contains CONTROLLED content; apply your data-handling policy.");
Console.WriteLine($"flagged for review: {flaggedRows.Count}/{rows.Count}");
foreach (var f in flaggedRows.Take(30))
    Console.WriteLine($"  {f.Pdf} p{f.Page}: {f.Reason}");
Console.WriteLine($"scorecard → {csvPath}");
if (emitReadingOrderDir != null)
    Console.WriteLine($"reading-order records harvested (local-only): {emitted} → " +
                      $"{Path.Combine(emitReadingOrderDir, "reading-order.jsonl")}");
if (emitFormKvDir != null)
    Console.WriteLine($"form-K-V records harvested (local-only, license={kvLicense}): {kvEmitted} → " +
                      $"{Path.Combine(emitFormKvDir, "form-kv.jsonl")}");

// Gate 1 is vacuous on corpora with NO text-layer truth anywhere (e.g. 100%-scan corpora):
// there is nothing to score recall against, so it can neither pass nor fail on merit.
bool gate1 = scored.Count == 0 || (avgRecall >= 98.0 && pct95 >= 98.0);
bool gate2 = totalLost == 0;
bool gate9 = gate9Violations.Count == 0;
Console.WriteLine("\n════ GATES (RESULTS.md) ════");
Console.WriteLine(scored.Count == 0
    ? "Gate 1 corpus recall   : N/A   (no text-layer truth in this corpus — recall is unscoreable; rely on Gate 9 honesty)"
    : $"Gate 1 corpus recall   : {(gate1 ? "PASS" : "FAIL")}  (avg {avgRecall:0.0}% / ≥95% on {pct95:0.0}% of pages)");
Console.WriteLine($"Gate 2 zero text loss  : {(gate2 ? "PASS" : "FAIL")}  ({totalLost} lines lost)");
// Gate 9 (ADR-0004): recall lines must never print without the review count next to them.
Console.WriteLine($"Gate 9 no silent empty : {(gate9 ? "PASS" : "FAIL")}  " +
                  $"({gate9Violations.Count} silent empty OCR pages; needs-review: {needsReviewPages} pages)");
foreach (var v9 in gate9Violations.Take(10))
    Console.WriteLine($"  GATE 9 VIOLATION: {v9}");

return gate1 && gate2 && gate9 ? 0 : 1;

static (bool Flagged, string Reason) Flag(PageVerification v, double? orderPct, string? notice = null)
{
    if (notice != null) return (true, notice);
    if (v.LinesLost > 0) return (true, $"{v.LinesLost} lines lost");
    if (v.TruthWords == 0) return (true, "no text layer (needs eyeball)");
    if (v.RecallPercent < 95.0) return (true, $"recall {v.RecallPercent:0.0}%");
    if (orderPct < OrderScore.FlagThreshold) return (true, $"reading-order {orderPct:0.0}% (scramble)");
    return (false, "");
}

internal sealed record Row(
    string Pdf, int Page, int Lines, int Regions, double Seconds,
    int Lost, int TruthWords, int TruthFound, double? Recall,
    int OrderAnchors, int OrderInSeq, bool Flagged, string Reason,
    string Sensitivity = "")
{
    /// <summary>Reading-order fidelity; null when too few anchor words to judge (sparse/tabular page).</summary>
    public double? OrderPct => OrderAnchors >= 8 ? 100.0 * OrderInSeq / OrderAnchors : null;
}

/// <summary>
/// Reading-order fidelity scored against the PDF's own text layer — the axis word-recall is blind
/// to (recall is set membership; a permuted page still scores 100%). PdfPig returns the page's
/// text-layer words in reading order; using only words that occur exactly once (clean position
/// anchors), we read off the pipeline output's anchor words in output order and measure the longest
/// run kept in increasing truth position (a longest-increasing-subsequence, O(n log n)). A page that
/// keeps every word but permutes it — the box-grid scramble — scores ~100% recall yet a low order
/// fraction. Same metric as the spike harness, but computed from the SHIPPING pipeline's per-page
/// Markdown, so the gate is a property of the library rather than a parallel re-implementation.
/// </summary>
internal static class OrderScore
{
    // Pages reading below this fidelity are flagged for review even at perfect recall.
    public const double FlagThreshold = 90.0;

    public static (int Anchors, int InSequence) Measure(byte[] pdfBytes, int pageNumber, string output)
    {
        try
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(pdfBytes);
            if (pageNumber < 1 || pageNumber > doc.NumberOfPages) return (0, 0);
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
            return (seq.Count, Lis(seq));
        }
        catch { return (0, 0); }
    }

    /// <summary>Length of the longest strictly-increasing subsequence (patience sorting, O(n log n)).</summary>
    private static int Lis(List<int> a)
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

    private static string Normalize(string s) =>
        new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
}
