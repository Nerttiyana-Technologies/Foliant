// LiLT form-K-V Gate-3 eval — BORN-DIGITAL quick read (#10). Scores the model's VALUE-word predictions
// against the AcroForm widget /V regions on filled forms. Pure PdfPig + LiLT, no rendering.
//
// IMPORTANT framing: a form that HAS /V is handled exactly by AcroFormFieldExtractor in production, so
// this read is a sanity check on the model's value-vs-label discrimination, NOT LiLT's real niche. The
// flattened/OCR eval (render filled forms, OCR, run LiLT on OCR words) is the true Gate-3 decision.

using Foliant;
using Foliant.Forms.Lilt;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace Foliant.Verification;

internal static class FormKvEvalRunner
{
    public static async Task<bool> RunAsync(string pdfDir, string liltModelDir)
    {
        var pdfs = Directory.GetFiles(pdfDir, "*.pdf").OrderBy(p => p).ToList();
        if (pdfs.Count == 0) { Console.Error.WriteLine($"No PDFs in {pdfDir}."); return false; }
        if (!File.Exists(Path.Combine(liltModelDir, "model.onnx")))
        {
            Console.Error.WriteLine($"LiLT model.onnx not found under {liltModelDir}.");
            return false;
        }

        using var model = new LiltFormKvModel(liltModelDir);

        long tp = 0, fp = 0, fn = 0;
        int forms = 0, scoredPages = 0;
        var offenders = new List<(string Name, int Fp, int TrueValues)>();

        foreach (var pdf in pdfs)
        {
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(pdf); } catch { continue; }
            string name = Path.GetFileName(pdf);

            int formFp = 0, formTrue = 0;
            try
            {
                using var doc = PdfDocument.Open(bytes);
                for (int pageNo = 1; pageNo <= doc.NumberOfPages; pageNo++)
                {
                    var page = doc.GetPage(pageNo);
                    float pageH = (float)page.Height, pageW = (float)page.Width;

                    var words = new List<string>();
                    var boxes = new List<BoundingBox>();
                    var valueRects = new List<BoundingBox>();

                    // Ground-truth filled VALUES: each widget's /V, positioned at its rect. Added FIRST so
                    // they sit inside the 512-token budget (never truncated away).
                    foreach (var ann in page.GetAnnotations())
                    {
                        if (ann.Type != AnnotationType.Widget) continue;
                        if (ann.Flags.HasFlag(AnnotationFlags.Hidden) || ann.Flags.HasFlag(AnnotationFlags.NoView)) continue;
                        var d = ann.AnnotationDictionary;
                        string? val = null;
                        if (d.TryGet(NameToken.Create("V"), out StringToken sv) && !string.IsNullOrWhiteSpace(sv.Data))
                            val = sv.Data;
                        else if (d.TryGet(NameToken.Create("Parent"), out DictionaryToken p)
                                 && p.TryGet(NameToken.Create("V"), out StringToken pv) && !string.IsNullOrWhiteSpace(pv.Data))
                            val = pv.Data;
                        if (val is null) continue;

                        var box = ToTopLeft(ann.Rectangle, pageH);
                        if (box.Width <= 0 || box.Height <= 0) continue;
                        valueRects.Add(box);
                        words.Add(val.Trim());
                        boxes.Add(box);
                    }
                    if (valueRects.Count == 0) continue;

                    // Static content words (the printed labels) — the distractors the model must NOT flag.
                    foreach (var w in page.GetWords())
                    {
                        if (string.IsNullOrWhiteSpace(w.Text)) continue;
                        var box = ToTopLeft(w.BoundingBox, pageH);
                        if (box.Width <= 0 || box.Height <= 0) continue;
                        words.Add(w.Text);
                        boxes.Add(box);
                    }

                    // Ground truth: a word is VALUE iff its center sits in a value widget rect (training rule).
                    var gt = new bool[words.Count];
                    for (int i = 0; i < words.Count; i++)
                        gt[i] = valueRects.Any(r => r.ContainsCenterOf(boxes[i]));

                    var predicted = model
                        .PredictValueWords(words, boxes, (int)MathF.Ceiling(pageW), (int)MathF.Ceiling(pageH))
                        .ToHashSet();

                    for (int i = 0; i < words.Count; i++)
                    {
                        bool pred = predicted.Contains(i);
                        if (pred && gt[i]) tp++;
                        else if (pred && !gt[i]) { fp++; formFp++; }
                        else if (!pred && gt[i]) fn++;
                    }
                    formTrue += valueRects.Count;
                    scoredPages++;
                }
            }
            catch (Exception ex) { Console.WriteLine($"  ERROR {name}: {ex.Message}"); continue; }

            forms++;
            if (formFp > 0) offenders.Add((name, formFp, formTrue));
        }

        double precision = tp + fp > 0 ? (double)tp / (tp + fp) : 0;
        double recall = tp + fn > 0 ? (double)tp / (tp + fn) : 0;
        double f1 = precision + recall > 0 ? 2 * precision * recall / (precision + recall) : 0;

        Console.WriteLine("\n════ LiLT form-K-V eval — born-digital quick read ════");
        Console.WriteLine($"forms scored: {forms}   pages: {scoredPages}");
        Console.WriteLine($"true value words: {tp + fn}   predicted value words: {tp + fp}");
        Console.WriteLine($"TP {tp}   FP {fp} (fabrication)   FN {fn}");
        Console.WriteLine($"precision: {precision:P1}   recall: {recall:P1}   F1: {f1:P1}");
        Console.WriteLine($"fabrication rate (1 - precision): {1 - precision:P1}");
        if (offenders.Count > 0)
        {
            Console.WriteLine("\ntop fabrication offenders:");
            foreach (var o in offenders.OrderByDescending(x => x.Fp).Take(10))
                Console.WriteLine($"  {o.Name}: {o.Fp} false values (of {o.TrueValues} true)");
        }
        Console.WriteLine("\nNOTE: sanity read only. /V-bearing forms are handled exactly by AcroFormFieldExtractor " +
                          "in production; the flattened/OCR eval is the real Gate-3 decision.");
        return true;   // informational — the numbers drive the decision, not a hard pass/fail
    }

    private static BoundingBox ToTopLeft(PdfRectangle r, float pageH)
    {
        float x1 = (float)r.Left, x2 = (float)r.Right;
        float y1 = pageH - (float)r.Top, y2 = pageH - (float)r.Bottom;
        return new BoundingBox(MathF.Min(x1, x2), MathF.Min(y1, y2), MathF.Max(x1, x2), MathF.Max(y1, y2));
    }
}
