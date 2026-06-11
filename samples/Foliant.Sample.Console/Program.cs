// Minimal Foliant usage: PDF in → Markdown + structured JSON out.
//
//   dotnet run --project samples/Foliant.Sample.Console -- <pdf-path> [--models <dir>]
//
// With no --models argument, models are downloaded once into the local cache
// (~/.local/share/Foliant/models or %LocalAppData%\Foliant\models) and reused.

using Foliant;
using Foliant.Pipeline;

string? pdfPath = null;
string? modelsDir = null;

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--models" && i + 1 < args.Length) { modelsDir = args[++i]; continue; }
    pdfPath ??= args[i];
}

if (pdfPath == null || !File.Exists(pdfPath))
{
    Console.Error.WriteLine("Usage: Foliant.Sample.Console <pdf-path> [--models <dir>]");
    return 1;
}

using var processor = modelsDir != null
    ? FoliantProcessor.CreateDefault(modelsDir)
    : await FoliantProcessor.CreateDefaultAsync(
        downloadProgress: new Progress<(string Id, double Fraction)>(
            p => Console.Error.Write($"\rdownloading {p.Id}: {p.Fraction:P0}   ")));

var result = await processor.ProcessAsync(await File.ReadAllBytesAsync(pdfPath));

// Extraction output goes into a gitignored folder — derived artifacts are never committed.
string outDir = "sample-out";
Directory.CreateDirectory(outDir);
string stem = Path.Combine(outDir, Path.GetFileNameWithoutExtension(pdfPath));
await File.WriteAllTextAsync($"{stem}.md", result.Markdown);
await File.WriteAllTextAsync($"{stem}.json", result.ToJson(indented: true));

Console.WriteLine($"\n{result.Pages.Count} pages processed.");
foreach (var page in result.Pages)
{
    var v = page.Verification;
    Console.WriteLine(
        $"  p{page.PageNumber:D3}: {page.Regions.Count} regions, source={page.Source}, " +
        $"recall={(v.RecallPercent is { } r ? $"{r:0.0}%" : "n/a")}, {v.Seconds:0.0}s");
}
Console.WriteLine($"\n→ {stem}.md");
Console.WriteLine($"→ {stem}.json");
return 0;
