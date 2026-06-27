namespace Foliant.Orchestration;

/// <summary>
/// The engine-agnostic input to <see cref="RoutingPolicy"/> for a single page — produced by the ZeroDep
/// classification reader, consumed by the policy. Carries no ZeroDep types.
/// </summary>
/// <param name="PageNumber">1-based page number.</param>
/// <param name="Kind">The page's content class.</param>
/// <param name="Confidence">ZeroDep's classification confidence in [0,1].</param>
/// <param name="RulingLineCount">
/// ZeroDep's axis-aligned vector ruling-line count for the page — the table-vs-prose discriminator the
/// probe identified. Used only for the <see cref="PageKind.TableOrComplexLayout"/> reclaim policy
/// (<see cref="OrchestrationOptions.TableRulingLineThreshold"/>). Defaults to 0.
/// </param>
public readonly record struct PageRoutingInput(
    int PageNumber, PageKind Kind, double Confidence, int RulingLineCount = 0);

/// <summary>One page's routing decision within a <see cref="PageRoutingPlan"/>.</summary>
/// <param name="PageNumber">1-based page number.</param>
/// <param name="Lane">The lane this page was assigned to.</param>
/// <param name="Kind">The classified page kind that drove the decision.</param>
/// <param name="Confidence">The classification confidence in [0,1].</param>
/// <param name="Reason">Human-readable justification (for the audit log).</param>
public sealed record PageRoutingPlanEntry(
    int PageNumber,
    PageLane Lane,
    PageKind Kind,
    double Confidence,
    string Reason);

/// <summary>
/// The complete, inspectable routing manifest for a document, produced by the planning scan <b>before</b>
/// any extraction. It is the audit artifact of plan-then-execute: log it to see exactly why each page went
/// where it did, and use it to drive batched execution (all heavy pages in one Foliant invocation).
/// </summary>
/// <param name="Pages">Per-page decisions, in page order.</param>
/// <param name="DocumentStopped">True if the document failed integrity/decrypt — no page is processed.</param>
/// <param name="WholeDocumentEscalated">
/// True if the escalation share met <see cref="OrchestrationOptions.WholeDocumentEscalationThreshold"/> and
/// the per-page split was skipped in favour of running the whole document through the heavy lane.
/// </param>
public sealed record PageRoutingPlan(
    IReadOnlyList<PageRoutingPlanEntry> Pages,
    bool DocumentStopped,
    bool WholeDocumentEscalated)
{
    /// <summary>Pages assigned to the fast lane.</summary>
    public int FastLaneCount => Pages.Count(p => p.Lane == PageLane.Fast);

    /// <summary>Pages assigned to the heavy lane.</summary>
    public int HeavyLaneCount => Pages.Count(p => p.Lane == PageLane.Heavy);

    /// <summary>1-based page numbers routed to the heavy lane (the batched render set).</summary>
    public IReadOnlyList<int> HeavyPageNumbers =>
        Pages.Where(p => p.Lane == PageLane.Heavy).Select(p => p.PageNumber).ToList();

    /// <summary>1-based page numbers answered by the fast lane.</summary>
    public IReadOnlyList<int> FastPageNumbers =>
        Pages.Where(p => p.Lane == PageLane.Fast).Select(p => p.PageNumber).ToList();

    /// <summary>Fraction of pages escalated to the heavy lane, in [0,1]; 0 when there are no pages.</summary>
    public double EscalationShare => Pages.Count == 0 ? 0d : (double)HeavyLaneCount / Pages.Count;
}
