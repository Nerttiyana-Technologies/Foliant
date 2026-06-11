// Debug inspection for one page: writes a layout overlay PNG (composed regions over the
// rendered page), a JSON dump of regions + text lines with bounds, and the Markdown.
// Output goes to the (gitignored) harness output directory.

using System.Text.Json;
using Foliant;
using Foliant.Layout.DocLayoutNet;
using Foliant.Pipeline;

namespace Foliant.Verification;

internal static class Inspector
{
    public static async Task RunAsync(
        DocumentProcessor processor, string pdfDir, string pdfName, int pageNo,
        ProcessingOptions options, string outDir)
    {
        string path = Path.Combine(pdfDir, pdfName);
        if (!File.Exists(path)) { Console.Error.WriteLine($"not found: {path}"); return; }

        var pdf = await File.ReadAllBytesAsync(path);
        var doc = await processor.ProcessAsync(pdf, options with { Pages = new[] { pageNo } });
        if (doc.Pages.Count == 0) { Console.Error.WriteLine("page not processable"); return; }
        var page = doc.Pages[0];

        string stem = Path.Combine(outDir,
            $"inspect_{Path.GetFileNameWithoutExtension(pdfName)}_p{pageNo:D3}");

        // Overlay: composed output regions (incl. a synthetic box per furniture line)
        var image = new PdfPageRenderer().Render(pdf, pageNo, options.Dpi);
        var overlayRegions = page.Regions
            .Select(r => new LayoutRegion(r.Type, r.RawLabel, r.Confidence, r.Bounds))
            .Concat(page.PageFurniture.Select(l =>
                new LayoutRegion(RegionType.PageFurniture, "furniture-line", 1f, l.Bounds)))
            .ToList();
        DocLayoutNetDetector.DrawOverlay(image, overlayRegions, stem + "_overlay.png");

        // Geometry dump
        var dump = new
        {
            page.PageNumber,
            page.WidthPx,
            page.HeightPx,
            Source = page.Source.ToString(),
            Regions = page.Regions.Select(r => new
            {
                Type = r.Type.ToString(), r.RawLabel, r.Confidence,
                r.Bounds.X1, r.Bounds.Y1, r.Bounds.X2, r.Bounds.Y2,
                HasTable = r.Table != null,
                TableRows = r.Table?.RowCount,
                TableCols = r.Table?.ColumnCount,
                Text = r.Text.Length > 300 ? r.Text[..300] + "…" : r.Text,
            }),
            Lines = page.Lines.Select(l => new
            {
                l.Text, l.Source,
                l.Bounds.X1, l.Bounds.Y1, l.Bounds.X2, l.Bounds.Y2,
            }),
            Furniture = page.PageFurniture.Select(l => new { l.Text, l.Bounds.Y1 }),
        };
        await File.WriteAllTextAsync(stem + ".json",
            JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true }));
        await File.WriteAllTextAsync(stem + ".md", page.Markdown);

        Console.WriteLine($"→ {stem}_overlay.png");
        Console.WriteLine($"→ {stem}.json");
        Console.WriteLine($"→ {stem}.md");
    }
}
