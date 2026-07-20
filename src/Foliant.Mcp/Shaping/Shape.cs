using System.Text.Json;
using System.Text.Json.Serialization;

namespace Foliant.Mcp.Shaping;

// ── Return DTOs (typed so shaping is unit-testable) ─────────────────────────────────────────────

public sealed record PageDto(
    int PageNumber,
    string Source,
    string? Markdown,
    bool MarkdownTruncated,
    double? RecallPercent,
    bool NeedsReview,
    string? Notice,
    string? SensitivityMarking,
    bool LowResolution,
    int FormFieldCount);

public sealed record WindowDto(
    int TotalPages,
    int FromPage,
    int Returned,
    IReadOnlyList<PageDto> Pages,
    IReadOnlyList<int> PagesNeedingReview,
    IReadOnlyList<int> SensitivityMarkedPages,
    double? AverageRecallPercent);

public sealed record SummaryDto(
    int TotalPages,
    string? Markdown,
    bool MarkdownTruncated,
    double? AverageRecallPercent,
    IReadOnlyList<int> PagesNeedingReview,
    IReadOnlyList<int> SensitivityMarkedPages,
    double ProcessingSeconds);

public sealed record FormFieldDto(
    int PageNumber,
    string Name,
    string Value,
    string Kind,
    double Confidence,
    string Source,
    bool PossiblyTruncated);

// ── Shaping: caps, windowing, redaction (ADR-0005 D6/D9) ───────────────────────────────────────

public static class Shape
{
    public const int MaxWindowPages = 20;
    public const int MaxPageMarkdownChars = 20_000;
    public const int MaxSummaryMarkdownChars = 40_000;
    public const int MaxFormFields = 200;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Compact JSON — the model parses JSON reliably; whitespace is wasted tokens.</summary>
    public static string ToJson(object value) => JsonSerializer.Serialize(value, Json);

    public static string Error(string message) => ToJson(new { error = message });

    public static (string Text, bool Truncated) Cap(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return (text ?? string.Empty, false);
        return (text[..maxChars] + " …[truncated]", true);
    }

    public static string RedactionNotice(string marking) =>
        $"[content withheld: this page carries the sensitivity marking '{marking}' and " +
        "Privacy:BlockSensitivePages is enabled on this server]";

    /// <summary>The page's Markdown, or the redaction notice when the privacy gate applies (D9).</summary>
    public static string PageMarkdown(PageResult page, bool blockSensitivePages) =>
        blockSensitivePages && page.SensitivityMarking is not null
            ? RedactionNotice(page.SensitivityMarking)
            : page.Markdown ?? string.Empty;

    /// <summary>
    /// A page window over a completed result. Clamps are enforced here, in code — never by the
    /// prompt: fromPage into [1, totalPages], pageCount into [1, MaxWindowPages], per-page Markdown
    /// into MaxPageMarkdownChars. With <paramref name="includeContent"/> false the per-page
    /// Markdown is omitted entirely — a verification-only reply that local (slow-reading) client
    /// models can ingest in a fraction of the time.
    /// </summary>
    public static WindowDto BuildWindow(
        DocumentResult result, int fromPage, int pageCount, bool blockSensitivePages,
        bool includeContent = true)
    {
        var pages = result.Pages;
        int totalPages = pages.Count;
        fromPage = Math.Clamp(fromPage, 1, Math.Max(1, totalPages));
        pageCount = Math.Clamp(pageCount, 1, MaxWindowPages);

        var window = pages
            .Where(p => p.PageNumber >= fromPage)
            .OrderBy(p => p.PageNumber)
            .Take(pageCount)
            .Select(p =>
            {
                var (markdown, truncated) = includeContent
                    ? Cap(PageMarkdown(p, blockSensitivePages), MaxPageMarkdownChars)
                    : ((string?)null, false);
                return new PageDto(
                    p.PageNumber,
                    p.Source.ToString(),
                    markdown,
                    truncated,
                    Round(p.Verification.RecallPercent),
                    p.NeedsReview,
                    p.Notice,
                    p.SensitivityMarking,
                    p.LowResolution,
                    p.FormFields?.Count ?? 0);
            })
            .ToList();

        return new WindowDto(
            totalPages,
            fromPage,
            window.Count,
            window,
            result.PagesNeedingReview,
            result.SensitivityMarkedPages,
            AverageRecall(pages));
    }

    /// <summary>
    /// Whole-document summary for small documents (extract_summary). The honesty rule carries
    /// through: recall is always accompanied by PagesNeedingReview — a document cannot claim 100%
    /// recall while that list is non-empty.
    /// </summary>
    public static SummaryDto BuildSummary(
        DocumentResult result, bool blockSensitivePages, bool includeContent = true)
    {
        string? capped = null;
        bool truncated = false;
        if (includeContent)
        {
            // Rebuild the document Markdown from per-page Markdown so the privacy gate applies per page.
            string markdown = blockSensitivePages && result.SensitivityMarkedPages.Count > 0
                ? string.Join("\n\n", result.Pages.OrderBy(p => p.PageNumber)
                    .Select(p => PageMarkdown(p, blockSensitivePages)))
                : result.Markdown ?? string.Empty;
            (capped, truncated) = Cap(markdown, MaxSummaryMarkdownChars);
        }

        return new SummaryDto(
            result.Pages.Count,
            capped,
            truncated,
            AverageRecall(result.Pages),
            result.PagesNeedingReview,
            result.SensitivityMarkedPages,
            Math.Round(result.Pages.Sum(p => p.Verification.Seconds), 2));
    }

    public static double? AverageRecall(IReadOnlyList<PageResult> pages)
    {
        var recalls = pages
            .Select(p => p.Verification.RecallPercent)
            .Where(r => r.HasValue)
            .Select(r => r!.Value)
            .ToList();
        return recalls.Count == 0 ? null : Math.Round(recalls.Average(), 1);
    }

    public static double? Round(double? value) =>
        value.HasValue ? Math.Round(value.Value, 1) : null;
}
