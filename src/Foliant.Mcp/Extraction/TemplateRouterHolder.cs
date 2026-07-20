using Foliant.Templates;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Foliant.Mcp.Extraction;

/// <summary>
/// Lazily builds the page-template router (bundled federal Standard Forms + optional customer SQLite
/// store). Model-free — fingerprint matching over the PDF's widgets/text — so match_template works
/// without ever loading the ONNX processor. Shared with <see cref="ProcessorHolder"/>, which wires the
/// same router into the pipeline; the underlying <see cref="TemplateLibrary"/> picks up newly
/// registered customer templates without a rebuild. Mirrors FoliantView's TemplateCatalog wiring.
/// </summary>
public sealed class TemplateRouterHolder : IDisposable
{
    private readonly FoliantMcpOptions _opts;
    private readonly ILogger<TemplateRouterHolder> _log;
    private readonly object _lock = new();
    private TemplateLibrary? _library;

    public TemplateRouterHolder(IOptions<FoliantMcpOptions> options, ILogger<TemplateRouterHolder> log)
    {
        _opts = options.Value;
        _log = log;
    }

    /// <summary>"bundled-only" or the customer store path — for server_health.</summary>
    public string Mode =>
        string.IsNullOrWhiteSpace(_opts.TemplatesDbPath)
            ? "bundled federal templates only"
            : $"bundled + customer store '{_opts.TemplatesDbPath}'";

    public IPageTemplateRouter Get()
    {
        if (_library is not null) return _library.Router;
        lock (_lock)
        {
            if (_library is null)
            {
                string dbPath;
                if (string.IsNullOrWhiteSpace(_opts.TemplatesDbPath))
                {
                    // Bundled-only mode: TemplateLibrary documents ":memory:" as the storeless form —
                    // the registry then serves the bundled federal templates over an empty store.
                    dbPath = ":memory:";
                }
                else
                {
                    dbPath = _opts.TemplatesDbPath!;
                    var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                }

                _library = new TemplateLibrary(dbPath);
                _log.LogInformation("Template routing ready ({Mode}).", Mode);
            }
            return _library.Router;
        }
    }

    public void Dispose() => _library?.Dispose();
}
