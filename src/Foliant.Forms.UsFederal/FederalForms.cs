namespace Foliant.Forms.UsFederal;

/// <summary>
/// Ready-made <see cref="FormProfile"/>s for U.S. federal Standard Forms (FAR Part 53). Hand the
/// ones you need — or <see cref="All"/> — to a <c>GeometricFormFieldExtractor</c>; it scores each
/// profile's label hits per page and applies the best match, so a mixed RFP package (solicitation +
/// amendments + attachments) routes every page to the right form automatically.
///
/// Deterministic and abstaining: a field that can't be anchored is simply absent, never a guessed
/// value. Profiles are public-domain form layouts; extend the pack as new forms are profiled.
/// </summary>
public static class FederalForms
{
    /// <summary>
    /// SF-33 — "Solicitation, Offer and Award" cover page: solicitation number, issue date,
    /// issuing-office code, info-call email, and the Table-of-Contents section checkboxes.
    /// </summary>
    public static FormProfile Sf33 { get; } = new("SF-33 solicitation", new[]
    {
        new FormFieldSpec("solicitation_number", "SOLICITATION NUMBER", FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("date_issued",         "DATE ISSUED",          FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("issued_by_code",      "ISSUED BY",            FieldKind.Text, ValueAnchor.Right),
        new FormFieldSpec("info_call_email",     "E-MAIL ADDRESS",       FieldKind.Text, ValueAnchor.Below),

        new FormFieldSpec("toc_A", "SOLICITATION/CONTRACT FORM",     FieldKind.Checkbox, ValueAnchor.Mark),
        new FormFieldSpec("toc_B", "SUPPLIES OR SERVICES AND PRICE", FieldKind.Checkbox, ValueAnchor.Mark),
        new FormFieldSpec("toc_C", "DESCRIPTION/SPECS",              FieldKind.Checkbox, ValueAnchor.Mark),
        new FormFieldSpec("toc_D", "PACKAGING AND MARKING",          FieldKind.Checkbox, ValueAnchor.Mark),
        new FormFieldSpec("toc_E", "INSPECTION AND ACCEPTANCE",      FieldKind.Checkbox, ValueAnchor.Mark),
        new FormFieldSpec("toc_F", "DELIVERIES OR PERFORMANCE",      FieldKind.Checkbox, ValueAnchor.Mark),
        new FormFieldSpec("toc_G", "CONTRACT ADMINISTRATION DATA",   FieldKind.Checkbox, ValueAnchor.Mark),
        new FormFieldSpec("toc_H", "SPECIAL CONTRACT REQUIREMENTS",  FieldKind.Checkbox, ValueAnchor.Mark),
        new FormFieldSpec("toc_I", "CONTRACT CLAUSES",               FieldKind.Checkbox, ValueAnchor.Mark),
        new FormFieldSpec("toc_J", "LIST OF ATTACHMENTS",            FieldKind.Checkbox, ValueAnchor.Mark),
        // toc_K wraps to two lines; anchor on the line carrying the checkbox.
        new FormFieldSpec("toc_K", "REPRESENTATIONS, CERTIFICATIONS", FieldKind.Checkbox, ValueAnchor.Mark),
        new FormFieldSpec("toc_L", "NOTICES TO OFFERORS",            FieldKind.Checkbox, ValueAnchor.Mark),
        new FormFieldSpec("toc_M", "EVALUATION FACTORS FOR AWARD",   FieldKind.Checkbox, ValueAnchor.Mark),
    });

    /// <summary>
    /// SF-30 — "Amendment of Solicitation / Modification of Contract": amendment number, effective
    /// date, issuing-office code, the amended solicitation number, and the "offers extended" box.
    /// </summary>
    public static FormProfile Sf30 { get; } = new("SF-30 amendment", new[]
    {
        new FormFieldSpec("amendment_number",      "AMENDMENT/MODIFICATION NO",   FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("effective_date",        "EFFECTIVE DATE",              FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("issued_by_code",        "ISSUED BY",                   FieldKind.Text, ValueAnchor.Right),
        new FormFieldSpec("amended_solicitation",  "AMENDMENT OF SOLICITATION NO", FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("offers_extended",       "IS EXTENDED",                 FieldKind.Checkbox, ValueAnchor.Mark),
    });

    /// <summary>
    /// SF-1449 — "Solicitation/Contract/Order for Commercial Products and Commercial Services":
    /// solicitation number, issue date, requisition number, offer due date, NAICS, size standard,
    /// and the set-aside box. The most common modern commercial solicitation.
    /// </summary>
    public static FormProfile Sf1449 { get; } = new("SF-1449 commercial", new[]
    {
        new FormFieldSpec("solicitation_number",     "SOLICITATION NUMBER",     FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("solicitation_issue_date", "SOLICITATION ISSUE DATE", FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("requisition_number",      "REQUISITION NO",          FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("offer_due_date",          "OFFER DUE DATE",          FieldKind.Text, ValueAnchor.RightThenBelow),
        new FormFieldSpec("naics",                   "NAICS",                   FieldKind.Text, ValueAnchor.Right),
        new FormFieldSpec("size_standard",           "SIZE STANDARD",           FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("set_aside",               "SET ASIDE",               FieldKind.Checkbox, ValueAnchor.Mark),
    });

    // ── Forms below are DRAFTS built from the published FAR form layouts (no instance was in the
    //    reference corpus to verify against). They are deterministic and abstaining, so a wrong
    //    label simply yields "missing", never a fabricated value. Validate + tune each against a
    //    real instance the first time it is ingested (the Gate-3 extraction log shows how it did).

    /// <summary>SF-18 — "Request for Quotations" (older RFQ form; modern commercial RFQs use SF-1449). DRAFT.</summary>
    public static FormProfile Sf18 { get; } = new("SF-18 RFQ", new[]
    {
        new FormFieldSpec("request_number",     "REQUEST NO",                  FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("date_issued",        "DATE ISSUED",                 FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("requisition_number", "REQUISITION/PURCHASE REQUEST", FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("issued_by",          "ISSUED BY",                   FieldKind.Text, ValueAnchor.Below),
    });

    /// <summary>SF-1442 — "Solicitation, Offer and Award (Construction, Alteration, or Repair)". DRAFT.</summary>
    public static FormProfile Sf1442 { get; } = new("SF-1442 construction", new[]
    {
        new FormFieldSpec("solicitation_number", "SOLICITATION NO",            FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("date_issued",         "DATE ISSUED",                FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("contract_number",     "CONTRACT NO",                FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("project_number",      "PROJECT NO",                 FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("issued_by",           "ISSUED BY",                  FieldKind.Text, ValueAnchor.Below),
    });

    /// <summary>SF-26 — "Award/Contract" (post-award). DRAFT.</summary>
    public static FormProfile Sf26 { get; } = new("SF-26 award", new[]
    {
        new FormFieldSpec("effective_date",     "EFFECTIVE DATE",             FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("requisition_number", "REQUISITION/PURCHASE REQUEST", FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("issued_by",          "ISSUED BY",                  FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("administered_by",    "ADMINISTERED BY",            FieldKind.Text, ValueAnchor.Below),
    });

    /// <summary>OF-347 — "Order for Supplies or Services" (civilian, post-award). DRAFT.</summary>
    public static FormProfile Of347 { get; } = new("OF-347 order", new[]
    {
        new FormFieldSpec("date_of_order",   "DATE OF ORDER",  FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("contract_number", "CONTRACT NO",    FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("order_number",    "ORDER NO",       FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("issuing_office",  "ISSUING OFFICE", FieldKind.Text, ValueAnchor.Below),
    });

    /// <summary>DD-1155 — "Order for Supplies or Services" (DoD: Army/Navy/Air Force, post-award). DRAFT.</summary>
    public static FormProfile Dd1155 { get; } = new("DD-1155 order", new[]
    {
        new FormFieldSpec("contract_number", "CONTRACT/PURCH. ORDER", FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("order_number",    "DELIVERY ORDER",        FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("date_of_order",   "DATE OF ORDER",         FieldKind.Text, ValueAnchor.Below),
        new FormFieldSpec("issued_by",       "ISSUED BY",             FieldKind.Text, ValueAnchor.Below),
    });

    /// <summary>
    /// Every profile in this pack — convenient to pass straight to the extractor (it auto-selects
    /// the best-matching profile per page). SF-33/SF-30/SF-1449 are validated against real instances;
    /// SF-18/SF-1442/SF-26/OF-347/DD-1155 are layout drafts pending first-instance validation.
    /// </summary>
    public static IReadOnlyList<FormProfile> All { get; } =
        new[] { Sf33, Sf30, Sf1449, Sf18, Sf1442, Sf26, Of347, Dd1155 };
}
