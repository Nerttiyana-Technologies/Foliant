// FormDatasetEmitter — ADR-0002 milestone 1 (`--emit-form-dataset`).
//
// Auto-labels a dataset for the 2.0.0 learned field/checkbox LOCATOR, using born-digital federal forms as
// their OWN ground truth: each AcroForm widget gives — for free and exactly — a field's rect, kind
// (text/checkbox), value (/V), checked state (/AS), field name (/T) and human label (/TU). We render the page
// to a raster (which is already the "flattened/scanned" appearance the model sees at inference — the widgets
// are used only to produce labels, never shown to the model) and emit one record per field.
//
// DATA-POLICY GUARDRAIL (foliant-data-strategy): the BASE model trains on PUBLIC data only — blank federal SF
// templates (data/blank_pdfs) + public-domain solicitations. Do NOT point --source public at customer/private
// corpora. Private corpora (e.g. ZeroDep/private/data) are for EVALUATION (held-out agencies) or on-device
// adaptation, tagged --source eval, and must never feed base training. The tag is written into every record
// so the training split can enforce it.
//
// Output:
//   <outDir>/images/<pdfstem>_p<NN>_<variant>.png
//   <outDir>/labels.jsonl          one JSON object per image (schema: DatasetRecord below)
//   <outDir>/manifest.json         run parameters + counts
//
// Augmentation (--degrade): only DIMENSION-PRESERVING degradations are applied, so the normalized field
// rectangles stay valid without transforming them — Downscale(150/100/72 DPI), JPEG(40), Gaussian blur/noise,
// faded contrast. Geometric skew/offset is intentionally NOT applied here: it would move the target boxes and
// needs a matching coordinate transform (left as a TODO so labels are never silently wrong).
//
// Wire into the harness (tests/Foliant.Verification/Program.cs), at the top of arg dispatch:
//     if (args is ["--emit-form-dataset", .. var rest]) return FormDatasetEmitter.Run(rest);
//
// Usage:
//     dotnet run -c Release --project tests/Foliant.Verification -- \
//         --emit-form-dataset <pdfDir> <outDir> [--dpi 300] [--degrade] [--max N] [--source public|eval]

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using Foliant;
using Foliant.Pipeline;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Tokens;

namespace Foliant.Verification;

public static class FormDatasetEmitter
{
    public sealed record FieldLabel(
        [property: JsonPropertyName("x1")] float X1,
        [property: JsonPropertyName("y1")] float Y1,
        [property: JsonPropertyName("x2")] float X2,
        [property: JsonPropertyName("y2")] float Y2,
        [property: JsonPropertyName("kind")] string Kind,        // "text" | "checkbox"
        [property: JsonPropertyName("name")] string? Name,       // AcroForm field name (/T)
        [property: JsonPropertyName("label")] string? Label,     // human label (/TU), null if absent
        [property: JsonPropertyName("value")] string? Value,     // filled text value (/V)
        [property: JsonPropertyName("checked")] bool Checked);   // checkbox /AS != Off

    public sealed record DatasetRecord(
        [property: JsonPropertyName("image")] string Image,
        [property: JsonPropertyName("source_pdf")] string SourcePdf,
        [property: JsonPropertyName("page")] int Page,
        [property: JsonPropertyName("width")] int Width,
        [property: JsonPropertyName("height")] int Height,
        [property: JsonPropertyName("dpi")] int Dpi,
        [property: JsonPropertyName("variant")] string Variant,        // "clean" | "dpi150" | "jpeg40" | ...
        [property: JsonPropertyName("provenance")] string Provenance,  // "public" | "eval"
        [property: JsonPropertyName("fields")] IReadOnlyList<FieldLabel> Fields);

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: --emit-form-dataset <pdfDir> <outDir> [--dpi 300] [--degrade] [--max N] [--source public|eval]");
            return 2;
        }

        string pdfDir = args[0];
        string outDir = args[1];
        int dpi = ArgInt(args, "--dpi", 300);
        int max = ArgInt(args, "--max", int.MaxValue);
        bool degrade = args.Contains("--degrade");
        string source = ArgStr(args, "--source", "public");   // provenance tag stamped into every record

        if (!Directory.Exists(pdfDir)) { Console.Error.WriteLine($"no such dir: {pdfDir}"); return 2; }
        string imgDir = Path.Combine(outDir, "images");
        Directory.CreateDirectory(imgDir);

        var renderer = new PdfPageRenderer();
        using var labels = new StreamWriter(Path.Combine(outDir, "labels.jsonl"), append: false);
        var json = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

        // Dimension-preserving degradations only (rects stay valid). Name → IPageImageTransform.
        var variants = new List<(string Name, IPageImageTransform T)> { ("clean", ScanDegrader.Identity) };
        if (degrade)
            variants.AddRange(new (string, IPageImageTransform)[]
            {
                ("dpi150", ScanDegrader.Downscale(150)),
                ("dpi100", ScanDegrader.Downscale(100)),
                ("dpi72",  ScanDegrader.Downscale(72)),
                ("jpeg40", ScanDegrader.JpegRecompress(40)),
                ("blur",   ScanDegrader.GaussianBlur(1.2f)),
                ("faded",  ScanDegrader.FadeContrast(0.5)),
            });

        int pdfs = 0, pagesEmitted = 0, recordsEmitted = 0, skippedNoWidgets = 0;
        var pdfFiles = Directory.EnumerateFiles(pdfDir, "*.pdf", SearchOption.AllDirectories).OrderBy(p => p);

        foreach (var pdfPath in pdfFiles)
        {
            if (pdfs >= max) break;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(pdfPath); }
            catch (Exception ex) { Console.Error.WriteLine($"skip {pdfPath}: {ex.Message}"); continue; }

            string stem = Path.GetFileNameWithoutExtension(pdfPath);
            int pageCount;
            try { pageCount = renderer.GetPageCount(bytes); }
            catch (Exception ex) { Console.Error.WriteLine($"skip {pdfPath}: {ex.Message}"); continue; }

            bool anyPage = false;
            for (int pageNo = 1; pageNo <= pageCount; pageNo++)
            {
                List<FieldLabel> fields;
                try { fields = ReadWidgets(bytes, pageNo); }
                catch (Exception ex) { Console.Error.WriteLine($"  {stem} p{pageNo}: widget read failed: {ex.Message}"); continue; }

                if (fields.Count == 0) { skippedNoWidgets++; continue; }   // no born-digital ground truth → skip

                PageImage clean;
                try { clean = renderer.Render(bytes, pageNo, dpi); }
                catch (Exception ex) { Console.Error.WriteLine($"  {stem} p{pageNo}: render failed: {ex.Message}"); continue; }

                foreach (var (vName, transform) in variants)
                {
                    PageImage img = transform.Transform(clean);
                    string file = $"{stem}_p{pageNo:D3}_{vName}.png";
                    SavePng(img, Path.Combine(imgDir, file));

                    var rec = new DatasetRecord(
                        Image: Path.Combine("images", file),
                        SourcePdf: Path.GetRelativePath(pdfDir, pdfPath),
                        Page: pageNo, Width: img.Width, Height: img.Height, Dpi: dpi,
                        Variant: vName, Provenance: source, Fields: fields);
                    labels.WriteLine(JsonSerializer.Serialize(rec, json));
                    recordsEmitted++;
                }
                pagesEmitted++;
                anyPage = true;
            }
            if (anyPage) pdfs++;
            if (pdfs % 25 == 0 && anyPage) Console.Error.WriteLine($"…{pdfs} pdfs, {pagesEmitted} pages, {recordsEmitted} records");
        }

        labels.Flush();
        File.WriteAllText(Path.Combine(outDir, "manifest.json"), JsonSerializer.Serialize(new
        {
            pdfDir, outDir, dpi, degrade, source,
            variants = variants.Select(v => v.Name),
            pdfs_with_widgets = pdfs, pages = pagesEmitted, records = recordsEmitted,
            pages_skipped_no_widgets = skippedNoWidgets,
            generated_utc = DateTime.UtcNow
        }, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"done: {pdfs} born-digital PDFs, {pagesEmitted} pages, {recordsEmitted} records " +
                          $"({variants.Count} variant(s)); {skippedNoWidgets} pages skipped (no widgets). → {outDir}");
        return 0;
    }

    // Reads each visible AcroForm widget on the page as a ground-truth field. Mirrors FormLayoutGenerator's
    // PdfPig usage, plus /V (value), /AS (checkbox state), /T (name), /TU (label).
    private static List<FieldLabel> ReadWidgets(byte[] pdf, int pageNo)
    {
        var outFields = new List<FieldLabel>();
        using var doc = PdfDocument.Open(pdf);
        if (pageNo < 1 || pageNo > doc.NumberOfPages) return outFields;
        var page = doc.GetPage(pageNo);
        float w = (float)page.Width, h = (float)page.Height;

        foreach (var ann in page.GetAnnotations())
        {
            if (ann.Type != AnnotationType.Widget) continue;
            if (ann.Flags.HasFlag(AnnotationFlags.Hidden) || ann.Flags.HasFlag(AnnotationFlags.NoView)) continue;

            var d = ann.AnnotationDictionary;
            string? ft = Ft(d) ?? (Parent(d) is { } p ? Ft(p) : null);
            if (ft is null) continue;
            bool isCheckbox = ft == "Btn";

            var r = ann.Rectangle;
            float x1 = (float)r.Left / w, x2 = (float)r.Right / w;
            float y1 = (h - (float)r.Top) / h, y2 = (h - (float)r.Bottom) / h;
            float rx1 = MathF.Min(x1, x2), ry1 = MathF.Min(y1, y2), rx2 = MathF.Max(x1, x2), ry2 = MathF.Max(y1, y2);
            if (rx2 <= rx1 || ry2 <= ry1) continue;

            outFields.Add(new FieldLabel(
                rx1, ry1, rx2, ry2,
                Kind: isCheckbox ? "checkbox" : "text",
                Name: Str(d, "T") ?? (Parent(d) is { } pp ? Str(pp, "T") : null),
                Label: Str(d, "TU") ?? (Parent(d) is { } pq ? Str(pq, "TU") : null),
                Value: isCheckbox ? null : (Val(d) ?? (Parent(d) is { } pr ? Val(pr) : null)),
                Checked: isCheckbox && IsChecked(d)));
        }
        return outFields;
    }

    private static string? Ft(DictionaryToken d) =>
        d.TryGet(NameToken.Create("FT"), out NameToken ft) ? ft.Data : null;

    private static DictionaryToken? Parent(DictionaryToken d) =>
        d.TryGet(NameToken.Create("Parent"), out DictionaryToken p) ? p : null;

    private static string? Str(DictionaryToken d, string key) =>
        d.TryGet(NameToken.Create(key), out StringToken s) ? s.Data : null;

    // /V may be a string (text field) or a name (some buttons); take whichever is present.
    private static string? Val(DictionaryToken d)
    {
        if (d.TryGet(NameToken.Create("V"), out StringToken s)) return s.Data;
        if (d.TryGet(NameToken.Create("V"), out NameToken n)) return n.Data;
        return null;
    }

    // Checkbox/radio is "on" when its appearance state /AS is present and not "Off".
    private static bool IsChecked(DictionaryToken d) =>
        d.TryGet(NameToken.Create("AS"), out NameToken a) && !string.Equals(a.Data, "Off", StringComparison.Ordinal);

    private static void SavePng(PageImage page, string path)
    {
        var info = new SKImageInfo(page.Width, page.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var pixels = page.PixelsBgra8888;
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var bmp = new SKBitmap();
            bmp.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes);
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100)
                ?? throw new InvalidOperationException("PNG encode failed.");
            using var fs = File.Create(path);
            data.SaveTo(fs);
        }
        finally { handle.Free(); }
    }

    private static int ArgInt(string[] a, string flag, int dflt)
    {
        int i = Array.IndexOf(a, flag);
        return (i >= 0 && i + 1 < a.Length && int.TryParse(a[i + 1], out int v)) ? v : dflt;
    }

    private static string ArgStr(string[] a, string flag, string dflt)
    {
        int i = Array.IndexOf(a, flag);
        return (i >= 0 && i + 1 < a.Length) ? a[i + 1] : dflt;
    }
}
