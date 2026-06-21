using System.Text.Json;

namespace Foliant.Templates;

/// <summary>
/// Productized entry point for the "bring your own template" library. Wraps the generator + SQLite store +
/// registry + router so a customer can register their OWN blank form templates and have uploads routed to
/// template-aware extraction (falling back to the default pipeline for anything unrecognized).
///
/// Review lifecycle: <see cref="Register"/> a blank PDF → a DRAFT <see cref="FormLayout"/> with auto-paired
/// labels is stored and is immediately routable. Dense checkbox blocks get geometry-only labels that should be
/// corrected once — <see cref="ToJson"/> the draft, edit the labels offline, then <see cref="FromJson"/> +
/// <see cref="Update"/> to commit. Customer templates merge with the bundled federal templates; a customer
/// template overrides a bundled one with the same id. <see cref="Router"/> plugs straight into the pipeline.
/// </summary>
public sealed class TemplateLibrary : IDisposable
{
    private readonly TemplateStore _store;
    private readonly TemplateRegistry _registry;
    private readonly TemplateRouter _router;

    /// <param name="customerDbPath">SQLite file for the customer's registered templates (created if absent;
    /// use ":memory:" for tests).</param>
    public TemplateLibrary(string customerDbPath)
    {
        _store = new TemplateStore(customerDbPath);
        _registry = new TemplateRegistry(_store);
        _router = new TemplateRouter(_registry);
    }

    /// <summary>The per-page router over bundled + customer templates. Wire into the pipeline (it implements
    /// <see cref="IPageTemplateRouter"/>); newly registered templates are picked up without rebuilding.</summary>
    public IPageTemplateRouter Router => _router;

    /// <summary>
    /// Registers a customer's blank template: generates a DRAFT layout (auto-labels) and stores it, returning
    /// the draft so the caller can review labels and commit corrections via <see cref="Update"/>.
    /// </summary>
    public FormLayout Register(byte[] blankPdf, string templateId, string name)
    {
        var layout = FormLayoutGenerator.Generate(blankPdf, templateId, name);
        _store.Save(layout);
        return layout;
    }

    /// <summary>Persists a reviewed/edited layout (label corrections), keyed by template id.</summary>
    public void Update(FormLayout reviewed) => _store.Save(reviewed);

    /// <summary>Removes a customer template; true when one was deleted. Bundled templates are unaffected.</summary>
    public bool Unregister(string templateId) => _store.Delete(templateId);

    /// <summary>A registered customer template, or null.</summary>
    public FormLayout? Get(string templateId) => _store.Get(templateId);

    /// <summary>Only the customer-registered templates.</summary>
    public IReadOnlyList<FormLayout> CustomerTemplates() => _store.All();

    /// <summary>The full candidate set: bundled federal templates + customer templates (customer wins on id).</summary>
    public IReadOnlyList<FormLayout> AllTemplates() => _registry.All();

    /// <summary>Serializes a layout to indented JSON for offline label review.</summary>
    public static string ToJson(FormLayout layout) =>
        JsonSerializer.Serialize(layout, new JsonSerializerOptions(TemplateRegistry.Json) { WriteIndented = true });

    /// <summary>Parses a reviewed layout JSON (e.g. produced by <see cref="ToJson"/>).</summary>
    public static FormLayout FromJson(string json) =>
        JsonSerializer.Deserialize<FormLayout>(json, TemplateRegistry.Json)
        ?? throw new ArgumentException("Invalid template JSON.", nameof(json));

    public void Dispose() => _store.Dispose();
}
