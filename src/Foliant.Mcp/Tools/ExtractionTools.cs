using System.ComponentModel;
using Foliant.Mcp.Extraction;
using Foliant.Mcp.Shaping;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Foliant.Mcp.Tools;

/// <summary>Run-ticket extraction (ADR-0005 D6/D7) + synchronous summary for small documents.</summary>
[McpServerToolType]
public static class ExtractionTools
{
    [McpServerTool(Name = "start_extraction"),
     Description(
        "Start extracting a PDF into layout-aware Markdown with Foliant (fully local — layout " +
        "detection, OCR where needed, tables, reading order, per-page self-verification). Returns a " +
        "runId immediately; the work runs in the background. Poll get_extraction_status, then read " +
        "pages with get_extraction_result. Use this for documents larger than ~10 pages; small " +
        "documents can use extract_summary instead. The FIRST extraction after server start loads " +
        "the ONNX models (seconds when Foliant__ModelsDir is staged; otherwise a one-time ~330 MB " +
        "verified download).")]
    public static string StartExtraction(
        ProcessorHolder holder,
        ExtractionRunRegistry registry,
        IOptions<FoliantMcpOptions> options,
        [Description("Absolute path to the PDF on the machine running this server.")] string path,
        [Description("Also extract typed key-value form fields per page. Default true.")]
        bool extractFormFields = true,
        [Description("Append a generated hardware-specification section (server/desktop/laptop/" +
                     "workstation/component specs — CPU, memory, storage, GPU, form factor, quantity, " +
                     "part number) at the bottom of the document. For federal solicitations that procure " +
                     "IT hardware. Additive; a document with no hardware appends nothing. Default false.")]
        bool extractHardwareSpecs = false,
        [Description("Process at most this many pages; 0 uses the server's configured cap.")]
        int maxPages = 0)
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

        // The guardrail lives in code, not in the prompt (ADR-0005 D6).
        int cap = Math.Max(1, options.Value.MaxPages);
        int effective = maxPages > 0 ? Math.Min(Math.Max(1, maxPages), cap) : cap;
        int pagesToProcess = Math.Min(totalPages, effective);
        IReadOnlyCollection<int>? pageSubset = totalPages > effective
            ? Enumerable.Range(1, effective).ToArray()
            : null;

        var run = registry.Create(path, pagesToProcess);

        _ = Task.Run(async () =>
        {
            await registry.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                run.MarkRunning();
                var engine = await holder.GetAsync().ConfigureAwait(false);
                var processingOptions = new ProcessingOptions
                {
                    ExtractFormFields = extractFormFields,
                    ExtractHardwareSpecs = extractHardwareSpecs,
                    Pages = pageSubset,
                    Progress = new SyncProgress<ProcessingProgress>(
                        p => run.ReportPagesDone(p.CompletedPages)),
                };
                var result = await engine.ProcessAsync(pdf, processingOptions).ConfigureAwait(false);
                run.MarkCompleted(result);
            }
            catch (Exception ex)
            {
                run.MarkFailed(ex.Message);
            }
            finally
            {
                registry.Gate.Release();
            }
        });

        return Shape.ToJson(new
        {
            runId = run.Id,
            status = "pending",
            totalPagesInDocument = totalPages,
            pagesToProcess,
            pagesCappedByServer = totalPages > pagesToProcess,
            next = "Poll get_extraction_status with this runId; when completed, page through " +
                   "get_extraction_result.",
        });
    }

    [McpServerTool(Name = "get_extraction_status"),
     Description("Status of an extraction run started by start_extraction: pending/running/" +
                 "completed/failed, with per-page progress.")]
    public static string GetExtractionStatus(
        ExtractionRunRegistry registry,
        [Description("The runId returned by start_extraction.")] string runId)
    {
        var run = registry.Get(runId);
        if (run is null)
            return Shape.Error($"Unknown runId '{runId}'. Runs live only for this server session.");

        return Shape.ToJson(new
        {
            runId = run.Id,
            status = run.Status,
            pageCount = run.PageCount,
            pagesDone = run.PagesDone,
            error = run.Error,
            elapsedSeconds = Math.Round(
                ((run.CompletedAtUtc ?? DateTimeOffset.UtcNow) - run.CreatedAtUtc).TotalSeconds, 1),
        });
    }

    [McpServerTool(Name = "get_extraction_result"),
     Description(
        "Read a completed extraction run page-by-page. Returns a window of at most 20 pages per " +
        "call (per-page Markdown, source, recall, needs-review and sensitivity flags) plus " +
        "totalPages — never the whole document at once. Ask for further windows as needed. When " +
        "you only need verification status (which pages need review, sensitivity markings, recall) " +
        "and not the text itself, set includeContent=false for a much smaller, faster reply.")]
    public static string GetExtractionResult(
        ExtractionRunRegistry registry,
        IOptions<PrivacyOptions> privacy,
        [Description("The runId returned by start_extraction.")] string runId,
        [Description("First 1-based page of the window. Default 1.")] int fromPage = 1,
        [Description("Pages in the window. Default 5, hard cap 20.")] int pageCount = 5,
        [Description("False omits the per-page Markdown — returns only verification metadata " +
                     "(recall, needs-review, sensitivity). Use when the question is about " +
                     "extraction quality, not document content. Default true.")]
        bool includeContent = true)
    {
        var run = registry.Get(runId);
        if (run is null)
            return Shape.Error($"Unknown runId '{runId}'. Runs live only for this server session.");
        if (run.Status == ExtractionRunStatus.Failed)
            return Shape.Error($"Run {runId} failed: {run.Error}");
        if (run.Status != ExtractionRunStatus.Completed || run.Result is null)
            return Shape.Error(
                $"Run {runId} is {run.Status.ToString().ToLowerInvariant()} " +
                $"({run.PagesDone}/{run.PageCount} pages). Poll get_extraction_status until completed.");

        return Shape.ToJson(Shape.BuildWindow(
            run.Result, fromPage, pageCount, privacy.Value.BlockSensitivePages, includeContent));
    }

    [McpServerTool(Name = "extract_summary"),
     Description(
        "Synchronously extract a SMALL PDF (at or under the server's sync page limit, default 10 " +
        "pages) and return the whole document's layout-aware Markdown plus a verification summary " +
        "(word recall, pages needing review, sensitivity-marked pages). For larger documents this " +
        "tool refuses and tells you to use start_extraction. When you only need verification " +
        "status and not the text itself, set includeContent=false for a much smaller, faster " +
        "reply. The first call after server start loads the ONNX models.")]
    public static async Task<string> ExtractSummary(
        ProcessorHolder holder,
        ExtractionRunRegistry registry,
        IOptions<FoliantMcpOptions> options,
        IOptions<PrivacyOptions> privacy,
        [Description("Absolute path to the PDF on the machine running this server.")] string path,
        [Description("False omits the document Markdown — returns only verification metadata " +
                     "(page count, recall, needs-review, sensitivity). Use when the question is " +
                     "about extraction quality, not document content. Default true.")]
        bool includeContent = true,
        [Description("Append a generated hardware-specification section (server/desktop/laptop/" +
                     "workstation/component specs) at the bottom of the document. For federal " +
                     "solicitations that procure IT hardware. Additive; no hardware ⇒ appends nothing. " +
                     "Default false.")]
        bool extractHardwareSpecs = false,
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

        int limit = options.Value.SummarySyncPageLimit;
        if (totalPages > limit)
            return Shape.Error(
                $"Document has {totalPages} pages — over the synchronous limit of {limit}. " +
                "Use start_extraction (run-ticket) instead.");

        await registry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var engine = await holder.GetAsync(cancellationToken).ConfigureAwait(false);
            var result = await engine.ProcessAsync(
                    pdf,
                    new ProcessingOptions
                    {
                        ExtractFormFields = true,
                        ExtractHardwareSpecs = extractHardwareSpecs,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            return Shape.ToJson(
                Shape.BuildSummary(result, privacy.Value.BlockSensitivePages, includeContent));
        }
        finally
        {
            registry.Gate.Release();
        }
    }
}

/// <summary>Synchronous IProgress — no SynchronizationContext marshalling, so counters update
/// immediately from the processing thread (same pattern as FoliantView's ProgressForwarder).</summary>
internal sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _action;
    public SyncProgress(Action<T> action) => _action = action;
    public void Report(T value) => _action(value);
}
