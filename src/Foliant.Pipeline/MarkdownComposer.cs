// Assembles ordered layout regions + text lines into Markdown and structured Regions.
// Invariants (validated across the Phase 0 corpus, enforced by ExtractionVerifier):
//  - every text line lands in the output, is page furniture, or is counted as lost (must be 0);
//  - page furniture (headers/footers) is excluded from Markdown but preserved as metadata.

using System.Text;

namespace Foliant.Pipeline;

/// <summary>Composed output for one page.</summary>
internal sealed record ComposedPage(
    string Markdown,
    IReadOnlyList<Region> Regions,
    IReadOnlyList<TextLine> PageFurniture);

internal sealed class MarkdownComposer
{
    private readonly IReadingOrderAssembler _readingOrder;
    private readonly ITableExtractor _tables;

    public MarkdownComposer(IReadingOrderAssembler readingOrder, ITableExtractor tables)
    {
        _readingOrder = readingOrder;
        _tables = tables;
    }

    public ComposedPage Compose(
        PageImage page, IReadOnlyList<LayoutRegion> rawRegions, IReadOnlyList<TextLine> lines,
        bool enumeratorReadingOrder = false)
    {
        var ordered = _readingOrder.Order(XyCutReadingOrder.SuppressDuplicates(rawRegions));
        var furniture = new List<TextLine>();
        var blocks = new List<(float Y, string Md, Region Region)>();

        // Claim pass: every line gets EXACTLY ONE owning region, so overlapping regions of
        // different labels can no longer emit the same text twice (e.g. a title claimed by
        // both a Caption and a Title region). Claim priority: Tables first (an overlapping
        // text region must never hollow out a table's cells), then Titles (headings beat
        // captions for shared text), then the rest in reading order.
        var owner = new Dictionary<TextLine, LayoutRegion>();
        foreach (var region in ordered.Where(r => r.Type == RegionType.Table)
                     .Concat(ordered.Where(r => r.Type == RegionType.Title))
                     .Concat(ordered.Where(r => r.Type is not RegionType.Table and not RegionType.Title)))
        foreach (var l in lines)
            if (!owner.ContainsKey(l) && region.Bounds.ContainsCenterOf(l.Bounds))
                owner[l] = region;

        // Enumerator-aware post-pass: now that text is attached, reorder regions that carry a clean
        // leading-number run (1,2,3,…) into numeric order. Strict guard inside; geometry is the
        // default and is left untouched unless a normally-ordered page is provably a numbered mosaic.
        if (enumeratorReadingOrder)
        {
            var linesByRegion = ordered.ToDictionary(
                r => r,
                r => lines.Where(l => owner.TryGetValue(l, out var o) && o == r).ToList());
            ordered = EnumeratorReadingOrder.Apply(ordered, linesByRegion);
        }

        foreach (var region in ordered)
        {
            var regionLines = lines
                .Where(l => owner.TryGetValue(l, out var o) && o == region)
                .ToList();

            if (region.Type == RegionType.PageFurniture)
            {
                furniture.AddRange(regionLines);
                continue;
            }

            string? md;
            TableStructure? table = null;
            switch (region.Type)
            {
                case RegionType.Title:
                    md = regionLines.Count > 0
                        ? "## " + string.Join(" ", regionLines.Select(l => l.Text))
                        : null;
                    break;

                case RegionType.Table:
                    // Pass only the lines this table OWNS — lines claimed by other regions
                    // must not be re-emitted inside the grid.
                    var extraction = _tables.Extract(page, region, regionLines);
                    if (TableGridFits(extraction, regionLines))
                    {
                        table = extraction.Structure;
                        md = RenderTable(extraction);
                    }
                    else
                    {
                        // Box-grid FORM block mis-detected as a table: the predicted grid ejects
                        // sentences that span its fake cell borders, which scrambles their order on
                        // linearization. When the grid fails to capture the region's text, fall back
                        // to flowing reading-order prose so spanning sentences stay intact and in order.
                        md = string.Join("\n",
                            LineGrouping.GroupIntoVisualLines(regionLines).Select(g => g.Text));
                    }
                    break;

                case RegionType.Caption:
                    md = regionLines.Count > 0
                        ? "*" + string.Join(" ", regionLines.Select(l => l.Text)) + "*"
                        : null;
                    break;

                default:
                    md = string.Join("\n",
                        LineGrouping.GroupIntoVisualLines(regionLines).Select(g => g.Text));
                    break;
            }

            if (string.IsNullOrWhiteSpace(md)) continue;
            blocks.Add((region.Bounds.Y1, md,
                new Region(region.Type, region.RawLabel, region.Bounds, md, table, region.Confidence)));
        }

        // Orphans: lines outside every region. Grouped into visual rows and inserted by Y —
        // text outside detected regions must never be lost.
        var orphans = lines.Where(l => !owner.ContainsKey(l)).ToList();
        foreach (var group in LineGrouping.GroupLines(orphans))
        {
            var bounds = group.Select(l => l.Bounds).Aggregate(BoundingBox.Union);
            string text = string.Join("  ", group.OrderBy(t => t.Bounds.X1).Select(t => t.Text));
            var region = new Region(RegionType.Text, "unassigned", bounds, text, null, 0f);

            int idx = blocks.FindIndex(b => b.Y > bounds.Y1);
            blocks.Insert(idx < 0 ? blocks.Count : idx, (bounds.Y1, text, region));
        }

        var md2 = new StringBuilder();
        foreach (var (_, text, _) in blocks) md2.AppendLine(text).AppendLine();

        return new ComposedPage(
            md2.ToString(),
            blocks.Select(b => b.Region).ToList(),
            furniture);
    }

    /// <summary>
    /// A real data table captures (almost) all of its region's text inside the predicted grid.
    /// A box-grid FORM block that the layout model mislabels as a table does not: TableTransformer
    /// imposes a grid whose fake cell borders cut across running sentences, ejecting the spanning
    /// text outside every cell. We treat the share of region text left OUTSIDE the grid as the
    /// fit signal — above the threshold, the "table" is really prose in a box, and gridding it
    /// scrambles meaning, so the caller renders it as flowing reading-order text instead.
    /// Conservative by design: real tables eject almost nothing and are never reclassified.
    /// </summary>
    internal const double MaxUnassignedTextFractionForTable = 0.25;

    internal static bool TableGridFits(TableExtraction extraction, IReadOnlyList<TextLine> regionLines)
    {
        // No grid at all → RenderTable already degrades to a paragraph; let it.
        if (extraction.Structure is not { } t || t.Cells.Count == 0) return true;

        // A 1-column "grid" carries no tabular information a paragraph wouldn't — and can't
        // hold tabular order — so prefer prose for it.
        if (t.ColumnCount <= 1) return false;

        double total = regionLines.Sum(l => (double)l.Text.Length);
        if (total <= 0) return true;
        double unassigned = extraction.UnassignedLines.Sum(l => (double)l.Text.Length);
        return unassigned / total <= MaxUnassignedTextFractionForTable;
    }

    /// <summary>Renders a table extraction as a Markdown table (or a paragraph fallback when
    /// no grid was found), always followed by any unassigned region lines.</summary>
    internal static string RenderTable(TableExtraction extraction)
    {
        var sb = new StringBuilder();

        if (extraction.Structure is { } t)
        {
            var byPos = t.Cells.ToDictionary(c => (c.Row, c.Column), c => c.Text);
            for (int r = 0; r < t.RowCount; r++)
            {
                sb.Append('|');
                for (int c = 0; c < t.ColumnCount; c++)
                {
                    byPos.TryGetValue((r, c), out var text);
                    sb.Append(' ').Append((text ?? "").Replace("|", "\\|")).Append(" |");
                }
                sb.AppendLine();
                if (r == 0)
                {
                    sb.Append('|');
                    for (int c = 0; c < t.ColumnCount; c++) sb.Append("---|");
                    sb.AppendLine();
                }
            }
        }

        // No text loss: lines inside the region but outside the predicted grid, grouped into
        // visual rows so same-row fragments stay in left-to-right order.
        if (extraction.UnassignedLines.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            foreach (var (_, _, text) in LineGrouping.GroupIntoVisualLines(extraction.UnassignedLines))
                sb.AppendLine(text);
        }

        return sb.ToString();
    }
}
