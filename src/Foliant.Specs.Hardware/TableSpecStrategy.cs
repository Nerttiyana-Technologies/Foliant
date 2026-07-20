using System.Text.RegularExpressions;

namespace Foliant.Specs.Hardware;

/// <summary>
/// Table strategy (ADR-0006 §3.2 #1) — over composed <see cref="TableStructure"/> grids. Classifies
/// Qty / Description(or Component) / Part-Number / Unit columns by header keywords, then emits one
/// <see cref="HardwareComponent"/> per data row whose description carries hardware vocabulary. Covers
/// the RFQ CLIN table, the C07 Description/QTY blob, and the docx QTY/Component grid.
///
/// <para>Conservative (G-precision): a row is emitted only when its description column recognizes at
/// least one hardware attribute, so a clause list or a pricing grid with no hardware never becomes a
/// component.</para>
/// </summary>
internal static partial class TableSpecStrategy
{
    [GeneratedRegex(@"\b(qty|quantity)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QtyHeaderRx();

    [GeneratedRegex(@"\b(description|component|item|supplies|services|nomenclature|specification)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DescHeaderRx();

    [GeneratedRegex(@"\b(part\s*(no|number|#)?|model|nsn|p/?n|manufacturer)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PartHeaderRx();

    [GeneratedRegex(@"\b(unit|u/?i|unit\s*of\s*issue|uom|ea)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UnitHeaderRx();

    public static IEnumerable<HardwareComponent> Extract(IReadOnlyList<PageResult> pages)
    {
        foreach (var page in pages)
            foreach (var region in page.Regions)
                if (region.Table is { RowCount: >= 2 } table)
                    foreach (var component in FromTable(table))
                        yield return component;
    }

    private static IEnumerable<HardwareComponent> FromTable(TableStructure table)
    {
        // (row,col) → text lookup, plus per-row ordered cells.
        var byCell = table.Cells
            .GroupBy(c => c.Row)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Column).ToList());

        // The header is not always row 0 — real grids (e.g. C07) carry a blank spacer row above the
        // "Description | Part Number | Qty" header. Search the first few rows for the one that actually
        // carries column keywords; data rows are everything below it.
        int headerRow = -1;
        for (int r = 0; r < Math.Min(4, table.RowCount); r++)
            if (byCell.TryGetValue(r, out var cand) && cand.Any(c => IsColumnHeader(c.Text)))
            {
                headerRow = r;
                break;
            }

        int qtyCol = -1, descCol = -1, partCol = -1, unitCol = -1;
        if (headerRow >= 0)
            foreach (var cell in byCell[headerRow])
            {
                string h = cell.Text ?? "";
                if (descCol < 0 && DescHeaderRx().IsMatch(h)) descCol = cell.Column;
                else if (qtyCol < 0 && QtyHeaderRx().IsMatch(h)) qtyCol = cell.Column;
                else if (partCol < 0 && PartHeaderRx().IsMatch(h)) partCol = cell.Column;
                else if (unitCol < 0 && UnitHeaderRx().IsMatch(h)) unitCol = cell.Column;
            }

        // No description-like column → fall back to the widest column as the description carrier
        // (CLIN/blob tables whose header row is itself a data-ish row). Still guarded by the
        // per-row hardware-vocabulary check below, so non-hardware tables emit nothing.
        bool headerlessDesc = descCol < 0;

        for (int row = headerRow + 1; row < table.RowCount; row++)
        {
            if (!byCell.TryGetValue(row, out var cells)) continue;

            string Text(int col) => col >= 0
                ? cells.FirstOrDefault(c => c.Column == col)?.Text?.Trim() ?? ""
                : "";

            string description = headerlessDesc
                ? cells.OrderByDescending(c => (c.Text ?? "").Length).FirstOrDefault()?.Text?.Trim() ?? ""
                : Text(descCol);

            if (description.Length == 0) continue;
            var attributes = AttributeRecognizer.Recognize(description);
            if (attributes.Count == 0) continue;   // precision guard — no hardware here

            yield return new HardwareComponent(
                Description: Collapse(description),
                Quantity: ParseQty(Text(qtyCol)),
                PartNumber: NullIfEmpty(Text(partCol)),
                UnitOfIssue: NullIfEmpty(Text(unitCol)),
                Attributes: attributes);
        }
    }

    private static bool IsColumnHeader(string? text)
    {
        string h = text ?? "";
        return DescHeaderRx().IsMatch(h) || QtyHeaderRx().IsMatch(h)
            || PartHeaderRx().IsMatch(h) || UnitHeaderRx().IsMatch(h);
    }

    private static int? ParseQty(string text)
    {
        var m = Regex.Match(text, @"\b(\d{1,6})\b");
        return m.Success && int.TryParse(m.Groups[1].Value, out int q) ? q : (int?)null;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string Collapse(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
