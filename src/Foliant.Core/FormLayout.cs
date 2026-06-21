namespace Foliant;

/// <summary>A rectangle expressed as fractions of the page (0..1), top-left origin. Resolution- and
/// DPI-independent, so a template's coordinates match the same form at any scan size or render DPI.</summary>
public readonly record struct NormalizedRect(float X1, float Y1, float X2, float Y2)
{
    public float CenterX => (X1 + X2) / 2f;
    public float CenterY => (Y1 + Y2) / 2f;

    /// <summary>True when the center of <paramref name="other"/> falls inside this rect (with optional padding).</summary>
    public bool ContainsCenterOf(NormalizedRect other, float pad = 0f) =>
        other.CenterX >= X1 - pad && other.CenterX <= X2 + pad &&
        other.CenterY >= Y1 - pad && other.CenterY <= Y2 + pad;
}

/// <summary>What a templated element is.</summary>
public enum FormElementKind
{
    /// <summary>A free-text value field.</summary>
    Text,

    /// <summary>A checkbox/selection; its checked state is read at runtime from the matching widget.</summary>
    Checkbox,
}

/// <summary>
/// One known element of a form template: a fixed location on the page and what it MEANS. The semantic
/// <see cref="Label"/> is the whole point — it removes runtime geometric guessing, so a checkbox in a dense
/// block (e.g. 27b ARE NOT ATTACHED) is bound correctly because the template already says what that position
/// is. <see cref="Group"/> ties related elements together (e.g. all of block 27b's checkboxes).
/// </summary>
public sealed record FormElement(
    FormElementKind Kind,
    int Page,
    NormalizedRect Rect,
    string Label,
    string? Group = null);

/// <summary>
/// A learned layout for one fixed-layout form (federal Standard Form, or a customer-registered template):
/// its known fields and checkboxes at fixed normalized positions, plus a <see cref="Fingerprint"/> used to
/// recognize that an upload IS this form before applying the layout. Built once from a blank template; the
/// form never changes, so the layout is permanent.
/// </summary>
public sealed record FormLayout(
    string TemplateId,
    string Name,
    IReadOnlyList<FormElement> Elements,
    string? Fingerprint = null);
