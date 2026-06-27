using Xunit;

namespace Foliant.Orchestration.Tests;

/// <summary>
/// Unit tests for the ZeroDep-free routing policy (the ADR-0003 decision table). These run without a PDF or
/// the ZeroDep engine — the routing logic is pure.
/// </summary>
public sealed class RoutingPolicyTests
{
    private static readonly OrchestrationOptions Defaults = new();

    [Theory]
    [InlineData(PageKind.DigitalText)]
    [InlineData(PageKind.FormPage)]
    [InlineData(PageKind.Empty)]
    public void Confident_fast_lane_kinds_go_fast(PageKind kind)
    {
        var (lane, _) = RoutingPolicy.Decide(new PageRoutingInput(1, kind, Confidence: 0.95), Defaults);
        Assert.Equal(PageLane.Fast, lane);
    }

    [Theory]
    [InlineData(PageKind.TableOrComplexLayout)]
    [InlineData(PageKind.ScannedImageOnly)]
    [InlineData(PageKind.ScannedWithOcr)]
    [InlineData(PageKind.Mixed)]
    public void Pixel_or_structure_kinds_escalate(PageKind kind)
    {
        var (lane, _) = RoutingPolicy.Decide(new PageRoutingInput(1, kind, Confidence: 0.99), Defaults);
        Assert.Equal(PageLane.Heavy, lane);
    }

    [Fact]
    public void Low_confidence_fast_candidate_escalates()
    {
        // Bias to escalate on doubt — a form page ZeroDep is unsure about must not be fast-laned.
        var input = new PageRoutingInput(1, PageKind.FormPage, Confidence: 0.40);
        var (lane, _) = RoutingPolicy.Decide(input, Defaults);
        Assert.Equal(PageLane.Heavy, lane);
    }

    [Fact]
    public void Unprocessable_stops()
    {
        var (lane, _) = RoutingPolicy.Decide(new PageRoutingInput(1, PageKind.Unprocessable, 0), Defaults);
        Assert.Equal(PageLane.Stop, lane);
    }

    [Fact]
    public void Table_escalates_by_default_regardless_of_ruling_lines()
    {
        // Default threshold is 0 → never reclaim → every table-class page is heavy (today's behavior).
        var lowRuling = RoutingPolicy.Decide(new PageRoutingInput(1, PageKind.TableOrComplexLayout, 0.95, RulingLineCount: 2), Defaults);
        var highRuling = RoutingPolicy.Decide(new PageRoutingInput(2, PageKind.TableOrComplexLayout, 0.95, RulingLineCount: 40), Defaults);
        Assert.Equal(PageLane.Heavy, lowRuling.Lane);
        Assert.Equal(PageLane.Heavy, highRuling.Lane);
    }

    [Fact]
    public void Low_ruling_table_is_reclaimed_when_threshold_set()
    {
        var opts = Defaults with { TableRulingLineThreshold = 10 };
        var lowRuling = RoutingPolicy.Decide(new PageRoutingInput(1, PageKind.TableOrComplexLayout, 0.95, RulingLineCount: 3), opts);
        var highRuling = RoutingPolicy.Decide(new PageRoutingInput(2, PageKind.TableOrComplexLayout, 0.95, RulingLineCount: 40), opts);
        Assert.Equal(PageLane.Fast, lowRuling.Lane);   // < 10 ruling → reclaimed as text
        Assert.Equal(PageLane.Heavy, highRuling.Lane); // >= 10 ruling → genuine table, stays heavy
    }

    [Fact]
    public void BuildPlan_routes_a_mixed_document_per_page()
    {
        var pages = new[]
        {
            new PageRoutingInput(1, PageKind.FormPage, 0.95),            // fast
            new PageRoutingInput(2, PageKind.DigitalText, 0.97),         // fast
            new PageRoutingInput(3, PageKind.TableOrComplexLayout, 0.9), // heavy
            new PageRoutingInput(4, PageKind.ScannedImageOnly, 0.9),     // heavy
        };

        var plan = RoutingPolicy.BuildPlan(pages, Defaults);

        Assert.False(plan.DocumentStopped);
        Assert.False(plan.WholeDocumentEscalated);
        Assert.Equal(2, plan.FastLaneCount);
        Assert.Equal(2, plan.HeavyLaneCount);
        Assert.Equal(new[] { 3, 4 }, plan.HeavyPageNumbers);
        Assert.Equal(new[] { 1, 2 }, plan.FastPageNumbers);
        Assert.Equal(0.5, plan.EscalationShare, 3);
    }

    [Fact]
    public void BuildPlan_applies_whole_document_shortcut_above_threshold()
    {
        // 9 of 10 pages heavy (0.9) ≥ default 0.80 threshold → whole-doc escalation, every page Heavy.
        var pages = Enumerable.Range(1, 10)
            .Select(i => new PageRoutingInput(i, i == 1 ? PageKind.DigitalText : PageKind.ScannedImageOnly, 0.95))
            .ToList();

        var plan = RoutingPolicy.BuildPlan(pages, Defaults);

        Assert.True(plan.WholeDocumentEscalated);
        Assert.Equal(10, plan.HeavyLaneCount);
        Assert.Equal(0, plan.FastLaneCount);
    }

    [Fact]
    public void BuildPlan_stops_whole_document_when_any_page_unprocessable()
    {
        var pages = new[]
        {
            new PageRoutingInput(1, PageKind.DigitalText, 0.95),
            new PageRoutingInput(2, PageKind.Unprocessable, 0),
        };

        var plan = RoutingPolicy.BuildPlan(pages, Defaults);

        Assert.True(plan.DocumentStopped);
        Assert.All(plan.Pages, e => Assert.Equal(PageLane.Stop, e.Lane));
    }
}
