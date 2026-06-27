using Xunit;
using ZD = ZeroDep.Abstractions;
using static Foliant.Orchestration.Tests.TestData;

namespace Foliant.Orchestration.Tests;

/// <summary>
/// End-to-end executor tests with a fake Foliant pipeline and an injected analysis (no real PDF). Verifies
/// plan-then-execute: per-page routing, batched heavy call via <see cref="ProcessingOptions.Pages"/>,
/// fast-lane building, order-preserving merge, the whole-doc shortcut, and the flag-off pass-through.
/// </summary>
public sealed class DocumentOrchestratorExecutorTests
{
    [Fact]
    public async Task Mixed_document_routes_per_page_and_merges_in_order()
    {
        var analysis = Analysis(
            pages: new[]
            {
                PC(0, ZD.PageContentClass.FormPage),
                PC(1, ZD.PageContentClass.DigitalText),
                PC(2, ZD.PageContentClass.ScannedImageOnly),
                PC(3, ZD.PageContentClass.ScannedImageOnly),
            },
            runs: new[] { Run(1, "Hello", x: 72, y: 700, width: 30), Run(1, "world", x: 110, y: 700, width: 30) },
            fields: new[] { Field(0, "applicant.name", "Jane Doe") });

        PageRoutingPlan? captured = null;
        var fake = new FakeDocumentProcessor
        {
            Handler = o => new DocumentResult(
                (o?.Pages ?? Enumerable.Range(1, 4)).Select(HeavyPage).ToList(), "heavy"),
        };
        var opts = new OrchestrationOptions { UseZeroDepFastLane = true, OnPlan = p => captured = p };
        var orchestrator = new DocumentOrchestrator(fake, opts, analyze: _ => analysis);

        var result = await orchestrator.ProcessAsync(new byte[] { 1 });

        // Plan + audit hook
        Assert.NotNull(captured);
        Assert.Equal(new[] { 3, 4 }, captured!.HeavyPageNumbers);
        Assert.False(captured.WholeDocumentEscalated);

        // Heavy lane: one batched call over just the escalated pages
        Assert.Equal(1, fake.Calls);
        Assert.Equal(new[] { 3, 4 }, fake.LastOptions!.Pages!);

        // Merge: every page, in order
        Assert.Equal(new[] { 1, 2, 3, 4 }, result.Pages.Select(p => p.PageNumber));

        // Fast lane outputs
        Assert.NotNull(result.Pages[0].FormFields);
        Assert.Equal("applicant.name", Assert.Single(result.Pages[0].FormFields!).Name);
        Assert.Contains("Hello world", result.Pages[1].Markdown);

        // Heavy lane outputs preserved
        Assert.Equal("HEAVY p3", result.Pages[2].Markdown);
        Assert.Equal("HEAVY p4", result.Pages[3].Markdown);
    }

    [Fact]
    public async Task Mostly_heavy_document_takes_the_whole_document_shortcut()
    {
        var analysis = Analysis(Enumerable.Range(0, 4).Select(i => PC(i, ZD.PageContentClass.ScannedImageOnly)));

        PageRoutingPlan? captured = null;
        var fake = new FakeDocumentProcessor
        {
            Handler = o => new DocumentResult(
                (o?.Pages ?? Enumerable.Range(1, 4)).Select(HeavyPage).ToList(), "heavy"),
        };
        var opts = new OrchestrationOptions { UseZeroDepFastLane = true, OnPlan = p => captured = p };
        var orchestrator = new DocumentOrchestrator(fake, opts, analyze: _ => analysis);

        var result = await orchestrator.ProcessAsync(new byte[] { 1 });

        Assert.True(captured!.WholeDocumentEscalated);
        Assert.Equal(1, fake.Calls);
        Assert.Null(fake.LastOptions?.Pages);          // whole document, not a page subset
        Assert.Equal(4, result.Pages.Count);
    }

    [Fact]
    public async Task All_fast_document_never_calls_the_heavy_lane()
    {
        var analysis = Analysis(
            pages: new[] { PC(0, ZD.PageContentClass.FormPage), PC(1, ZD.PageContentClass.DigitalText) },
            runs: new[] { Run(1, "some born digital text here") },
            fields: new[] { Field(0, "f", "v") });

        var fake = new FakeDocumentProcessor
        {
            Handler = _ => throw new InvalidOperationException("heavy lane must not run when all pages are fast"),
        };
        var opts = new OrchestrationOptions { UseZeroDepFastLane = true };
        var orchestrator = new DocumentOrchestrator(fake, opts, analyze: _ => analysis);

        var result = await orchestrator.ProcessAsync(new byte[] { 1 });

        Assert.Equal(0, fake.Calls);
        Assert.Equal(2, result.Pages.Count);
    }

    [Fact]
    public async Task Page_with_undecodable_text_layer_abstains_and_escalates()
    {
        // DigitalText page that CLAIMS 30 text runs (structure) but has NO decodable runs (undecodable
        // CID layer → empty text). The fast lane would emit ~nothing, so it must escalate to heavy.
        var analysis = Analysis(
            pages: new[] { PC(0, ZD.PageContentClass.DigitalText, 0.95, textRuns: 30) },
            runs: Array.Empty<ZD.TextRunInfo>());

        var fake = new FakeDocumentProcessor
        {
            Handler = o => new DocumentResult((o?.Pages ?? Enumerable.Range(1, 1)).Select(HeavyPage).ToList(), "heavy"),
        };
        var orchestrator = new DocumentOrchestrator(
            fake, new OrchestrationOptions { UseZeroDepFastLane = true }, analyze: _ => analysis);

        var result = await orchestrator.ProcessAsync(new byte[] { 1 });

        Assert.Equal(1, fake.Calls);
        Assert.Equal(new[] { 1 }, fake.LastOptions!.Pages!);     // the abstained page was escalated
        Assert.Equal("HEAVY p1", Assert.Single(result.Pages).Markdown);
    }

    [Fact]
    public async Task Low_text_decode_trust_page_escalates_even_with_normal_word_count()
    {
        // Plenty of decoded "words", but ZeroDep flags the decode as untrustworthy (symbolic CID, no
        // /ToUnicode → plausibly-wrong text). The fast lane would emit confidently-wrong text → escalate.
        var analysis = Analysis(
            pages: new[] { PC(0, ZD.PageContentClass.DigitalText, 0.95, textRuns: 30, textDecodeConfidence: 0.2) },
            runs: new[] { Run(0, "garbled glyphs that decoded to the wrong letters entirely", x: 72, y: 700) });

        var fake = new FakeDocumentProcessor
        {
            Handler = o => new DocumentResult((o?.Pages ?? Enumerable.Range(1, 1)).Select(HeavyPage).ToList(), "heavy"),
        };
        var orchestrator = new DocumentOrchestrator(
            fake, new OrchestrationOptions { UseZeroDepFastLane = true }, analyze: _ => analysis);

        var result = await orchestrator.ProcessAsync(new byte[] { 1 });

        Assert.Equal(1, fake.Calls);
        Assert.Equal(new[] { 1 }, fake.LastOptions!.Pages!);
        Assert.Equal("HEAVY p1", Assert.Single(result.Pages).Markdown);
    }

    [Fact]
    public async Task Page_with_real_text_layer_is_not_escalated()
    {
        // Same claimed structure, but the runs actually decode → fast lane keeps it (no escalation).
        var analysis = Analysis(
            pages: new[] { PC(0, ZD.PageContentClass.DigitalText, 0.95, textRuns: 30) },
            runs: new[]
            {
                Run(0, "The quick brown fox jumps over the lazy dog today", x: 72, y: 700),
            });

        var fake = new FakeDocumentProcessor
        {
            Handler = _ => throw new InvalidOperationException("heavy must not run; the page has real text"),
        };
        var orchestrator = new DocumentOrchestrator(
            fake, new OrchestrationOptions { UseZeroDepFastLane = true }, analyze: _ => analysis);

        var result = await orchestrator.ProcessAsync(new byte[] { 1 });

        Assert.Equal(0, fake.Calls);
        Assert.Contains("quick brown", Assert.Single(result.Pages).Markdown);
    }

    [Fact]
    public async Task Flag_off_delegates_verbatim_and_never_analyzes()
    {
        var fake = new FakeDocumentProcessor
        {
            Handler = _ => new DocumentResult(new[] { HeavyPage(1) }, "x"),
        };
        var opts = new OrchestrationOptions { UseZeroDepFastLane = false };
        var orchestrator = new DocumentOrchestrator(
            fake, opts,
            analyze: _ => throw new InvalidOperationException("analyze must not run when the flag is off"));

        var result = await orchestrator.ProcessAsync(new byte[] { 1 });

        Assert.Equal(1, fake.Calls);
        Assert.Single(result.Pages);
    }
}
