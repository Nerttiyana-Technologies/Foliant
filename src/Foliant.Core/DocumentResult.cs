using System.Text.Json;
using System.Text.Json.Serialization;

namespace Foliant;

/// <summary>Result of processing a whole document.</summary>
/// <param name="Pages">Per-page results in page order.</param>
/// <param name="Markdown">Concatenated per-page Markdown.</param>
public sealed record DocumentResult(
    IReadOnlyList<PageResult> Pages,
    string Markdown)
{
    /// <summary>
    /// Page numbers flagged <see cref="PageResult.NeedsReview"/> — pages whose text came from
    /// pixels, produced ~no words, and have no text-layer truth vouching for them. Such pages are
    /// invisible to recall aggregates (<see cref="PageVerification.RecallPercent"/> is null), so a
    /// caller reporting document recall MUST surface this list alongside it: a document cannot
    /// honestly claim 100% recall while this list is non-empty.
    /// </summary>
    public IReadOnlyList<int> PagesNeedingReview =>
        Pages.Where(p => p.NeedsReview).Select(p => p.PageNumber).ToList();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly JsonSerializerOptions JsonOptionsIndented = new(JsonOptions)
    {
        WriteIndented = true,
    };

    /// <summary>Structured JSON export of all pages (regions, bounds, tables, verification).</summary>
    public string ToJson(bool indented = false) =>
        JsonSerializer.Serialize(Pages, indented ? JsonOptionsIndented : JsonOptions);
}
