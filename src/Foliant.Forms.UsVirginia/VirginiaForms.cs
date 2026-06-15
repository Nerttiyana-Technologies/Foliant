namespace Foliant.Forms.UsVirginia;

/// <summary>
/// Ready-made <see cref="FormProfile"/>s for Commonwealth of Virginia (eVA) state solicitations.
///
/// Unlike federal FAR forms (one SF-33 across every agency), Virginia has no single rigid standard
/// form — each agency (VDACS, DSS, VDOT, the universities) uses its own template. They do, however,
/// share a recurring Commonwealth-of-Virginia RFP cover-page format with inline "Label: value"
/// fields (Issue Date, Title, Commodity Codes, Issuing Agency, Questions Due By, Period of
/// Contract), which is what <see cref="CommonwealthRfp"/> targets.
///
/// Built from a public VDACS RFP; validate + extend across more agencies as instances arrive.
/// Deterministic and abstaining — a field that can't be anchored is absent, never fabricated.
/// </summary>
public static class VirginiaForms
{
    /// <summary>
    /// Commonwealth of Virginia RFP cover page (eVA). Validated against two agencies' real
    /// solicitations (VDACS, DSS) — the labeled "Field: value" cover format these share.
    /// </summary>
    public static FormProfile CommonwealthRfp { get; } = new("Commonwealth of Virginia RFP", new[]
    {
        new FormFieldSpec("rfp_number",         "RFP NO",             FieldKind.Text, ValueAnchor.RightThenBelow),
        new FormFieldSpec("issue_date",         "ISSUE DATE",         FieldKind.Text, ValueAnchor.RightThenBelow),
        new FormFieldSpec("title",              "TITLE",              FieldKind.Text, ValueAnchor.RightThenBelow),
        new FormFieldSpec("commodity_codes",    "COMMODITY CODE",     FieldKind.Text, ValueAnchor.RightThenBelow),
        new FormFieldSpec("issuing_agency",     "ISSUING AGENCY",     FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("location",           "LOCATION",           FieldKind.Text, ValueAnchor.RightThenBelow),
        new FormFieldSpec("period_of_contract", "PERIOD OF CONTRACT", FieldKind.Text, ValueAnchor.RightThenBelow),
        new FormFieldSpec("questions_due",      "QUESTIONS DUE",      FieldKind.Text, ValueAnchor.RightThenBelow),
    });

    /// <summary>Every profile in this pack — pass straight to the extractor (it auto-selects per page).</summary>
    public static IReadOnlyList<FormProfile> All { get; } = new[] { CommonwealthRfp };
}
