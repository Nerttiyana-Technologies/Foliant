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

    // ---- synthetic value generation (keyword-driven, PROCEDURAL) ----
    // v1 used 4-entry fixed pools; the LiLT Gate-3 scanned-holdout run (2026-07-05) showed the
    // model memorized value TEXT instead of layout (repeated sentences everywhere, novel values
    // missed). v2 composes values from a seeded RNG over word/part pools: effectively unbounded
    // diversity, still deterministic per (field, seed) for reproducible corpora. All identifiers
    // are fabricated (555 phone exchange, 9xx SSN area, example.* emails); nothing real.

    private static readonly string[] FirstNames = { "Jane", "Robert", "Maria", "Devi", "Carlos", "Aisha", "Thomas", "Elena", "Marcus", "Priya", "Samuel", "Nadia", "George", "Linh", "Walter", "Rosa", "Henry", "Amara", "Frank", "Yuki", "Clara", "Omar", "Ruth", "Felix" };
    private static readonly string[] LastNames = { "Smith", "Lee", "Gomez", "Patel", "Okafor", "Novak", "Reyes", "Chen", "Dubois", "Larsen", "Moreau", "Kim", "Alvarez", "Brandt", "Costa", "Egan", "Fontaine", "Grieve", "Hoang", "Ibarra", "Jensen", "Kovacs", "Lindqvist", "Mbeki" };
    private static readonly string[] StreetNames = { "Cedar", "Franklin", "Meridian", "Willow", "Harbor", "Summit", "Prairie", "Juniper", "Colonial", "Granite", "Lakeview", "Sycamore", "Bramble", "Foxglove", "Hickory", "Palmetto" };
    private static readonly string[] StreetSuffixes = { "St", "Ave", "Blvd", "Dr", "Ln", "Way", "Ct", "Pkwy" };
    private static readonly (string City, string State, string Zip)[] Places = {
        ("Washington", "DC", "20500"), ("Austin", "TX", "78701"), ("Albany", "NY", "12207"),
        ("Reno", "NV", "89501"), ("Columbus", "OH", "43215"), ("Portland", "OR", "97204"),
        ("Raleigh", "NC", "27601"), ("Boise", "ID", "83702"), ("Madison", "WI", "53703"),
        ("Trenton", "NJ", "08608"), ("Helena", "MT", "59601"), ("Dover", "DE", "19901"),
        ("Mesa", "AZ", "85201"), ("Tulsa", "OK", "74103"), ("Erie", "PA", "16501"), ("Salem", "OR", "97301") };
    private static readonly string[] Titles = { "Contracting Officer", "Program Analyst", "HR Specialist", "Budget Officer", "Records Manager", "Payroll Supervisor", "Administrative Officer", "Benefits Counselor", "Personnel Clerk", "Division Chief" };
    private static readonly string[] RemarkSubjects = { "Applicant", "Employee", "Claimant", "The undersigned", "Requesting office", "Servicing agency" };
    private static readonly string[] RemarkVerbs = { "claims", "certifies", "submits", "requests", "confirms", "provides", "attaches", "disputes" };
    private static readonly string[] RemarkObjects = {
        "documentation supporting the application for benefits",
        "verification of prior federal civilian service",
        "a corrected service history for the period shown",
        "proof of military service for retirement credit",
        "an updated designation of beneficiary",
        "records of non-creditable time for review",
        "a certified copy of the appointment action",
        "supporting statements from the servicing payroll office" };
    private static readonly string[] RemarkTails = { "", "; see attached service history", "; originals to follow by mail", " for the period indicated", " as required by the instructions", "; no further action requested" };

    /// <summary>Stable FNV-1a — string.GetHashCode / HashCode.Combine are per-process randomized.</summary>
    private static int StableHash(string s)
    {
        unchecked
        {
            int h = (int)2166136261;
            foreach (char c in s) h = (h ^ c) * 16777619;
            return h;
        }
    }

    private static string SynthValue(string field, int seed)
    {
        string n = field.ToLowerInvariant();
        var rng = new Random(StableHash(field) ^ (seed * 486187739));
        string Pick(string[] p) => p[rng.Next(p.Length)];
        string Digits(int k) { var c = new char[k]; for (int i = 0; i < k; i++) c[i] = (char)('0' + rng.Next(10)); return new string(c); }

        if (n.Contains("ssn") || n.Contains("social security"))
            return $"9{rng.Next(10, 100)}-{rng.Next(10, 100)}-{Digits(4)}";
        if (n.Contains("date") || n.Contains("dob") || n.Contains("signed"))
        {
            int y = rng.Next(2018, 2027), mo = rng.Next(1, 13), d = rng.Next(1, 29);
            return rng.Next(4) == 0 ? $"{new DateTime(y, mo, d):MMMM d, yyyy}" : $"{mo:00}/{d:00}/{y}";
        }
        if (n.Contains("phone") || n.Contains("fax") || n.Contains("tel"))
            return $"({rng.Next(201, 990)}) 555-{Digits(4)}";
        if (n.Contains("email") || n.Contains("mail"))
            return $"{Pick(FirstNames).ToLowerInvariant()}.{Pick(LastNames).ToLowerInvariant()}@example.{Pick(new[] { "gov", "com", "org" })}";
        if (n.Contains("zip") || n.Contains("postal")) return Places[rng.Next(Places.Length)].Zip;
        if (n.Contains("state")) return Places[rng.Next(Places.Length)].State;
        if (n.Contains("city")) return Places[rng.Next(Places.Length)].City;
        if (n.Contains("address") || n.Contains("street"))
        {
            var p = Places[rng.Next(Places.Length)];
            string line1 = $"{rng.Next(100, 9900)} {Pick(StreetNames)} {Pick(StreetSuffixes)}";
            return rng.Next(3) == 0 ? $"{line1}, {p.City}, {p.State} {p.Zip}" : line1;
        }
        if (n.Contains("title")) return Pick(Titles);
        // Offeror/contractor cells are COMPANY + address in real award grids (SF1409/SF1449/SF1447),
        // not person names — must precede the generic "name" branch (NameOfOfferor contains "name").
        if (n.Contains("offeror") || n.Contains("contractor") || n.Contains("vendor") || n.Contains("bidder"))
        {
            var pl = Places[rng.Next(Places.Length)];
            string co = $"{Pick(StreetNames)} {Pick(new[] { "Industries", "Logistics", "Systems", "Supply Co", "Partners", "Solutions", "Rugged Devices LLC", "Federal LLC", "Group", "Technologies" })}";
            return $"{co} {pl.City}, {pl.State} {pl.Zip}";
        }
        if (n.Contains("name"))
        {
            string first = Pick(FirstNames), last = Pick(LastNames);
            return rng.Next(3) switch
            {
                0 => $"{first} {(char)('A' + rng.Next(26))}. {last}",
                1 => $"{last}, {first}",
                _ => $"{first} {last}",
            };
        }
        // Line-item grid columns carry TYPED values in the real holdout — teaching the model the
        // per-column type is what stops the qty<->unit<->cost mis-registration (value-stolen class).
        if (n.Contains("quantity") || n.Contains("qty"))
            return rng.Next(7) == 0 ? "0" : $"{rng.Next(1, 21) * 25}";              // small integers: 25..500
        if (n.Contains("fobpoint") || n.Contains("fob"))
            return Pick(new[] { "Origin", "Destination" });
        if (n.StartsWith("page") || n.Contains("pageof") || n.Contains("pages"))
            return $"{rng.Next(1, 6)}";                                             // page numbers: 1..5
        if (n.Contains("item"))                                                     // ItemNumber/ITEMNO/ITEMNUM -> CLIN-like
            return rng.Next(2) == 0 ? Digits(4) : $"{(char)('A' + rng.Next(26))}{(char)('A' + rng.Next(26))}-{Digits(4)}";
        // "unit" (unit total / extended cost) reads as MONEY in the SF1447/SF1449 holdout; "unitprice" also.
        if (n.Contains("amount") || n.Contains("price") || n.Contains("cost") || n.Contains("unit") || n.Contains("total")
            || n.Contains("fee") || n.Contains("salary") || n.Contains("balance") || n.Contains("due"))
        {
            double v = Math.Round(Math.Exp(rng.NextDouble() * 7.5 + 3.0), 2);   // ~$20 .. ~$36k, log-spread
            return rng.Next(6) == 0 ? "0" : $"${v:N2}";
        }
        if (n.Contains("naics")) return Digits(6);
        if (n.Contains("cage")) return $"{rng.Next(1, 10)}{(char)('A' + rng.Next(26))}{Digits(1)}{(char)('A' + rng.Next(26))}{Digits(1)}";
        if (n.Contains("code") || n.Contains("number") || n.Contains("no") || n.Contains("id"))
            return rng.Next(3) == 0
                ? $"{(char)('A' + rng.Next(26))}{(char)('A' + rng.Next(26))}-{Digits(rng.Next(4, 7))}"
                : Digits(rng.Next(5, 9));
        if (n.Contains("remark") || n.Contains("comment") || n.Contains("description") || n.Contains("explain"))
            return $"{Pick(RemarkSubjects)} {Pick(RemarkVerbs)} {Pick(RemarkObjects)}{Pick(RemarkTails)}.";
        // default: short plausible free text, never a fixed literal
        return $"{Pick(RemarkSubjects)} {Pick(RemarkVerbs)} {Pick(RemarkObjects)}.";
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
