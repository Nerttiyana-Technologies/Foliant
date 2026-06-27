using ZD = ZeroDep.Abstractions;

namespace Foliant.Orchestration;

/// <summary>
/// Default <see cref="ITypeAdapter"/>. Foliant types (<see cref="TextLine"/>, <see cref="FormField"/>,
/// <see cref="BoundingBox"/>, <see cref="TextSource"/>) are referenced unqualified via the enclosing
/// <c>Foliant</c> namespace; ZeroDep types are reached through the <c>ZD</c> alias to avoid the
/// same-name clashes (<c>BoundingBox</c>, <c>TextSource</c> exist in both).
/// </summary>
/// <remarks>
/// Coordinates: ZeroDep reports PDF device space (origin lower-left, Y up, points). Fast-lane pages are
/// <b>not rendered</b>, so there is no raster to map into; in Phase 1 the bounds are carried through as
/// advisory PDF-point boxes (no Y-flip, no DPI scaling). Normalising fast-lane and heavy-lane coordinates
/// into one space is a Phase-2 (unified-output) follow-up — it does not affect the Phase-1 correctness
/// gates (text recall, field EM, routing).
/// </remarks>
public sealed class ZeroDepTypeAdapter : ITypeAdapter
{
    /// <inheritdoc />
    public TextLine ToTextLine(ZD.TextRunInfo run)
    {
        ArgumentNullException.ThrowIfNull(run);

        // Advisory PDF-point bounds: origin (X,Y) is the run's baseline-left; height ≈ font size.
        var bounds = new BoundingBox(
            X1: (float)run.X,
            Y1: (float)run.Y,
            X2: (float)(run.X + run.Width),
            Y2: (float)(run.Y + run.FontSize));

        var source = run.Source == ZD.TextSource.OcrGenerated ? TextSource.Ocr : TextSource.TextLayer;

        return new TextLine(bounds, run.Text ?? string.Empty, (float)run.Confidence, source);
    }

    /// <inheritdoc />
    public FormField? ToFormField(ZD.FormFieldInfo field)
    {
        ArgumentNullException.ThrowIfNull(field);

        bool isButton = field.IsChecked.HasValue
            || string.Equals(field.FieldType, "Btn", StringComparison.Ordinal);

        if (isButton)
        {
            // Checkbox/radio: value is the on/off state (matches Foliant's "checked"/"unchecked" convention).
            string state = field.IsChecked == true ? "checked" : "unchecked";
            return new FormField(
                Name: FieldName(field),
                Value: state,
                Kind: FieldKind.Checkbox,
                Bounds: ToBoundsOrNull(field.Rect),
                Confidence: 1f,
                Source: FormFieldSource.AcroForm);
        }

        // Text/choice field. Skip when there is no value to carry (e.g. an unfilled signature field) — the
        // builder should not emit empty key-value pairs.
        if (string.IsNullOrWhiteSpace(field.Value))
            return null;

        return new FormField(
            Name: FieldName(field),
            Value: field.Value!.Trim(),
            Kind: FieldKind.Text,
            Bounds: ToBoundsOrNull(field.Rect),
            Confidence: 1f,
            Source: FormFieldSource.AcroForm);
    }

    // The field key. Prefer the fully-qualified AcroForm name (exact, stable); fall back to the partial
    // name, then the human label. (G1c reconciles this against Foliant's own AcroForm extractor naming.)
    private static string FieldName(ZD.FormFieldInfo field)
    {
        if (!string.IsNullOrWhiteSpace(field.FullyQualifiedName)) return field.FullyQualifiedName;
        if (!string.IsNullOrWhiteSpace(field.PartialName)) return field.PartialName!;
        if (!string.IsNullOrWhiteSpace(field.Label)) return field.Label!;
        return "(unnamed)";
    }

    private static BoundingBox? ToBoundsOrNull(ZD.BoundingBox? rect)
    {
        if (rect is not { } r) return null;
        // ZeroDep BoundingBox: (X, Y=bottom, Width, Height), PDF points. Advisory, as above.
        return new BoundingBox((float)r.X, (float)r.Y, (float)(r.X + r.Width), (float)(r.Y + r.Height));
    }
}
