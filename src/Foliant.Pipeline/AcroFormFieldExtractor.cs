// Form-field extraction from a PDF's fillable AcroForm dictionary — exact values, no OCR.
//
// This is the AcroForm half of IFormFieldExtractor: when a PDF carries a real fillable form, its
// field names and values are right there in the document and need no inference. Flattened/scanned
// forms (most of the federal-solicitation corpus) carry no AcroForm, so this returns nothing for
// them and the geometric fallback (a later increment) takes over. The two compose behind the
// IFormFieldExtractor seam without changing PageResult.

using UglyToad.PdfPig;
using UglyToad.PdfPig.AcroForms.Fields;

namespace Foliant.Pipeline;

public sealed class AcroFormFieldExtractor : IFormFieldExtractor
{
    public IReadOnlyList<FormField> Extract(
        byte[] pdf, int pageNumber, PageImage image, IReadOnlyList<TextLine> lines)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageNumber, 1);

        using var doc = PdfDocument.Open(pdf);
        if (!doc.TryGetForm(out var form)) return Array.Empty<FormField>();
        if (pageNumber > doc.NumberOfPages) return Array.Empty<FormField>();

        var fields = new List<FormField>();
        foreach (var field in form.GetFieldsForPage(pageNumber))
        {
            string name = field.Information.PartialName ?? string.Empty;
            switch (field)
            {
                case AcroTextField text:
                    if (!string.IsNullOrWhiteSpace(text.Value))
                        fields.Add(new FormField(
                            name, text.Value, FieldKind.Text, Bounds: null, Confidence: 1f,
                            Source: FormFieldSource.AcroForm));
                    break;

                case AcroCheckboxField box:
                    fields.Add(new FormField(
                        name, box.IsChecked ? "checked" : "unchecked", FieldKind.Checkbox,
                        Bounds: null, Confidence: 1f, Source: FormFieldSource.AcroForm));
                    break;

                // Other field kinds (choice lists, radio groups, push buttons) are out of scope for
                // this increment; the geometric path will not touch them either.
            }
        }
        return fields;
    }
}
