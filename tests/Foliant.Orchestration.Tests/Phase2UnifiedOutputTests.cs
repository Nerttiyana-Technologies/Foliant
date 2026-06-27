using Xunit;
using ZD = ZeroDep.Abstractions;
using static Foliant.Orchestration.Tests.TestData;

namespace Foliant.Orchestration.Tests;

/// <summary>
/// Phase 2 gates (ADR-0003): G2a contract conformance (every page carries provenance) and G2b no-loss +
/// boundary integrity + plan fidelity (every planned page appears exactly once, in order).
/// </summary>
public sealed class Phase2UnifiedOutputTests
{
    private static DocumentOrchestrator Orchestrator(ZD.DocumentAnalysis analysis, FakeDocumentProcessor fake, bool on = true)
        => new(fake, new OrchestrationOptions { UseZeroDepFastLane = on }, analyze: _ => analysis);

    [Fact]
    public async Task Unified_result_has_per_page_provenance_plan_and_empty_chunks()
    {
        var analysis = Analysis(
            pages: new[]
            {
                PC(0, ZD.PageContentClass.FormPage),
                PC(1, ZD.PageContentClass.DigitalText),
                PC(2, ZD.PageContentClass.ScannedImageOnly),
                PC(3, ZD.PageContentClass.ScannedImageOnly),
            },
            runs: new[] { Run(1, "some born digital words here now", x: 72, y: 700, width: 180) },
            fields: new[] { Field(0, "f", "v") });
        var fake = new FakeDocumentProcessor
        {
            Handler = o => new DocumentResult((o?.Pages ?? Enumerable.Range(1, 4)).Select(HeavyPage).ToList(), "heavy"),
        };

        var unified = await Orchestrator(analysis, fake).ProcessUnifiedAsync(new byte[] { 1 });

        // G2b — every planned page, exactly once, in order.
        Assert.Equal(new[] { 1, 2, 3, 4 }, unified.Document.Pages.Select(p => p.PageNumber));
        Assert.Equal(4, unified.Document.Pages.Count);

        // G2a — 100% provenance, correct lane per page.
        Assert.Equal(unified.Document.Pages.Count, unified.Provenance.Count);
        Assert.Equal(ProducedBy.ZeroDepFastLane, unified.Provenance[1]);   // form
        Assert.Equal(ProducedBy.ZeroDepFastLane, unified.Provenance[2]);   // digital text
        Assert.Equal(ProducedBy.FoliantHeavyLane, unified.Provenance[3]);  // scanned
        Assert.Equal(ProducedBy.FoliantHeavyLane, unified.Provenance[4]);

        // Plan present; chunks deferred to Phase 4.
        Assert.NotNull(unified.Plan);
        Assert.Equal(new[] { 3, 4 }, unified.Plan!.HeavyPageNumbers);
        Assert.Empty(unified.Chunks);
    }

    [Fact]
    public async Task ProcessAsync_returns_the_unified_documents_result()
    {
        var analysis = Analysis(
            pages: new[] { PC(0, ZD.PageContentClass.DigitalText) },
            runs: new[] { Run(0, "plenty of born digital words on this page", x: 72, y: 700, width: 220) });
        var fake = new FakeDocumentProcessor { Handler = _ => new DocumentResult(Array.Empty<PageResult>(), "") };

        var orch = Orchestrator(analysis, fake);
        var plain = await orch.ProcessAsync(new byte[] { 1 });
        var unified = await orch.ProcessUnifiedAsync(new byte[] { 1 });

        Assert.Equal(plain.Markdown, unified.Document.Markdown);
        Assert.Equal(plain.Pages.Count, unified.Document.Pages.Count);
    }

    [Fact]
    public async Task Abstained_page_is_attributed_to_the_heavy_lane()
    {
        // Claims 30 text runs but decodes nothing → abstains → served by heavy → provenance must be heavy.
        var analysis = Analysis(
            pages: new[] { PC(0, ZD.PageContentClass.DigitalText, textRuns: 30) },
            runs: Array.Empty<ZD.TextRunInfo>());
        var fake = new FakeDocumentProcessor
        {
            Handler = o => new DocumentResult((o?.Pages ?? Enumerable.Range(1, 1)).Select(HeavyPage).ToList(), "h"),
        };

        var unified = await Orchestrator(analysis, fake).ProcessUnifiedAsync(new byte[] { 1 });

        Assert.Equal(ProducedBy.FoliantHeavyLane, unified.Provenance[1]);
    }

    [Fact]
    public async Task Flag_off_unified_is_all_heavy_with_no_plan()
    {
        var fake = new FakeDocumentProcessor
        {
            Handler = _ => new DocumentResult(new[] { HeavyPage(1), HeavyPage(2) }, "x"),
        };
        // analyze must not run when the flag is off.
        var orch = new DocumentOrchestrator(
            fake, new OrchestrationOptions { UseZeroDepFastLane = false },
            analyze: _ => throw new InvalidOperationException("analyze must not run when flag off"));

        var unified = await orch.ProcessUnifiedAsync(new byte[] { 1 });

        Assert.Null(unified.Plan);
        Assert.Equal(2, unified.Provenance.Count);
        Assert.All(unified.Provenance.Values, v => Assert.Equal(ProducedBy.FoliantHeavyLane, v));
        Assert.Empty(unified.Chunks);
    }
}
