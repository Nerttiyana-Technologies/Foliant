namespace Foliant.Orchestration;

/// <summary>
/// Options for the ZeroDep-first orchestrator. Defaults are conservative: the fast lane is <b>off</b>, so the
/// orchestrator behaves exactly like the existing Foliant pipeline until a caller opts in. This keeps the
/// integration additive and instantly reversible (ADR-0003): flip <see cref="UseZeroDepFastLane"/> off and
/// nothing changes vs. Foliant-only.
/// </summary>
public sealed record OrchestrationOptions
{
    /// <summary>
    /// Master switch for the ZeroDep structural fast lane. When <c>false</c> (default) every document is
    /// processed by the Foliant pipeline unchanged — zero behavioural change. When <c>true</c>, the
    /// plan-then-execute router fast-lanes the pages ZeroDep can answer and escalates the rest.
    /// </summary>
    public bool UseZeroDepFastLane { get; init; }

    /// <summary>
    /// If the page routing plan escalates at least this fraction of pages to the heavy lane, skip the
    /// per-page split and run the whole document through the Foliant pipeline (the per-page machinery only
    /// pays off on a genuine mix). Range [0,1]; default 0.80.
    /// </summary>
    public double WholeDocumentEscalationThreshold { get; init; } = 0.80;

    /// <summary>
    /// A fast-lane candidate page whose classification <c>Confidence</c> is below this is escalated instead
    /// (bias to escalate on doubt — never under-extract; ADR-0003). Range [0,1]; default 0.60.
    /// </summary>
    public double MinFastLaneConfidence { get; init; } = 0.60;

    /// <summary>
    /// Table reclaim policy (ADR-0003, informed by the table-probe). A <see cref="PageKind.TableOrComplexLayout"/>
    /// page whose <c>RulingLineCount</c> is <b>below</b> this threshold is treated as fast-laneable prose
    /// (ZeroDep text, no cell reconstruction) instead of escalating. The probe found ruling-line count cleanly
    /// separates genuine tables (high) from the hint over-firing on columnar prose (low).
    /// <para>Default <c>0</c> means "never reclaim" — every table-class page escalates, exactly as today
    /// (conservative, zero fidelity risk). Set e.g. <c>10</c> to reclaim low-ruling table pages to the fast
    /// lane. This is a deliberate fidelity-vs-throughput trade (a small fraction of low-ruling pages are real
    /// borderless tables that become flat text — fine for RAG, lossy for faithful cells), so it is opt-in and
    /// should be G1a-validated before being made a default.</para>
    /// </summary>
    public int TableRulingLineThreshold { get; init; }

    /// <summary>
    /// Fast-lane abstention floor (ADR-0003). A prose fast-lane page must decode at least this many words to
    /// be trusted; below it (combined with <see cref="MinRunsForTextTrust"/>) the page is escalated instead
    /// of emitting near-empty output. Default 5.
    /// </summary>
    public int MinFastLaneTextWords { get; init; } = 5;

    /// <summary>
    /// Abstention trigger (ADR-0003). Only a page ZeroDep reports as having at least this many text runs is
    /// eligible to abstain — i.e. the page <i>claims</i> text structure but decoded to almost nothing (an
    /// undecodable text layer: CID fonts with no <c>/ToUnicode</c>). Such a page is escalated so Foliant's
    /// OCR/trust-guard recovers it. Genuinely sparse pages (few runs, few words) are <b>not</b> escalated —
    /// there is nothing to recover. Default 10.
    /// </summary>
    public int MinRunsForTextTrust { get; init; } = 10;

    /// <summary>
    /// Text-decode trust floor (ADR-0003 / ZeroDep 2.1.0). A prose fast-lane page whose
    /// <c>PageSignals.TextDecodeConfidence</c> is below this is escalated — its text layer is a
    /// plausibly-but-wrongly decoded font (e.g. a symbolic CID font with no usable <c>/ToUnicode</c>), so the
    /// fast lane would emit confidently-wrong text. Unlike the table knob, this defaults <b>on</b> (0.5): it
    /// is a correctness guard, and ZeroDep's score is sharply bimodal, so 0.5 cleanly separates the
    /// low-trust mode from authoritative pages. Set to 0 to disable. Range [0,1].
    /// </summary>
    public double MinTextDecodeTrust { get; init; } = 0.5;

    /// <summary>
    /// Optional sink invoked with the <see cref="PageRoutingPlan"/> once per document, right after planning
    /// and before execution. This is the audit hook (ADR-0003: "log the manifest") and the seam the Phase-1
    /// eval harness / tests use to inspect routing decisions. Default null (no-op). Never throws into the
    /// pipeline — exceptions from the observer are swallowed.
    /// </summary>
    public Action<PageRoutingPlan>? OnPlan { get; init; }
}
