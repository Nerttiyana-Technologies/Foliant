using Foliant.Orchestration;
using Foliant.Pipeline;
using Foliant.Specs.Hardware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Foliant.Mcp.Extraction;

/// <summary>
/// Lazy async singleton around the Foliant processor (ADR-0005 D5). The processor is expensive
/// (ONNX sessions; ~330 MB of models), so it is built on the FIRST extraction call — server startup
/// stays instant, tools/list never pays the model cost, and non-extraction sessions never load ONNX.
/// Models resolve from Foliant:ModelsDir when set (offline deployment pattern); otherwise the
/// SHA-256-verified ModelCache downloads on first use. The pipeline is wrapped in the ZeroDep
/// plan-then-execute orchestrator for production parity with FoliantView.
/// </summary>
public sealed class ProcessorHolder : IDisposable
{
    private readonly FoliantMcpOptions _opts;
    private readonly TemplateRouterHolder _templates;
    private readonly ILogger<ProcessorHolder> _log;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private DocumentProcessor? _processor;   // owns the ONNX models; disposed in Dispose
    private IDocumentProcessor? _engine;     // orchestrator wrapping _processor
    private volatile string _state = "not-loaded";

    public ProcessorHolder(
        IOptions<FoliantMcpOptions> options, TemplateRouterHolder templates, ILogger<ProcessorHolder> log)
    {
        _opts = options.Value;
        _templates = templates;
        _log = log;
    }

    /// <summary>"not-loaded" | "loading" | "ready" | "failed: ..." — for server_health.</summary>
    public string State => _state;

    public async Task<IDocumentProcessor> GetAsync(CancellationToken ct = default)
    {
        if (_engine is not null) return _engine;
        await _initLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_engine is null)
            {
                _state = "loading";
                var router = _templates.Get();

                // Deterministic hardware-spec extractor (ADR-0006); a no-op unless a tool call sets
                // ExtractHardwareSpecs. Wired here so the MCP surface's opt-in flag is functional.
                var hardwareSpecs = new HardwareSpecExtractor();

                if (!string.IsNullOrWhiteSpace(_opts.ModelsDir))
                {
                    _log.LogInformation("Loading Foliant models from {ModelsDir}", _opts.ModelsDir);
                    _processor = FoliantProcessor.CreateDefault(
                        _opts.ModelsDir!, templateRouter: router, hardwareSpecs: hardwareSpecs);
                }
                else
                {
                    _log.LogWarning(
                        "Foliant:ModelsDir not set — using Foliant's verified model cache " +
                        "(may download ~330 MB on first use).");
                    _processor = await FoliantProcessor
                        .CreateDefaultAsync(
                            templateRouter: router, hardwareSpecs: hardwareSpecs, cancellationToken: ct)
                        .ConfigureAwait(false);
                }

                // With UseZeroDepFastLane=false the orchestrator delegates verbatim to the pipeline,
                // so wrapping is always safe (same pattern as FoliantView's converter). The hardware-spec
                // extractor is given to the orchestrator too (not only the inner pipeline): the fast-lane
                // path re-assembles the document Markdown and must run the document-level append itself
                // (ADR-0006 open item #5), so the section survives regardless of routing.
                _engine = new DocumentOrchestrator(
                    _processor,
                    new OrchestrationOptions { UseZeroDepFastLane = _opts.UseZeroDepFastLane },
                    hardwareSpecs: hardwareSpecs);
                _state = "ready";
                _log.LogInformation("Foliant processor ready (fast lane: {FastLane}).", _opts.UseZeroDepFastLane);
            }
            return _engine;
        }
        catch (Exception ex)
        {
            _state = "failed: " + ex.Message;
            throw;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public void Dispose()
    {
        _processor?.Dispose();
        _initLock.Dispose();
    }
}
