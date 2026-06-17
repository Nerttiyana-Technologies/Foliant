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
