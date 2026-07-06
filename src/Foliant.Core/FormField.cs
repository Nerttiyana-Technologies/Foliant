namespace Foliant;

/// <summary>The kind of a form field's value.</summary>
public enum FieldKind
{
    /// <summary>A free-text value (name, date, number, address, …).</summary>
    Text,

    /// <summary>A boolean selection; <see cref="FormField.Value"/> is "checked" or "unchecked".</summary>
    Checkbox,
}

/// <summary>Where an extracted <see cref="FormField"/> came from.</summary>
public enum FormFieldSource
{
    /// <summary>The PDF's fillable AcroForm field dictionary — an exact value, no OCR involved.</summary>
    AcroForm,

    /// <summary>Geometric label→value association over a flattened/scanned form's recognized text.</summary>
    Geometry,

    /// <summary>
    /// A learned model (LiLT form-KV token classifier) over the page's recognized text. Confidence
    /// reflects the model's softmax score; low-confidence predictions are abstained, never guessed.
    /// </summary>
    Learned,
}

/// <summary>
/// A typed key-value pair extracted from a form: a named field and its value. Sourced either from
/// the PDF's fillable AcroForm dictionary (exact) or, on flattened/scanned forms that carry no form
/// dictionary, from geometric label→value association over the page's recognized text.
/// </summary>
/// <param name="Name">The field name — the AcroForm partial name, or the detected label text.</param>
/// <param name="Value">The field value; for <see cref="FieldKind.Checkbox"/> this is "checked"/"unchecked".</param>
/// <param name="Kind">Whether the field is free text or a checkbox.</param>
/// <param name="Bounds">
/// The value's bounds in page raster coordinates, or null when unknown (AcroForm values are not
/// geometry-located in this release).
/// </param>
/// <param name="Confidence">Extraction confidence in [0,1]; 1.0 for exact AcroForm values.</param>
/// <param name="Source">Whether the field came from the AcroForm dictionary or geometric detection.</param>
public sealed record FormField(
    string Name,
    string Value,
    FieldKind Kind,
    BoundingBox? Bounds = null,
    float Confidence = 1f,
    FormFieldSource Source = FormFieldSource.AcroForm);
