namespace Foliant.Orchestration;

/// <summary>The lane a page is routed to by the router (the "plan" half of plan-then-execute).</summary>
public enum PageLane
{
    /// <summary>Answered directly from ZeroDep's structural read — no render, no ML.</summary>
    Fast,

    /// <summary>Escalated to Foliant's render + ML pipeline.</summary>
    Heavy,

    /// <summary>Not processable (document integrity/decrypt failure); the whole document stops.</summary>
    Stop,
}

/// <summary>
/// The orchestrator's own page-content vocabulary, intentionally <b>decoupled</b> from ZeroDep's
/// <c>PageContentClass</c> so that a change in the engine's enum is absorbed by the adapter
/// (<see cref="IPageClassificationReader"/>) rather than rippling through routing policy. Mirrors the
/// classes in ADR-0003.
/// </summary>
public enum PageKind
{
    /// <summary>Positively blank (content stream paints nothing; no widgets, no images).</summary>
    Empty,

    /// <summary>Born-digital text, simple single-flow layout.</summary>
    DigitalText,

    /// <summary>AcroForm widgets on the page, with negligible non-widget content.</summary>
    FormPage,

    /// <summary>Table / complex multi-column layout (a routing hint — ZeroDep has runs, not cell grids).</summary>
    TableOrComplexLayout,

    /// <summary>Page-dominant image, no usable text layer.</summary>
    ScannedImageOnly,

    /// <summary>Page-dominant image with an OCR text layer.</summary>
    ScannedWithOcr,

    /// <summary>Two or more independent content modes on one page (e.g. form widgets + a printed table).</summary>
    Mixed,

    /// <summary>The document failed the integrity/decrypt gate — a whole-document stop condition.</summary>
    Unprocessable,
}
