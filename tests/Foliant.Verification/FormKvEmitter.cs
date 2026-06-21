// Opt-in local harvester (ADR-0001, Lever 2): turns a verification pass into a license-clean
// form key-value training set for the LiLT K-V head. For each page that carries a fillable
// AcroForm, emits one JSONL record { page image + text-layer tokens-with-boxes + AcroForm
// fields (name/value/kind + widget rect) }. Labels come from the PDF's OWN AcroForm dictionary —
// no annotation, no third-party (non-commercial) model.
//
// Invariants (binding, ADR-0001 governance): writes to the LOCAL filesystem only, makes NO
// network call, emits NO telemetry, and is active ONLY when --emit-form-kv is passed. Every
// record carries a `license` provenance tag (from --kv-license); per the data-strategy decision
// (2026-06-17) ONLY records tagged `public-domain` may ever feed the SHIPPABLE base model — see
// datasets/form-kv/README.md for the allowlist. Heightened sensitivity: records include the page
// IMAGE (more raw content than the reading-order snippets), so the output stays gitignored and
// local-only, exactly like the reading-order harvest.
//
// Coordinate convention matches the rest of the pipeline (PdfTextLayerReader): origin top-left,
// Y down, pixels at `dpi`. The PDF→raster transform here is the SAME formula the text-layer
// reader uses, so token/field boxes align with the rendered page image.

using System.Runtime.InteropServices;
using System.Text.Json;
using Foliant;
using Foliant.Layout.DocLayoutNet;
using Foliant.Pipeline;
using SkiaSharp;
using UglyToad.PdfPig;
using UglyToad.PdfPig.AcroForms.Fields;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Tokens;

namespace Foliant.Verification;

internal static class FormKvEmitter
{
    private sealed record TokenDto(string text, float[] bbox);
    private sealed record FieldDto(string name, string value, string kind, string source, float[]? bbox);

    /// <summary>Appends one JSONL form-K-V record for the page (and writes its page PNG), or does
    /// nothing when the page carries no AcroForm fields. Returns true if a record was written.</summary>
    public static bool Append(
        string outDir, string license, byte[] pdfBytes, string pdfName, int pageNumber, int dpi,
        bool overlay = false)
    {
        using var doc = PdfDocument.Open(pdfBytes);
        if (!doc.TryGetForm(out var form)) return false;
        if (pageNumber < 1 || pageNumber > doc.NumberOfPages) return false;

        var page = doc.GetPage(pageNumber);
        float scale = dpi / 72f;
        float pageH = (float)page.Height;

        // 1) AcroForm fields → name/value/kind (proven path; mirrors AcroFormFieldExtractor).
        var fields = new List<(string Name, string Value, string Kind)>();
        foreach (var field in form.GetFieldsForPage(pageNumber))
        {
            string name = field.Information.PartialName ?? string.Empty;
            switch (field)
            {
                case AcroTextField text:
                    // Emit empty fields too (value ""): a blank field still teaches key/field
                    // localization, which makes blank public-domain templates useful on their own.
                    // Downstream, filter `value != ""` for the value-extraction task.
                    fields.Add((name, text.Value ?? string.Empty, "Text"));
                    break;
                case AcroCheckboxField box:
                    fields.Add((name, box.IsChecked ? "checked" : "unchecked", "Checkbox"));
                    break;
                // choice lists / radio groups / push buttons out of scope (same as the deterministic extractor)
            }
        }
        if (fields.Count == 0) return false;   // not a fillable-form page → nothing to harvest

        // 2) Widget rectangles by field name, from the page's Widget annotations.
        //    NOTE (verify on first compile): PdfPig 0.1.14 annotation accessor. If `GetAnnotations()`
        //    does not resolve, use `page.ExperimentalAccess.GetAnnotations()`. We read /T directly off
        //    the widget dict (the common single-widget-per-field case); fields whose /T lives only on
        //    a /Parent simply get a null bbox rather than a wrong one.
        var rects = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var hidden = new HashSet<string>(StringComparer.Ordinal);   // form-logic controls to drop
        try
        {
            foreach (var ann in page.GetAnnotations())
            {
                if (ann.Type != AnnotationType.Widget) continue;
                if (!TryReadPartialName(ann.AnnotationDictionary, out var name)) continue;
                // Skip hidden / non-viewable widgets (e.g. page_exists_* logic checkboxes): they
                // carry rects but no human-readable content and would be training noise.
                if (ann.Flags.HasFlag(AnnotationFlags.Hidden) || ann.Flags.HasFlag(AnnotationFlags.NoView))
                {
                    hidden.Add(name);
                    continue;
                }
                var r = ann.Rectangle;
                rects[name] = ToPx(r.Left, r.Right, r.Top, r.Bottom, pageH, scale);
            }
        }
        catch { /* annotation read is best-effort; records still emit with null bbox */ }
        hidden.ExceptWith(rects.Keys);   // a field with any visible widget is kept

        var fieldDtos = fields
            .Where(f => !hidden.Contains(f.Name))
            .Select(f => new FieldDto(
                f.Name, f.Value, f.Kind, "AcroForm",
                rects.TryGetValue(f.Name, out var r) ? r : null))
            .ToList();
        if (fieldDtos.Count == 0) return false;   // only hidden/control fields → nothing to harvest

        // 3) Text-layer tokens with boxes (LiLT layout input). Empty on scanned forms with no text layer.
        var tokens = new List<TokenDto>();
        foreach (var w in page.GetWords())
        {
            if (string.IsNullOrWhiteSpace(w.Text)) continue;
            var bb = w.BoundingBox;
            var px = ToPx(bb.Left, bb.Right, bb.Top, bb.Bottom, pageH, scale);
            if (px[2] - px[0] <= 0 || px[3] - px[1] <= 0) continue;   // drop degenerate glyph boxes
            tokens.Add(new TokenDto(w.Text, px));
        }

        // 4) Page image — re-render at the SAME dpi so boxes align, encode PNG via SkiaSharp.
        var img = new PdfPageRenderer().Render(pdfBytes, pageNumber, dpi);
        string safe = Sanitize(Path.GetFileNameWithoutExtension(pdfName));
        string imgRel = Path.Combine("images", $"{safe}__p{pageNumber:D3}.png");
        string imgAbs = Path.Combine(outDir, imgRel);
        Directory.CreateDirectory(Path.GetDirectoryName(imgAbs)!);
        WritePng(img, imgAbs);

        var record = new
        {
            pdf = pdfName,
            page = pageNumber,
            width_px = img.Width,
            height_px = img.Height,
            dpi,
            license,
            image = imgRel.Replace('\\', '/'),
            tokens,
            fields = fieldDtos,
        };

        Directory.CreateDirectory(outDir);
        using (var w2 = new StreamWriter(Path.Combine(outDir, "form-kv.jsonl"), append: true))
            w2.WriteLine(JsonSerializer.Serialize(record));

        // Verification overlay (--form-kv-overlay): draw the field widget rects on the page so the
        // PdfPig->raster transform can be eyeballed. Reuses the harness's SkiaSharp DrawOverlay.
        if (overlay)
        {
            var boxes = fieldDtos.Where(f => f.bbox is not null)
                .Select(f => new LayoutRegion(
                    RegionType.Table, f.name, 1f,
                    new BoundingBox(f.bbox![0], f.bbox[1], f.bbox[2], f.bbox[3])))
                .ToList();
            if (boxes.Count > 0)
            {
                string ovAbs = Path.Combine(outDir, "overlays", $"{safe}__p{pageNumber:D3}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(ovAbs)!);
                DocLayoutNetDetector.DrawOverlay(img, boxes, ovAbs);
            }
        }
        return true;
    }

    // PdfPig: origin bottom-left, Y up, points. Raster: origin top-left, Y down, pixels at dpi.
    // Min/Max guards rotated rectangles. Identical to PdfTextLayerReader's mapping.
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
        return false;
    }

    private static void WritePng(PageImage img, string path)
    {
        var info = new SKImageInfo(img.Width, img.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var bmp = new SKBitmap(info);
        Marshal.Copy(img.PixelsBgra8888, 0, bmp.GetPixels(), img.PixelsBgra8888.Length);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static string Sanitize(string s) =>
        new string(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
}
