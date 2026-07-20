using Xunit;
using ZD = ZeroDep.Abstractions;
using static Foliant.Orchestration.Tests.TestData;

namespace Foliant.Orchestration.Tests;

/// <summary>
/// ADR-0006 open item #5: the document-level hardware-spec append is wired in DocumentProcessor, but the
/// fast-lane path re-assembles the document Markdown from per-page output, so the orchestrator must run
/// the append itself over the full merged page set. These tests use a stub extractor (the orchestrator
/// behaviour under test is "consult the extractor over the merged pages and append the rendered section",
/// not the extraction logic — that is covered by HardwareSpecTests).
/// </summary>
public sealed class HardwareSpecOrchestrationTests
{
    private static readonly HardwareSpecProfile ServerProfile = new(
        SystemKind.Server,
        new[] { new HardwareComponent("RS3700 Series Server", Quantity: 2) },
        Array.Empty<SpecAttribute>(),
        0.7);

    private sealed class StubExtractor : IHardwareSpecExtractor
    {
        private readonly HardwareSpecProfile _profile;
        public IReadOnlyList<PageResult>? Seen { get; private set; }
        public StubExtractor(HardwareSpecProfile profile) => _profile = profile;
        public HardwareSpecProfile Extract(IReadOnlyList<PageResult> pages) { Seen = pages; return _profile; }
    }

    // A single born-digital text page — routed entirely to the fast lane (no heavy call).
    private static ZD.DocumentAnalysis FastTextDoc() => Analysis(
        pages: new[] { PC(0, ZD.PageContentClass.DigitalText, 0.95, textRuns: 8) },
        runs: new[] { Run(0, "The quick brown fox jumps over the lazy dog today", x: 72, y: 700) });

    [Fact]
    public async Task Fast_lane_document_still_gets_the_hardware_spec_section()
    {
        var fake = new FakeDocumentProcessor
        {
            Handler = _ => throw new InvalidOperationException("all-fast: heavy lane must not run"),
        };
        var stub = new StubExtractor(ServerProfile);
        var orchestrator = new DocumentOrchestrator(
            fake, new OrchestrationOptions { UseZeroDepFastLane = true },
            analyze: _ => FastTextDoc(), hardwareSpecs: stub);

        var result = await orchestrator.ProcessAsync(
            new byte[] { 1 }, new ProcessingOptions { ExtractHardwareSpecs = true });

        // The section survives the fast-lane re-assembly …
        Assert.Contains("## Hardware Specifications (extracted)", result.Markdown);
        Assert.Contains("RS3700 Series Server", result.Markdown);
        // … and the base page content is still there (additive, not replaced).
        Assert.Contains("quick brown fox", result.Markdown);
        // … computed over the merged page set.
        Assert.NotNull(stub.Seen);
        Assert.Single(stub.Seen!);
    }

    [Fact]
    public async Task Flag_off_never_consults_the_extractor()
    {
        var fake = new FakeDocumentProcessor
        {
            Handler = _ => throw new InvalidOperationException("all-fast: heavy lane must not run"),
        };
        var stub = new StubExtractor(ServerProfile);
        var orchestrator = new DocumentOrchestrator(
            fake, new OrchestrationOptions { UseZeroDepFastLane = true },
            analyze: _ => FastTextDoc(), hardwareSpecs: stub);

        // No options → ExtractHardwareSpecs defaults to false.
        var result = await orchestrator.ProcessAsync(new byte[] { 1 });

        Assert.DoesNotContain("Hardware Specifications", result.Markdown);
        Assert.Null(stub.Seen);
    }

    [Fact]
    public async Task Empty_profile_appends_nothing()
    {
        var fake = new FakeDocumentProcessor
        {
            Handler = _ => throw new InvalidOperationException("all-fast: heavy lane must not run"),
        };
        var stub = new StubExtractor(HardwareSpecProfile.Empty);
        var orchestrator = new DocumentOrchestrator(
            fake, new OrchestrationOptions { UseZeroDepFastLane = true },
            analyze: _ => FastTextDoc(), hardwareSpecs: stub);

        var result = await orchestrator.ProcessAsync(
            new byte[] { 1 }, new ProcessingOptions { ExtractHardwareSpecs = true });

        Assert.NotNull(stub.Seen);                                   // consulted …
        Assert.DoesNotContain("Hardware Specifications", result.Markdown);   // … but nothing to append
    }

    [Fact]
    public async Task Heavy_lane_call_has_the_hardware_flag_stripped_and_the_append_runs_once()
    {
        // Mixed doc: pages 1-2 fast, pages 3-4 escalated. The inner heavy call must NOT be asked to append
        // (it sees only the heavy subset); the orchestrator appends once over all four merged pages.
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

        var fake = new FakeDocumentProcessor
        {
            Handler = o => new DocumentResult(
                (o?.Pages ?? Enumerable.Range(1, 4)).Select(HeavyPage).ToList(), "heavy"),
        };
        var stub = new StubExtractor(ServerProfile);
        var orchestrator = new DocumentOrchestrator(
            fake, new OrchestrationOptions { UseZeroDepFastLane = true },
            analyze: _ => analysis, hardwareSpecs: stub);

        var result = await orchestrator.ProcessAsync(
            new byte[] { 1 }, new ProcessingOptions { ExtractHardwareSpecs = true });

        Assert.False(fake.LastOptions!.ExtractHardwareSpecs);        // stripped from the inner heavy call
        Assert.Equal(4, stub.Seen!.Count);                          // orchestrator ran it over all pages
        // Appended exactly once.
        var section = "## Hardware Specifications (extracted)";
        Assert.Contains(section, result.Markdown);
        Assert.Equal(result.Markdown.IndexOf(section, StringComparison.Ordinal),
                     result.Markdown.LastIndexOf(section, StringComparison.Ordinal));
    }
}
