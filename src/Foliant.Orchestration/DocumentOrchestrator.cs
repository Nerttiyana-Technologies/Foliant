using Foliant.Pipeline;              // HardwareSpecSection (internal renderer, ADR-0006)
using ZeroDep;                       // PdfAnalyzer (entry point)
using ZD = ZeroDep.Abstractions;     // analysis result types

namespace Foliant.Orchestration;

/// <summary>
/// The plan-then-execute orchestrator. It is itself an <see cref="IDocumentProcessor"/>, so it is a drop-in
/// for any caller (e.g. FoliantView) that already depends on the Foliant pipeline.
///
/// <para>With <see cref="OrchestrationOptions.UseZeroDepFastLane"/> <b>off</b> (the default) every call
/// delegates verbatim to the wrapped Foliant pipeline — zero behavioural change. With it <b>on</b>, the
/// orchestrator runs the ADR-0003 plan-then-execute path: one ZeroDep scan → a per-page routing plan →
/// fast-lane pages answered from ZeroDep, escalated pages sent to Foliant in <b>one batched call</b> (via
/// <see cref="ProcessingOptions.Pages"/>, so the models load once) → merge in original page order.</para>
/// </summary>
public sealed class DocumentOrchestrator : IDocumentProcessor, IUnifiedDocumentProcessor
{
    private readonly IDocumentProcessor _foliant;
    private readonly OrchestrationOptions _options;
    private readonly IPageClassificationReader _reader;
    private readonly FastLanePageBuilder _builder;
    private readonly Func<byte[], ZD.DocumentAnalysis> _analyze;
    private readonly IHardwareSpecExtractor? _hardwareSpecs;

    /// <param name="foliantPipeline">The inner Foliant pipeline (the heavy lane / pass-through).</param>
    /// <param name="options">Orchestration options; defaults to fast lane off (pass-through).</param>
    /// <param name="reader">ZeroDep classification reader; defaults to <see cref="ZeroDepClassificationReader"/>.</param>
    /// <param name="fastLaneBuilder">Fast-lane page builder; defaults to one over <see cref="ZeroDepTypeAdapter"/>.</param>
    /// <param name="analyze">
    /// The ZeroDep analysis step; defaults to <c>PdfAnalyzer.Analyze</c>. Injectable so the executor can be
    /// unit-tested without a real PDF or the engine.
    /// </param>
    /// <param name="hardwareSpecs">
    /// Optional document-level hardware-spec extractor (ADR-0006). Needed HERE, not only in the inner
    /// pipeline, because the fast-lane path re-assembles the document Markdown from per-page output and
    /// would otherwise drop the document-level section. When supplied and
    /// <see cref="ProcessingOptions.ExtractHardwareSpecs"/> is on, the section is appended over the full
    /// merged page set (fast + heavy). Null disables it. Additive only; empty ⇒ no-op.
    /// </param>
    public DocumentOrchestrator(
        IDocumentProcessor foliantPipeline,
        OrchestrationOptions? options = null,
        IPageClassificationReader? reader = null,
        FastLanePageBuilder? fastLaneBuilder = null,
        Func<byte[], ZD.DocumentAnalysis>? analyze = null,
        IHardwareSpecExtractor? hardwareSpecs = null)
    {
        _foliant = foliantPipeline ?? throw new ArgumentNullException(nameof(foliantPipeline));
        _options = options ?? new OrchestrationOptions();
        _reader = reader ?? new ZeroDepClassificationReader();
        _builder = fastLaneBuilder ?? new FastLanePageBuilder(new ZeroDepTypeAdapter());
        _hardwareSpecs = hardwareSpecs;
        _analyze = analyze ?? (static bytes =>
        {
            using var ms = new MemoryStream(bytes, writable: false);
            return PdfAnalyzer.Analyze(ms);
        });
    }

    /// <inheritdoc />
    public Task<DocumentResult> ProcessAsync(
        byte[] pdf, ProcessingOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return _options.UseZeroDepFastLane
            ? RouteAsync(pdf, options, cancellationToken)
            : _foliant.ProcessAsync(pdf, options, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DocumentResult> ProcessAsync(
        Stream pdf, ProcessingOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);

        // Flag off: delegate the stream verbatim (no buffering) — exact pass-through.
        if (!_options.UseZeroDepFastLane)
            return await _foliant.ProcessAsync(pdf, options, cancellationToken).ConfigureAwait(false);

        using var ms = new MemoryStream();
        await pdf.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return await RouteAsync(ms.ToArray(), options, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<UnifiedDocument> ProcessUnifiedAsync(
        byte[] pdf, ProcessingOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        return _options.UseZeroDepFastLane
            ? RouteUnifiedAsync(pdf, options, cancellationToken)
            : FoliantOnlyUnifiedAsync(_foliant.ProcessAsync(pdf, options, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<UnifiedDocument> ProcessUnifiedAsync(
        Stream pdf, ProcessingOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        if (!_options.UseZeroDepFastLane)
            return await FoliantOnlyUnifiedAsync(_foliant.ProcessAsync(pdf, options, cancellationToken)).ConfigureAwait(false);

        using var ms = new MemoryStream();
        await pdf.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
        return await RouteUnifiedAsync(ms.ToArray(), options, cancellationToken).ConfigureAwait(false);
    }

    // Flag-off: the whole document went through Foliant — wrap it with all-heavy provenance (so the unified
    // contract still has 100% per-page provenance) and no plan.
    private static async Task<UnifiedDocument> FoliantOnlyUnifiedAsync(Task<DocumentResult> foliantTask)
    {
        var doc = await foliantTask.ConfigureAwait(false);
        return new UnifiedDocument(
            doc, Plan: null, Chunks: Array.Empty<DocumentChunk>(),
            Provenance: doc.Pages.ToDictionary(p => p.PageNumber, _ => ProducedBy.FoliantHeavyLane));
    }

    // The plan-then-execute path (fast lane on), returning just the DocumentResult.
    private async Task<DocumentResult> RouteAsync(
        byte[] pdf, ProcessingOptions? options, CancellationToken cancellationToken)
        => (await RouteUnifiedAsync(pdf, options, cancellationToken).ConfigureAwait(false)).Document;

    // The plan-then-execute path, returning the full unified result (DocumentResult + routing plan +
    // per-page provenance + chunks). This is the Phase-2 contract; ProcessAsync returns its .Document.
    private async Task<UnifiedDocument> RouteUnifiedAsync(
        byte[] pdf, ProcessingOptions? options, CancellationToken cancellationToken)
    {
        var analysis = _analyze(pdf);
        var inputs = _reader.Read(analysis);
        var plan = RoutingPolicy.BuildPlan(inputs, _options);

        SafeInvokeOnPlan(plan);

        // Stopped (integrity/decrypt) or mostly-heavy: let the Foliant pipeline own the whole document.
        if (plan.DocumentStopped || plan.WholeDocumentEscalated)
        {
            var whole = await _foliant.ProcessAsync(pdf, options, cancellationToken).ConfigureAwait(false);
            return new UnifiedDocument(
                whole, plan, Array.Empty<DocumentChunk>(),
                whole.Pages.ToDictionary(p => p.PageNumber, _ => ProducedBy.FoliantHeavyLane));
        }

        // Source data grouped by 0-based page index.
        var runsByPage = analysis.TextRuns
            .GroupBy(r => r.PageIndex)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ZD.TextRunInfo>)g.ToList());
        var fieldsByPage = analysis.Form.Fields
            .Where(f => f.PageIndex.HasValue)
            .GroupBy(f => f.PageIndex!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ZD.FormFieldInfo>)g.ToList());
        var signalsByPage = analysis.Pages.ToDictionary(p => p.PageIndex, p => p.Signals);

        // Build fast-lane pages first. A page that ABSTAINS — it claims text structure but decoded to almost
        // nothing (an untrustworthy/undecodable text layer) — is escalated instead of emitting empty output
        // (ADR-0003 abstention; mirrors Foliant's own text-layer trust guard).
        var fastResults = new Dictionary<int, PageResult>();
        var abstained = new List<int>();
        foreach (var entry in plan.Pages.Where(e => e.Lane == PageLane.Fast))
        {
            int idx = entry.PageNumber - 1;
            runsByPage.TryGetValue(idx, out var runs);
            fieldsByPage.TryGetValue(idx, out var fields);
            runs ??= Array.Empty<ZD.TextRunInfo>();

            var fastPage = _builder.Build(entry.PageNumber, entry.Kind, runs, fields ?? Array.Empty<ZD.FormFieldInfo>());

            signalsByPage.TryGetValue(idx, out var sig);
            int structureRuns = sig?.TextRunCount ?? runs.Count(r => !r.IsOcrLayer);
            double trust = sig?.TextDecodeConfidence ?? 1.0;

            if (ShouldAbstain(entry.Kind, structureRuns, trust, fastPage))
                abstained.Add(entry.PageNumber);
            else
                fastResults[entry.PageNumber] = fastPage;
        }

        // Heavy lane = planned-heavy ∪ abstained, in one batched Foliant call (models load once). The
        // hardware-spec append is stripped from this inner call: it would run over only the heavy subset
        // and land in a document string we discard below. The append runs once, here, over the full
        // merged page set (ADR-0006 open item #5).
        var heavyNumbers = plan.HeavyPageNumbers.Concat(abstained).Distinct().OrderBy(n => n).ToList();
        DocumentResult? heavy = heavyNumbers.Count > 0
            ? await _foliant.ProcessAsync(
                    pdf,
                    (options ?? ProcessingOptions.Default) with
                    {
                        Pages = heavyNumbers,
                        ExtractHardwareSpecs = false,
                    },
                    cancellationToken)
                .ConfigureAwait(false)
            : null;
        var heavyByPage = heavy?.Pages.ToDictionary(p => p.PageNumber) ?? new Dictionary<int, PageResult>();

        // Merge in original page order, recording per-page provenance: a fast page that survived abstention
        // wins (ZeroDep fast lane); otherwise the heavy result (covers planned-heavy + abstained-fast pages).
        var pages = new List<PageResult>(plan.Pages.Count);
        var provenance = new Dictionary<int, ProducedBy>(plan.Pages.Count);
        foreach (var entry in plan.Pages.OrderBy(e => e.PageNumber))
        {
            if (fastResults.TryGetValue(entry.PageNumber, out var fp))
            {
                pages.Add(fp);
                provenance[entry.PageNumber] = ProducedBy.ZeroDepFastLane;
            }
            else if (heavyByPage.TryGetValue(entry.PageNumber, out var hp))
            {
                pages.Add(hp);
                provenance[entry.PageNumber] = ProducedBy.FoliantHeavyLane;
            }
        }

        string markdown = string.Join(
            "\n\n",
            pages.Where(p => !string.IsNullOrWhiteSpace(p.Markdown)).Select(p => p.Markdown));

        // Document-level hardware-spec section (ADR-0006). The inner pipeline appends this on the direct
        // path, but the fast-lane merge rebuilt the document Markdown just above from per-page output, so
        // the document-level pass runs again here over the full merged page set (fast-lane text pages carry
        // Lines for the bullet/key-value strategies; escalated pages carry table Regions). Additive — an
        // empty profile appends nothing.
        markdown = AppendHardwareSpecs(markdown, pages, options);

        return new UnifiedDocument(
            new DocumentResult(pages, markdown), plan, Array.Empty<DocumentChunk>(), provenance);
    }

    // ADR-0006: append the document-level hardware-spec section to a freshly-merged fast-lane document.
    // No-op unless the flag is on, an extractor is wired, and the document actually describes hardware.
    private string AppendHardwareSpecs(string markdown, IReadOnlyList<PageResult> pages, ProcessingOptions? options)
    {
        if (options?.ExtractHardwareSpecs != true || _hardwareSpecs is null) return markdown;
        var profile = _hardwareSpecs.Extract(pages);
        if (profile.Components.Count == 0) return markdown;
        return markdown + "\n\n---\n\n" + HardwareSpecSection.Render(profile);
    }

    private bool ShouldAbstain(PageKind kind, int structureRuns, double textDecodeConfidence, PageResult fastPage)
        => FastLaneAbstention.ShouldAbstain(kind, structureRuns, textDecodeConfidence, fastPage, _options);

    private void SafeInvokeOnPlan(PageRoutingPlan plan)
    {
        if (_options.OnPlan is null) return;
        try { _options.OnPlan(plan); }
        catch { /* the audit/observer hook must never break the pipeline */ }
    }
}
