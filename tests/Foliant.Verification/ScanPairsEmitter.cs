// Opt-in local harvester: scan ↔ digital-twin TRUTH pairs, from paired synthetic corpora
// (<corpus>/digital/X.pdf ↔ <corpus>/scanned/X_scan.pdf). This closes the ADR-0001 supervision
// gap for scanned pages: a physically-degraded page normally has no text layer and no AcroForm
// to self-supervise from — but its digital twin has BOTH, so each pair yields (scan image,
// exact truth) without any human annotation. Uses: scanned-form K-V training/eval (Lever 2's
// "does synthetic-template training generalize to scans"), OCR robustness eval, and
// sensitivity-marking OCR-robustness eval (the CUI corpus).
//
// Invariants (ADR-0001 governance, same as the other harvesters): LOCAL filesystem only, no
// network, no telemetry, active only via --emit-scan-pairs. Every record carries a `license`
// provenance tag; only permissively-tagged records may enter published artifacts, and
// `local-only` records (e.g. the CUI corpus, excluded from publication by user decision
// 2026-07-04) never leave the machine.
//
// GEOMETRY CAVEAT (read before training): token/field boxes are in the DIGITAL twin's render
// frame (origin top-left, Y down, pixels at --dpi). The scan raster is a physically-degraded
// capture of the same page — near-aligned but not pixel-registered (skew/offset from the scan
// simulation). Both page sizes are recorded; treat truth boxes as approximate localization on
// the scan, or register the pair first. Text/value truth is exact regardless.
//
// Usage: --emit-scan-pairs <paired-corpus-dir> <out-dir> [--dpi N] [--license <tag>]

using System.Runtime.InteropServices;
using System.Text.Json;
using Foliant;
using Foliant.Pipeline;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.AcroForms.Fields;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Tokens;

namespace Foliant.Verification;

internal static class ScanPairsEmitter
{
    private sealed record TokenDto(string text, float[] bbox);
    private sealed record FieldDto(string name, string value, string kind, float[]? bbox);

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: --emit-scan-pairs <paired-corpus-dir> <out-dir> [--dpi N] [--license <tag>]");
            return 2;
        }

        string corpusDir = Path.GetFullPath(args[0]);
        string outDir = Path.GetFullPath(args[1]);
        int dpi = 300;
        string license = "local-only";
        for (int i = 2; i < args.Length; i++)
        {
            if (args[i] == "--dpi" && i + 1 < args.Length) dpi = int.Parse(args[++i]);
            else if (args[i] == "--license" && i + 1 < args.Length) license = args[++i];
        }

        string digitalDir = Path.Combine(corpusDir, "digital");
        string scannedDir = Path.Combine(corpusDir, "scanned");
        if (!Directory.Exists(digitalDir) || !Directory.Exists(scannedDir))
        {
            Console.Error.WriteLine($"emit-scan-pairs: expected {corpusDir}/digital and /scanned");
            return 2;
        }

        var renderer = new PdfPageRenderer();
        Directory.CreateDirectory(outDir);
        int pairs = 0, pages = 0, skipped = 0;

        var scanPdfs = Directory.GetFiles(scannedDir, "*.pdf").OrderBy(p => p).ToList();
        int docIndex = 0;
        foreach (string scanPath in scanPdfs)
        {
            docIndex++;
            string stem = Path.GetFileNameWithoutExtension(scanPath);
            Console.WriteLine($"[{docIndex}/{scanPdfs.Count}] {stem}");
            string digitalPath = Path.Combine(digitalDir,
                stem.EndsWith("_scan", StringComparison.Ordinal) ? stem[..^5] + ".pdf" : stem + ".pdf");
            if (!File.Exists(digitalPath)) { skipped++; continue; }

            byte[] scanBytes = File.ReadAllBytes(scanPath);
            byte[] digitalBytes = File.ReadAllBytes(digitalPath);

            using var digitalDoc = PdfDocument.Open(digitalBytes);
            int pageCount = Math.Min(renderer.GetPageCount(scanBytes), digitalDoc.NumberOfPages);

            for (int p = 1; p <= pageCount; p++)
            {
                var digitalPage = digitalDoc.GetPage(p);
                float scale = dpi / 72f;
                float pageH = (float)digitalPage.Height;

                // Truth 1: text-layer tokens with boxes (digital frame).
                var tokens = new List<TokenDto>();
                foreach (var w in digitalPage.GetWords())
                {
                    if (string.IsNullOrWhiteSpace(w.Text)) continue;
                    var bb = w.BoundingBox;
                    var px = ToPx((float)bb.Left, (float)bb.Right, (float)bb.Top, (float)bb.Bottom, pageH, scale);
                    if (px[2] - px[0] <= 0 || px[3] - px[1] <= 0) continue;
                    tokens.Add(new TokenDto(w.Text, px));
                }

                // Truth 2: AcroForm fields + visible widget rects (digital frame; may be empty).
                var fields = CollectFields(digitalDoc, digitalPage, p, pageH, scale);

                if (tokens.Count == 0 && fields.Count == 0) continue;   // no truth → no pair

                // Training input: the SCANNED page raster.
                var scanImg = renderer.Render(scanBytes, p, dpi);
                string safe = Sanitize(stem);
                string imgRel = Path.Combine("images", $"{safe}__p{p:D3}.png").Replace('\\', '/');
                string imgAbs = Path.Combine(outDir, imgRel);
                Directory.CreateDirectory(Path.GetDirectoryName(imgAbs)!);
                WritePng(scanImg, imgAbs);

                // Digital render size (truth frame) without rendering: page points × scale.
                var record = new
                {
                    corpus = Path.GetFileName(corpusDir),
                    doc = stem,
                    page = p,
                    dpi,
                    license,
                    scan_image = imgRel,
                    scan_width_px = scanImg.Width,
                    scan_height_px = scanImg.Height,
                    digital_width_px = (int)Math.Round(digitalPage.Width * scale),
                    digital_height_px = (int)Math.Round(digitalPage.Height * scale),
                    tokens,
                    fields,
                };
                using (var w2 = new StreamWriter(Path.Combine(outDir, "scan-pairs.jsonl"), append: true))
                    w2.WriteLine(JsonSerializer.Serialize(record));
                pages++;
            }
            pairs++;
        }

        Console.WriteLine($"emit-scan-pairs: {pairs} document pairs → {pages} page records " +
                          $"(license={license}), {skipped} scans without a digital twin skipped");
        Console.WriteLine($"→ {Path.Combine(outDir, "scan-pairs.jsonl")}  (+ images/) — LOCAL-ONLY output; " +
                          "publication requires the record license to be permissive AND the manifest sign-off.");
        return 0;
    }

    // AcroForm name/value/kind + visible widget rects — same approach as FormKvEmitter (which
    // rides the harness page loop and writes its own record shape, hence the local copy).
    private static List<FieldDto> CollectFields(
        PdfDocument doc, UglyToad.PdfPig.Content.Page page, int pageNumber, float pageH, float scale)
    {
        var result = new List<FieldDto>();
        if (!doc.TryGetForm(out var form)) return result;

        var fields = new List<(string Name, string Value, string Kind)>();
        foreach (var field in form.GetFieldsForPage(pageNumber))
        {
            string name = field.Information.PartialName ?? string.Empty;
            switch (field)
            {
                case AcroTextField text: fields.Add((name, text.Value ?? string.Empty, "Text")); break;
                case AcroCheckboxField box: fields.Add((name, box.IsChecked ? "checked" : "unchecked", "Checkbox")); break;
            }
        }
        if (fields.Count == 0) return result;

        var rects = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var ann in page.GetAnnotations())
            {
                if (ann.Type != AnnotationType.Widget) continue;
                if (!TryReadPartialName(ann.AnnotationDictionary, out var name)) continue;
                if (ann.Flags.HasFlag(AnnotationFlags.Hidden) || ann.Flags.HasFlag(AnnotationFlags.NoView))
                {
                    hidden.Add(name);
                    continue;
                }
                var r = ann.Rectangle;
                rects[name] = ToPx((float)r.Left, (float)r.Right, (float)r.Top, (float)r.Bottom, pageH, scale);
            }
        }
        catch { /* best-effort */ }
        hidden.ExceptWith(rects.Keys);

        foreach (var f in fields)
        {
            if (hidden.Contains(f.Name)) continue;
            result.Add(new FieldDto(f.Name, f.Value, f.Kind,
                rects.TryGetValue(f.Name, out var r) ? r : null));
        }
        return result;
    }

    private static bool TryReadPartialName(DictionaryToken dict, out string name)
    {
        name = string.Empty;
        if (dict.TryGet(NameToken.Create("T"), out StringToken s)) { name = s.Data; return true; }
        return false;
    }

    // PDF points (origin bottom-left, Y up) → raster px (origin top-left, Y down) — the same
    // transform the text-layer reader and FormKvEmitter use, so all truth boxes share one frame.
    private static float[] ToPx(double left, double right, double top, double bottom, float pageH, float scale)
    {
        float xA = (float)left * scale, xB = (float)right * scale;
        float yA = (pageH - (float)top) * scale, yB = (pageH - (float)bottom) * scale;
        return [Math.Min(xA, xB), Math.Min(yA, yB), Math.Max(xA, xB), Math.Max(yA, yB)];
    }

    private static void WritePng(PageImage img, string path)
    {
        using var bmp = new SKBitmap(new SKImageInfo(img.Width, img.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        Marshal.Copy(img.PixelsBgra8888, 0, bmp.GetPixels(), img.PixelsBgra8888.Length);
        bmp.NotifyPixelsChanged();
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path);
        data.SaveTo(fs);
    }

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Replace(' ', '_');
    }
}
