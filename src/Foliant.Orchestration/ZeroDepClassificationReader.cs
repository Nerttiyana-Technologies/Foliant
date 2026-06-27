using ZD = ZeroDep.Abstractions;

namespace Foliant.Orchestration;

/// <summary>
/// Maps ZeroDep's <see cref="ZD.DocumentAnalysis"/> to the orchestrator's per-page
/// <see cref="PageRoutingInput"/> vocabulary. This is the single file that traverses ZeroDep's analysis
/// shape; the class-enum binding lives in <see cref="PageKindMapping"/>.
/// </summary>
/// <remarks>
/// Two whole-document overrides are applied before the per-page mapping:
/// <list type="bullet">
///   <item><b>STOP:</b> a non-<see cref="ZD.DocumentStatus.Processed"/> document (integrity/decrypt
///   failure) yields all-<see cref="PageKind.Unprocessable"/> — the document is stopped, nothing is
///   processed (ADR-0003 precondition).</item>
///   <item><b>Dynamic XFA:</b> when the AcroForm carries an XFA packet, the visible pages are typically a
///   "Please wait…" placeholder and the real content is in the Adobe-only XFA stream ZeroDep cannot read.
///   These must never be fast-laned, so every page is forced to escalate (mapped to
///   <see cref="PageKind.Mixed"/>).</item>
/// </list>
/// </remarks>
public sealed class ZeroDepClassificationReader : IPageClassificationReader
{
    /// <inheritdoc />
    public IReadOnlyList<PageRoutingInput> Read(ZD.DocumentAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        // STOP — integrity/decrypt failure. Every page is unprocessable (whole-document stop).
        if (analysis.Status != ZD.DocumentStatus.Processed)
            return ForEveryPage(analysis, PageKind.Unprocessable, confidence: 1.0);

        // Dynamic XFA — never fast-lane; force escalation so Foliant handles the placeholder pages.
        if (analysis.Form.HasXfa)
            return analysis.Pages.Count > 0
                ? analysis.Pages.OrderBy(p => p.PageIndex)
                    .Select(p => new PageRoutingInput(p.PageIndex + 1, PageKind.Mixed, p.Confidence))
                    .ToList()
                : ForEveryPage(analysis, PageKind.Mixed, confidence: 1.0);

        // No per-page classification (shouldn't happen for a Processed schemaVersion >= 1.1 document) —
        // escalate the whole document rather than guess.
        if (analysis.Pages.Count == 0)
            return ForEveryPage(analysis, PageKind.Mixed, confidence: 1.0);

        return analysis.Pages
            .OrderBy(p => p.PageIndex)
            .Select(p => new PageRoutingInput(
                PageNumber: p.PageIndex + 1,            // ZeroDep PageIndex is 0-based; Foliant pages are 1-based
                Kind: PageKindMapping.FromZeroDep(p.Class),
                Confidence: p.Confidence,
                RulingLineCount: p.Signals.RulingLineCount))
            .ToList();
    }

    // Builds one routing input per page (1..PageCount) with a fixed kind — used for the whole-document
    // override cases where per-page classification is absent or must be ignored.
    private static IReadOnlyList<PageRoutingInput> ForEveryPage(
        ZD.DocumentAnalysis analysis, PageKind kind, double confidence)
    {
        int count = analysis.PageCount > 0
            ? analysis.PageCount
            : analysis.Pages.Count;   // fall back to the classified-page count if PageCount is unset

        var inputs = new List<PageRoutingInput>(count);
        for (int page = 1; page <= count; page++)
            inputs.Add(new PageRoutingInput(page, kind, confidence));
        return inputs;
    }
}
