// Synthetic-fill generator (ADR-0001 Lever 2 value signal). Blank public-domain form templates give
// field LOCATIONS but no VALUES; this fills them with synthetic values to produce license-clean
// (image, tokens, name/value/rect) training records WITH values.
//
// Approach: raster-level fill using ONLY existing deps (pdfium render + SkiaSharp draw) — no new
// dependency, and no reliance on AcroForm appearance generation. For each fillable page:
//   1. render the blank template page (pdfium),
//   2. read each non-hidden text/checkbox widget's name + rect (PdfPig, same transform as the emitter),
//   3. draw a synthetic value into each widget rect (SkiaSharp),
//   4. emit a form-kv record: image = the drawn page; fields = name/value(synthetic)/kind/rect;
//      tokens = the template's text-layer words PLUS the synthetic value tokens (so the value text is
//      present as a token, matching what OCR would see on a real filled form).
//
// License: the synthetic VALUES contain no third-party content, so a record's provenance = the
// TEMPLATE's (pass --kv-license public-domain when filling public-domain US-gov templates). Local,
// no network, opt-in (only runs under --synth-form-kv).

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

internal static class SyntheticFormFiller
{
    private sealed record TokenDto(string text, float[] bbox);
    private sealed record FieldDto(string name, string value, string kind, string source, float[] bbox);

    public static async Task<bool> RunAsync(
        string templatesDir, string outDir, string license, int variants, int dpi)
    {
        var pdfs = Directory.GetFiles(templatesDir, "*.pdf").OrderBy(p => p).ToList();
        if (pdfs.Count == 0) { Console.Error.WriteLine($"No template PDFs in {templatesDir}."); return false; }
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(Path.Combine(outDir, "images"));
        var renderer = new PdfPageRenderer();
        int records = 0, fieldsTotal = 0;

        foreach (var path in pdfs)
        {
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(path); } catch { continue; }
            string name = Path.GetFileName(path);
            using var doc = PdfDocument.Open(bytes);
            // NOTE: do NOT gate on doc.TryGetForm — static-XFA forms (most federal SSA/GSA forms) don't
            // surface via PdfPig's AcroForm API, but their field WIDGETS are on the page with /T, /FT,
            // /Rect. We drive extraction off the widget annotations, which works for plain AND XFA forms.

            for (int pageNo = 1; pageNo <= doc.NumberOfPages; pageNo++)
            {
                var page = doc.GetPage(pageNo);
                float scale = dpi / 72f, pageH = (float)page.Height;

                // Field name + kind + rect straight off the page's Widget annotations — works for both
                // plain AcroForms AND static-XFA forms (where GetFieldsForPage returns nothing).
                var widgets = new List<(string Name, string Kind, float[] Bbox)>();
                try
                {
                    foreach (var ann in page.GetAnnotations())
                    {
                        if (ann.Type != AnnotationType.Widget) continue;
                        if (ann.Flags.HasFlag(AnnotationFlags.Hidden) || ann.Flags.HasFlag(AnnotationFlags.NoView)) continue;
                        if (!TryReadPartialName(ann.AnnotationDictionary, out var nm)) continue;
                        var kind = ReadKind(ann.AnnotationDictionary);   // /FT: Tx->Text, Btn->Checkbox
                        if (kind is null) continue;                      // skip signature/unsupported
                        var r = ann.Rectangle;
                        widgets.Add((nm, kind, ToPx(r.Left, r.Right, r.Top, r.Bottom, pageH, scale)));
                    }
                }
                catch { }
                if (widgets.Count == 0) continue;

                // template's own text-layer tokens (the printed labels)
                var baseTokens = new List<TokenDto>();
                foreach (var w in page.GetWords())
                {
                    if (string.IsNullOrWhiteSpace(w.Text)) continue;
                    var bb = w.BoundingBox;
                    var px = ToPx(bb.Left, bb.Right, bb.Top, bb.Bottom, pageH, scale);
                    if (px[2] - px[0] <= 0 || px[3] - px[1] <= 0) continue;
                    baseTokens.Add(new TokenDto(w.Text, px));
                }

                var img = renderer.Render(bytes, pageNo, dpi);

                for (int v = 0; v < Math.Max(1, variants); v++)
                {
                    var fields = new List<FieldDto>();
                    int i = 0;
                    foreach (var (fn, kind, bbox) in widgets)
                    {
                        string value = kind == "Checkbox" ? (((v + i) % 2 == 0) ? "checked" : "unchecked")
                                                          : SynthValue(fn, v + i);
                        fields.Add(new FieldDto(fn, value, kind, "AcroForm", bbox));
                        i++;
                    }

                    // draw the synthetic values onto a copy of the rendered page
                    string safe = Sanitize(Path.GetFileNameWithoutExtension(name));
                    string imgRel = Path.Combine("images", $"{safe}__p{pageNo:D3}__v{v}.png");
                    DrawFilled(img, fields, Path.Combine(outDir, imgRel));

                    // tokens = template labels + synthetic value tokens (so the value text is present)
                    var tokens = new List<TokenDto>(baseTokens);
                    foreach (var f in fields)
                        if (f.kind == "Text" && f.value.Length > 0)
                            tokens.Add(new TokenDto(f.value, f.bbox));

                    var record = new
                    {
                        pdf = name, page = pageNo, width_px = img.Width, height_px = img.Height, dpi,
                        license, synthetic = true, variant = v,
                        image = imgRel.Replace('\\', '/'),
                        tokens, fields,
                    };
                    using (var w2 = new StreamWriter(Path.Combine(outDir, "form-kv.jsonl"), append: true))
                        w2.WriteLine(JsonSerializer.Serialize(record));
                    records++; fieldsTotal += fields.Count;
                }
            }
        }

        Console.WriteLine($"synthetic form-K-V (license={license}): {records} records, {fieldsTotal} fields → " +
                          $"{Path.Combine(outDir, "form-kv.jsonl")}");
        return records > 0;
    }

    // ---- synthetic value generation (keyword-driven, with small pools for variant diversity) ----
    private static readonly string[] Names = { "Jane A. Smith", "Robert Lee", "Maria Gomez", "D. Patel" };
    private static readonly string[] Dates = { "03/14/2026", "11/02/2025", "07/21/2024", "01/09/2026" };
    private static readonly string[] Cities = { "Washington", "Austin", "Albany", "Reno" };
    private static readonly string[] Codes = { "541512", "334111", "236220", "517311" };
    private static readonly string[] Amounts = { "$12,500.00", "$3,200.50", "$98,750.00", "$640.00" };

    private static string SynthValue(string field, int seed)
    {
        string n = field.ToLowerInvariant();
        string Pick(string[] p) => p[Math.Abs(seed) % p.Length];
        if (n.Contains("date")) return Pick(Dates);
        if (n.Contains("phone") || n.Contains("fax") || n.Contains("tel")) return "(202) 555-0143";
        if (n.Contains("email") || n.Contains("mail")) return "[email protected]";
        if (n.Contains("zip") || n.Contains("postal")) return "20500";
        if (n.Contains("state")) return "DC";
        if (n.Contains("city")) return Pick(Cities);
        if (n.Contains("address") || n.Contains("street")) return "1800 F Street NW";
        if (n.Contains("name")) return Pick(Names);
        if (n.Contains("amount") || n.Contains("price") || n.Contains("cost") || n.Contains("total") || n.Contains("fee"))
            return Pick(Amounts);
        if (n.Contains("naics") || n.Contains("code") || n.Contains("number") || n.Contains("no") || n.Contains("id"))
            return Pick(Codes);
        return "Sample Value";
    }

    // ---- drawing ----
    private static void DrawFilled(PageImage img, IReadOnlyList<FieldDto> fields, string outPath)
    {
        var info = new SKImageInfo(img.Width, img.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var bmp = new SKBitmap(info);
        Marshal.Copy(img.PixelsBgra8888, 0, bmp.GetPixels(), img.PixelsBgra8888.Length);
        using (var canvas = new SKCanvas(bmp))
        using (var font = new SKFont(SKTypeface.Default))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        {
            foreach (var f in fields)
            {
                float x1 = f.bbox[0], y1 = f.bbox[1], x2 = f.bbox[2], y2 = f.bbox[3];
                float h = Math.Max(1f, y2 - y1);
                if (f.kind == "Checkbox")
                {
                    if (f.value == "checked")
                    {
                        font.Size = h * 0.9f;
                        canvas.DrawText("X", x1 + h * 0.1f, y2 - h * 0.15f, SKTextAlign.Left, font, paint);
                    }
                    continue;
                }
                font.Size = Math.Clamp(h * 0.6f, 8f, 48f);
                canvas.Save();
                canvas.ClipRect(new SKRect(x1, y1, x2, y2));      // keep the value inside its field
                canvas.DrawText(f.value, x1 + 2f, y2 - h * 0.25f, SKTextAlign.Left, font, paint);
                canvas.Restore();
            }
        }
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(outPath, data.ToArray());
    }

    // ---- shared geometry/token helpers (same transform as PdfTextLayerReader / FormKvEmitter) ----
    private static float[] ToPx(double left, double right, double top, double bottom, float pageH, float scale)
    {
        float xA = (float)left * scale, xB = (float)right * scale;
        float yA = (pageH - (float)top) * scale, yB = (pageH - (float)bottom) * scale;
        return new[] { Math.Min(xA, xB), Math.Min(yA, yB), Math.Max(xA, xB), Math.Max(yA, yB) };
    }

    private static bool TryReadPartialName(DictionaryToken dict, out string name)
    {
        name = string.Empty;
        if (dict.TryGet(NameToken.Create("T"), out StringToken s)) { name = s.Data; return true; }
        if (dict.TryGet(NameToken.Create("Parent"), out DictionaryToken p)
            && p.TryGet(NameToken.Create("T"), out StringToken ps)) { name = ps.Data; return true; }
        return false;
    }

    // Field type from /FT (on the widget, or inherited from /Parent). Tx->Text, Btn->Checkbox,
    // Ch(choice)->Text, Sig->null (skip). Default Text keeps yield high on widgets with no direct /FT.
    private static string? ReadKind(DictionaryToken d)
    {
        if (d.TryGet(NameToken.Create("FT"), out NameToken ft)) return MapFt(ft.Data);
        if (d.TryGet(NameToken.Create("Parent"), out DictionaryToken p)
            && p.TryGet(NameToken.Create("FT"), out NameToken pft)) return MapFt(pft.Data);
        return "Text";
    }

    private static string? MapFt(string ft) => ft switch
    {
        "Tx" => "Text", "Btn" => "Checkbox", "Ch" => "Text", "Sig" => null, _ => "Text",
    };

    private static string Sanitize(string s) =>
        new string(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
}
