using System.ComponentModel;
using Foliant.Mcp.Extraction;
using Foliant.Mcp.Shaping;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Foliant.Mcp.Tools;

/// <summary>Typed form-field extraction + federal-form template matching (ADR-0005 D6).</summary>
[McpServerToolType]
public static class FormTools
{
    [McpServerTool(Name = "get_form_fields"),
     Description(
        "Extract typed key-value form fields from a PDF — exact values from the fillable AcroForm " +
        "dictionary when present, template-bound values for recognized federal Standard Forms, and " +
        "geometric/learned association on other scanned forms. Each field carries name, value, kind " +
        "(text/checkbox), confidence, source, and a possiblyTruncated honesty flag. Pass a page " +
        "number to process just that page (much faster on big documents). Loads the ONNX models on " +
        "first use.")]
    public static async Task<string> GetFormFields(
        ProcessorHolder holder,
        ExtractionRunRegistry registry,
        IOptions<FoliantMcpOptions> options,
        [Description("Absolute path to the PDF on the machine running this server.")] string path,
        [Description("1-based page to process. Omit to process the whole document (subject to the " +
                     "server page cap).")]
        int? page = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return Shape.Error($"File not found: {path}");

        byte[] pdf;
        int totalPages;
        try
        {
            pdf = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            totalPages = Pdf.GetPageCount(pdf);
        }
        catch (Exception ex)
        {
            return Shape.Error($"Could not read PDF '{path}': {ex.Message}");
        }

        if (page is int p && (p < 1 || p > totalPages))
            return Shape.Error($"Page {p} is out of range (document has {totalPages} pages).");

        int cap = Math.Max(1, options.Value.MaxPages);
        IReadOnlyCollection<int>? pages = page.HasValue
            ? new[] { page.Value }
            : totalPages > cap ? Enumerable.Range(1, cap).ToArray() : null;

        await registry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        DocumentResult result;
        try
        {
            var engine = await holder.GetAsync(cancellationToken).ConfigureAwait(false);
            result = await engine.ProcessAsync(
                    pdf,
                    new ProcessingOptions { ExtractFormFields = true, Pages = pages },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            registry.Gate.Release();
        }

        var all = result.Pages
            .OrderBy(pg => pg.PageNumber)
            .SelectMany(pg => (pg.FormFields ?? Array.Empty<FormField>())
                .Select(f => new FormFieldDto(
                    pg.PageNumber,
                    f.Name,
                    f.Value,
                    f.Kind.ToString(),
                    Math.Round(f.Confidence, 2),
                    f.Source.ToString(),
                    f.PossiblyTruncated)))
            .ToList();

        return Shape.ToJson(new
        {
            totalPagesInDocument = totalPages,
            pagesProcessed = result.Pages.Count,
            totalFields = all.Count,
            returned = Math.Min(all.Count, Shape.MaxFormFields),
            fields = all.Take(Shape.MaxFormFields).ToList(),
        });
    }

    [McpServerTool(Name = "match_template"),
     Description(
        "Check which pages of a PDF match a known form template (12 bundled federal Standard Forms " +
        "plus any customer-registered templates). Model-free and fast — no ONNX load. Returns per-" +
        "page template id, match score, and deterministic field count; unmatched pages are simply " +
        "absent. Useful to decide whether get_form_fields will yield template-bound (high-trust) " +
        "values.")]
    public static string MatchTemplate(
        TemplateRouterHolder templates,
        [Description("Absolute path to the PDF on the machine running this server.")] string path,
        [Description("1-based page to check. Omit to scan from page 1.")] int? page = null,
        [Description("When scanning the whole document, check at most this many pages from page 1. " +
                     "Default 25, hard cap 100.")]
        int maxPagesToScan = 25)
    {
        if (!File.Exists(path))
            return Shape.Error($"File not found: {path}");

        byte[] pdf;
        int totalPages;
        try
        {
            pdf = File.ReadAllBytes(path);
            totalPages = Pdf.GetPageCount(pdf);
        }
        catch (Exception ex)
        {
            return Shape.Error($"Could not read PDF '{path}': {ex.Message}");
        }

        if (page is int p && (p < 1 || p > totalPages))
            return Shape.Error($"Page {p} is out of range (document has {totalPages} pages).");

        var router = templates.Get();
        var pagesToScan = page.HasValue
            ? new[] { page.Value }
            : Enumerable.Range(1, Math.Min(totalPages, Math.Clamp(maxPagesToScan, 1, 100))).ToArray();

        var matches = new List<object>();
        foreach (int pageNumber in pagesToScan)
        {
            var match = router.TryRoute(pdf, pageNumber);
            if (match is not null)
            {
                matches.Add(new
                {
                    page = pageNumber,
                    templateId = match.TemplateId,
                    score = Math.Round(match.Score, 3),
                    fieldCount = match.Fields.Count,
                });
            }
        }

        return Shape.ToJson(new
        {
            totalPagesInDocument = totalPages,
            pagesScanned = pagesToScan.Length,
            totalMatches = matches.Count,
            matches,
            templateMode = templates.Mode,
        });
    }
}
