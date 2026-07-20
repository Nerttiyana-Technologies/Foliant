namespace Foliant;

/// <summary>Category of a normalized hardware specification attribute.</summary>
public enum SpecCategory
{
    Processor, Memory, Storage, Graphics, Display, Network, PowerSupply,
    FormFactor, Motherboard, OperatingSystem, Warranty, Quantity, PartNumber, Other,
}

/// <summary>The kind of system a document is procuring, inferred from vocabulary.</summary>
public enum SystemKind { Unknown, Server, Desktop, Laptop, Workstation, Monitor, Component }

/// <summary>
/// One normalized attribute (e.g. Memory → "512 GB DDR4 ECC"). <paramref name="RawText"/> is the
/// source fragment it was recognized from (kept for provenance); <paramref name="Value"/> and
/// <paramref name="Unit"/> are the normalized capacity/frequency and its qualifier when one was parsed.
/// </summary>
public sealed record SpecAttribute(
    SpecCategory Category, string RawText, string? Value = null, string? Unit = null);

/// <summary>One procured line item / component (a CLIN row, a spec-sheet block, a SOW bullet).</summary>
public sealed record HardwareComponent(
    string Description,
    int? Quantity = null,
    string? PartNumber = null,
    string? UnitOfIssue = null,
    IReadOnlyList<SpecAttribute>? Attributes = null);

/// <summary>
/// Document-level hardware specification profile. Empty <see cref="Components"/> ⇒ nothing to append
/// (a document that describes no hardware, per the ADR-0006 G-precision gate).
/// </summary>
public sealed record HardwareSpecProfile(
    SystemKind SystemKind,
    IReadOnlyList<HardwareComponent> Components,
    IReadOnlyList<SpecAttribute> SystemAttributes,   // rolled-up (CPU/RAM/Storage/GPU/FormFactor…)
    double Confidence)
{
    /// <summary>An empty profile — the canonical "no hardware described" result.</summary>
    public static HardwareSpecProfile Empty { get; } = new(
        SystemKind.Unknown,
        Array.Empty<HardwareComponent>(),
        Array.Empty<SpecAttribute>(),
        0d);
}

/// <summary>
/// Document-level extractor: reads hardware specifications out of an already-composed document.
/// Runs post-composition so it can see both prose lines and extracted table grids (ADR-0006). A
/// no-op seam — unwired ⇒ <see cref="ProcessingOptions.ExtractHardwareSpecs"/> does nothing. Additive
/// only; it never mutates page Markdown, so it cannot regress recall or reading order. Mirrors
/// <see cref="IFormFieldExtractor"/> / <see cref="IPageTemplateRouter"/>, but document-level rather
/// than per-page, and deterministic-first so a learned/LLM backend can be swapped in behind it.
/// </summary>
public interface IHardwareSpecExtractor
{
    /// <param name="pages">The composed per-page results (Markdown, Regions, tables, lines).</param>
    /// <returns>
    /// The profile; <see cref="HardwareSpecProfile.Empty"/> (or any profile with no
    /// <see cref="HardwareSpecProfile.Components"/>) when the document describes no hardware.
    /// </returns>
    HardwareSpecProfile Extract(IReadOnlyList<PageResult> pages);
}
