namespace Foliant;

/// <summary>Where a field's value sits relative to its label on the page.</summary>
public enum ValueAnchor
{
    /// <summary>The value is to the right of the label, on the same row.</summary>
    Right,

    /// <summary>The value is directly below the label.</summary>
    Below,

    /// <summary>Try right first, then below — the common case for boxed form labels.</summary>
    RightThenBelow,

    /// <summary>A checkbox mark sits on the label's row (use with <see cref="FieldKind.Checkbox"/>).</summary>
    Mark,
}

/// <summary>
/// One field in a <see cref="FormProfile"/>: the field's output name, the on-page label text to
/// locate it by, its kind, and where its value sits relative to the label.
/// </summary>
/// <param name="Name">The output field name (e.g. "solicitation_number").</param>
/// <param name="Label">The label text to locate on the page (matched as a normalized substring).</param>
/// <param name="Kind">Whether the field is free text or a checkbox.</param>
/// <param name="Anchor">Where the value sits relative to the located label.</param>
public sealed record FormFieldSpec(
    string Name,
    string Label,
    FieldKind Kind = FieldKind.Text,
    ValueAnchor Anchor = ValueAnchor.RightThenBelow);

/// <summary>
/// A deterministic, label-anchored description of a known form family (e.g. an SF-33 / SIR
/// solicitation cover page). The geometric extractor locates each spec's label on the page and
/// reads the associated value — no model, no training data, no license question. Profiles are
/// domain knowledge supplied by the caller; one profile per form family.
/// </summary>
/// <param name="Name">A human label for the profile (e.g. "SF-33 solicitation").</param>
/// <param name="Fields">The field specs to extract.</param>
public sealed record FormProfile(string Name, IReadOnlyList<FormFieldSpec> Fields);
