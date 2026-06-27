namespace Foliant.Orchestration;

/// <summary>
/// The deterministic per-page routing decision — the ADR-0003 decision table in code. Pure and
/// ZeroDep-free, so it is fully unit-testable without a PDF or the engine. Bias: <b>when in doubt,
/// escalate</b> — a page wrongly kept in the fast lane is under-extracted (the dangerous error); an
/// over-escalated page is merely slower.
/// </summary>
public static class RoutingPolicy
{
    /// <summary>Decide the lane for one page.</summary>
    public static (PageLane Lane, string Reason) Decide(PageRoutingInput page, OrchestrationOptions options)
    {
        switch (page.Kind)
        {
            case PageKind.Unprocessable:
                return (PageLane.Stop, "document integrity/decrypt failure");

            // Fast-lane candidates: structurally answerable without pixels — but only when ZeroDep is
            // confident. Low confidence escalates (never under-extract).
            case PageKind.FormPage:
            case PageKind.DigitalText:
            case PageKind.Empty:
                return page.Confidence < options.MinFastLaneConfidence
                    ? (PageLane.Heavy,
                        $"{page.Kind} but low confidence {page.Confidence:0.00} < {options.MinFastLaneConfidence:0.00} — escalate")
                    : (PageLane.Fast, $"{page.Kind} answered structurally (confidence {page.Confidence:0.00})");

            // Table/complex: escalate by default, but reclaim low-ruling pages to the fast lane when the
            // operator opts in (ADR-0003 probe — ruling-line count separates genuine tables from over-fire).
            // The page keeps its TableOrComplexLayout kind (honest), but the builder composes its text as
            // prose. Threshold 0 (default) never reclaims → identical to escalate-all.
            case PageKind.TableOrComplexLayout:
                return page.RulingLineCount < options.TableRulingLineThreshold
                    ? (PageLane.Fast,
                        $"table/complex reclaimed as text (ruling {page.RulingLineCount} < {options.TableRulingLineThreshold})")
                    : (PageLane.Heavy, $"TableOrComplexLayout needs render/ML (ruling {page.RulingLineCount})");

            // Everything else that needs pixels.
            case PageKind.ScannedImageOnly:
            case PageKind.ScannedWithOcr:
            case PageKind.Mixed:
                return (PageLane.Heavy, $"{page.Kind} needs render/ML");

            default:
                // Unknown/future class — escalate by default (safer than fast-laning something unmodelled).
                return (PageLane.Heavy, $"unhandled kind {page.Kind} — escalate by default");
        }
    }

    /// <summary>
    /// Build the full <see cref="PageRoutingPlan"/> from the per-page inputs and options, applying the
    /// whole-document escalation shortcut. A document with any <see cref="PageKind.Unprocessable"/> page is
    /// stopped as a whole.
    /// </summary>
    public static PageRoutingPlan BuildPlan(
        IReadOnlyList<PageRoutingInput> pages, OrchestrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(options);

        if (pages.Any(p => p.Kind == PageKind.Unprocessable))
        {
            var stopped = pages
                .Select(p => new PageRoutingPlanEntry(
                    p.PageNumber, PageLane.Stop, p.Kind, p.Confidence, "document stopped (integrity/decrypt)"))
                .ToList();
            return new PageRoutingPlan(stopped, DocumentStopped: true, WholeDocumentEscalated: false);
        }

        var entries = pages
            .Select(p =>
            {
                var (lane, reason) = Decide(p, options);
                return new PageRoutingPlanEntry(p.PageNumber, lane, p.Kind, p.Confidence, reason);
            })
            .ToList();

        // Whole-document shortcut: if the heavy share meets the threshold, the per-page split is not worth
        // it — run the whole document through the heavy lane (rewrite every non-stopped entry to Heavy).
        double heavyShare = entries.Count == 0
            ? 0d
            : (double)entries.Count(e => e.Lane == PageLane.Heavy) / entries.Count;

        if (entries.Count > 0 && heavyShare >= options.WholeDocumentEscalationThreshold)
        {
            var allHeavy = entries
                .Select(e => e with
                {
                    Lane = PageLane.Heavy,
                    Reason = $"whole-document escalation (heavy share {heavyShare:0.00} ≥ {options.WholeDocumentEscalationThreshold:0.00})",
                })
                .ToList();
            return new PageRoutingPlan(allHeavy, DocumentStopped: false, WholeDocumentEscalated: true);
        }

        return new PageRoutingPlan(entries, DocumentStopped: false, WholeDocumentEscalated: false);
    }
}
