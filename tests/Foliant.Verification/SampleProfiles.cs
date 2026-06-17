// Form profiles for the verification corpus. A profile is domain knowledge — the label text and
// value geometry of a known form family — so it lives with the harness, not the library. This one
// describes the SF-33 "SOLICITATION, OFFER AND AWARD" cover page (the federal SIR solicitations the
// Gate 3 truth set labels). Field names match the truth so Gate 3 can score by name.

using Foliant;

namespace Foliant.Verification;

internal static class SampleProfiles
{
    /// <summary>SF-33 / SIR solicitation cover page (page 1).</summary>
    public static readonly FormProfile Sf33Solicitation = new("SF-33 solicitation", new[]
    {
        // Text fields. The numbered boxes carry their value BELOW the label; "ISSUED BY" is inline.
        new FormFieldSpec("solicitation_number", "SOLICITATION NUMBER", FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("date_issued",         "DATE ISSUED",          FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("issued_by_code",      "ISSUED BY",            FieldKind.Text, ValueAnchor.Right),
        new FormFieldSpec("info_call_email",     "E-MAIL ADDRESS",       FieldKind.Text, ValueAnchor.Below),
        // offer_due_date / offer_due_time are buried mid-sentence ("until 1700 ET local time
        // 09/18/2025") — no clean label anchor, so they are intentionally left to a future
        // sentence-pattern matcher rather than guessed (guessing was the lone Gate 3 fabrication).

        // Table-of-contents checkboxes: a mark glyph on the section's row. (Two-column TOC is a
        // known hard case for a row-level mark test — the scorecard will show where it strains.)
        new FormFieldSpec("toc_A", "SOLICITATION/CONTRACT FORM",         FieldKind.Checkbox, ValueAnchor.Mark),
        new FormFieldSpec("toc_B", "SUPPLIES OR SERVICES AND PRICE",     FieldKind.Checkbox, ValueAnchor.Mark),
        new FormFieldSpec("toc_C", "DESCRIPTION/SPECS",                  FieldKind.Checkbox, ValueAnchor.Mark),
        new FormFieldSpec("toc_D", "PACKAGING AND MARKING",              FieldKind.Checkbox, ValueAnchor.Mark),
        new FormFieldSpec("toc_E", "INSPECTION AND ACCEPTANCE",          FieldKind.Checkbox, ValueAnchor.Mark),
        new FormFieldSpec("toc_F", "DELIVERIES OR PERFORMANCE",          FieldKind.Checkbox, ValueAnchor.Mark),
        new FormFieldSpec("toc_G", "CONTRACT ADMINISTRATION DATA",       FieldKind.Checkbox, ValueAnchor.Mark),
        new FormFieldSpec("toc_H", "SPECIAL CONTRACT REQUIREMENTS",      FieldKind.Checkbox, ValueAnchor.Mark),
        new FormFieldSpec("toc_I", "CONTRACT CLAUSES",                   FieldKind.Checkbox, ValueAnchor.Mark),
        new FormFieldSpec("toc_J", "LIST OF ATTACHMENTS",               FieldKind.Checkbox, ValueAnchor.Mark),
        // toc_K's entry wraps to two lines ("REPRESENTATIONS, CERTIFICATIONS AND / OTHER STATEMENTS
        // OF OFFERORS") with the checkbox on the FIRST line — anchor there, not on the continuation.
        new FormFieldSpec("toc_K", "REPRESENTATIONS, CERTIFICATIONS",    FieldKind.Checkbox, ValueAnchor.Mark),
        new FormFieldSpec("toc_L", "NOTICES TO OFFERORS",               FieldKind.Checkbox, ValueAnchor.Mark),
        new FormFieldSpec("toc_M", "EVALUATION FACTORS FOR AWARD",       FieldKind.Checkbox, ValueAnchor.Mark),
    });
}
