using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Foliant;

namespace Foliant.Templates;

/// <summary>
/// The candidate set an upload is matched against: the BUNDLED federal Standard Form templates (shipped as
/// embedded resources — accurate out of the box, no customer setup) merged with the customer's OWN registered
/// templates (a <see cref="TemplateStore"/> SQLite file). On a <see cref="FormLayout.TemplateId"/> collision
/// the customer's version wins, so a customer can override a bundled federal template with their own revision.
/// </summary>
public sealed class TemplateRegistry
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IReadOnlyList<FormLayout> _bundled;
    private readonly TemplateStore? _customer;

    /// <param name="customerStore">Customer-registered templates (SQLite). Null = bundled federal only.</param>
    /// <param name="bundled">Override the bundled set (tests). Null = load the embedded federal templates.</param>
    public TemplateRegistry(TemplateStore? customerStore = null, IEnumerable<FormLayout>? bundled = null)
    {
        _bundled = (bundled ?? LoadBundled()).ToList();
        _customer = customerStore;
    }

    /// <summary>Merged candidate set; customer templates override bundled ones with the same id.</summary>
    public IReadOnlyList<FormLayout> All()
    {
        var byId = new Dictionary<string, FormLayout>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _bundled) byId[t.TemplateId] = t;
        if (_customer is not null)
            foreach (var t in _customer.All()) byId[t.TemplateId] = t;   // customer wins
        return byId.Values.ToList();
    }

    /// <summary>The bundled federal templates only (no customer store).</summary>
    public IReadOnlyList<FormLayout> Bundled => _bundled;

    /// <summary>Reads every "*.template.json" embedded resource shipped in this assembly.</summary>
    public static IReadOnlyList<FormLayout> LoadBundled()
    {
        var result = new List<FormLayout>();
        var asm = typeof(TemplateRegistry).Assembly;
        foreach (var name in asm.GetManifestResourceNames()
                     .Where(n => n.EndsWith(".template.json", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            if (JsonSerializer.Deserialize<FormLayout>(reader.ReadToEnd(), Json) is { } layout)
                result.Add(layout);
        }
        return result;
    }
}
