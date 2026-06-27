namespace Foliant.Orchestration;

/// <summary>
/// The fast-lane abstention rule (ADR-0003), as a single shared decision so the live orchestrator and the
/// verification harness apply identical logic. A prose fast-lane page is escalated to the heavy lane when
/// either:
/// <list type="number">
///   <item><b>low decode trust</b> — ZeroDep's <c>TextDecodeConfidence</c> is below
///   <see cref="OrchestrationOptions.MinTextDecodeTrust"/> (a plausibly-but-wrongly decoded font; the fast
///   lane would emit confidently-wrong text), or</item>
///   <item><b>empty-but-structured</b> — it decoded fewer than
///   <see cref="OrchestrationOptions.MinFastLaneTextWords"/> words while ZeroDep reported at least
///   <see cref="OrchestrationOptions.MinRunsForTextTrust"/> text runs (an undecodable layer that emitted
///   nothing).</item>
/// </list>
/// Form pages are trusted via their AcroForm field values (sourced from the form dictionary, not glyph
/// decode), so they abstain only when they produced neither fields nor text. Genuinely sparse pages (few
/// runs, full trust) never abstain.
/// </summary>
public static class FastLaneAbstention
{
    /// <param name="kind">The page's routed kind.</param>
    /// <param name="structureRuns">ZeroDep's reported text-run count for the page (the structure signal).</param>
    /// <param name="textDecodeConfidence">ZeroDep's per-page <c>TextDecodeConfidence</c> in [0,1].</param>
    /// <param name="fastPage">The fast-lane page the builder produced.</param>
    /// <param name="options">Orchestration thresholds.</param>
    public static bool ShouldAbstain(
        PageKind kind, int structureRuns, double textDecodeConfidence, PageResult fastPage, OrchestrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(fastPage);
        ArgumentNullException.ThrowIfNull(options);

        // Form fields come from the AcroForm dictionary, not glyph decode, so the trust signal doesn't apply;
        // abstain only if the page produced neither fields nor any text.
        if (kind == PageKind.FormPage)
            return (fastPage.FormFields is null || fastPage.FormFields.Count == 0)
                   && fastPage.Verification.TruthWords < options.MinFastLaneTextWords;

        // (1) Plausibly-but-wrongly decoded text → escalate so the heavy lane recovers it via OCR.
        if (textDecodeConfidence < options.MinTextDecodeTrust)
            return true;

        // (2) Claims text structure but decoded to ~nothing (undecodable layer that emitted empty).
        return fastPage.Verification.TruthWords < options.MinFastLaneTextWords
               && structureRuns >= options.MinRunsForTextTrust;
    }
}
