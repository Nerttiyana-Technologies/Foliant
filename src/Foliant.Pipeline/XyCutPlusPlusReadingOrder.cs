// Reading order via XY-Cut++ (adapted from arXiv 2504.10258, "XY-Cut++: Advanced Layout
// Ordering via Hierarchical Mask Mechanism"). Pure geometry + region classes — no model,
// no license constraints (the learned alternative, LayoutReader on LayoutLMv3 weights, is
// CC-BY-NC-SA and unusable in a commercially published library).
//
// Three deviations from the paper, all deliberate:
//  1. We order layout REGIONS, not OCR spans, so the cross-layout mask uses region width
//     vs the β-scaled median width plus x-projection overlap with ≥2 other regions.
//  2. Masked elements are re-inserted as horizontal BAND SEPARATORS (everything whose
//     center is above the separator reads before it, everything below after) instead of
//     IoU-weighted matching — at region granularity the two coincide, and separators also
//     fix the mid-page full-width table case that pure re-insertion gets wrong.
//  3. The paper's density-driven cut-axis choice (τ_d) needs pixel densities that the
//     assembler doesn't have; the deterministic equivalent at region level is "cut the
//     axis with the widest whitespace gap first", which is what plain XY-cut should have
//     done all along (a 100 px column gutter beats a 20 px row gap; a form's 40 px row
//     band beats its 20 px cell gutters).

namespace Foliant.Pipeline;

public sealed class XyCutPlusPlusReadingOrder : IReadingOrderAssembler
{
    private readonly float _minGap;
    private readonly float _crossLayoutWidthFactor;

    /// <param name="minGap">
    /// Minimum whitespace gap (pixels at processing DPI) that separates bands; same meaning
    /// and default as <see cref="XyCutReadingOrder"/> (validated at 300 DPI).
    /// </param>
    /// <param name="crossLayoutWidthFactor">
    /// β from the paper: a Title/Table/Figure wider than β × median region width whose
    /// x-projection overlaps at least two other regions is treated as a cross-layout
    /// element (full-width header over columns, mid-page full-width table) and masked
    /// out of the cut, then re-inserted as a band separator. Paper value: 1.3.
    /// </param>
    public XyCutPlusPlusReadingOrder(float minGap = 12f, float crossLayoutWidthFactor = 1.3f)
    {
        _minGap = minGap;
        _crossLayoutWidthFactor = crossLayoutWidthFactor;
    }

    public IReadOnlyList<LayoutRegion> Order(IReadOnlyList<LayoutRegion> regions)
    {
        if (regions.Count <= 1) return regions.ToList();

        var masked = DetectCrossLayoutElements(regions);
        var flow = regions.Where(r => !masked.Contains(r)).ToList();

        // Degenerate page: everything is a separator — plain top-left order.
        if (flow.Count == 0)
            return regions.OrderBy(r => r.Bounds.Y1).ThenBy(r => r.Bounds.X1).ToList();

        // Separators split the flow into horizontal bands read in sequence:
        // band 0, separator 0, band 1, separator 1, … (bands may be empty).
        var separators = masked.OrderBy(m => m.Bounds.CenterY).ThenBy(m => m.Bounds.X1).ToList();
        var bands = new List<LayoutRegion>[separators.Count + 1];
        for (int i = 0; i < bands.Length; i++) bands[i] = new List<LayoutRegion>();
        foreach (var r in flow)
            bands[separators.Count(s => s.Bounds.CenterY < r.Bounds.CenterY)].Add(r);

        var result = new List<LayoutRegion>(regions.Count);
        for (int i = 0; i < bands.Length; i++)
        {
            Cut(bands[i], result);
            if (i < separators.Count) result.Add(separators[i]);
        }
        return result;
    }

    /// <summary>
    /// Cross-layout detection (paper §pre-mask, Eq. 1–2): semantically high-priority regions
    /// (Title/Table/Figure) wider than β × median width whose x-projection overlaps ≥2 other
    /// regions. The width threshold does the heavy lifting: in a single-column page the
    /// median IS the column width, so nothing fires and the cut degenerates gracefully.
    /// </summary>
    private HashSet<LayoutRegion> DetectCrossLayoutElements(IReadOnlyList<LayoutRegion> regions)
    {
        var masked = new HashSet<LayoutRegion>();
        if (regions.Count < 4) return masked;   // median is meaningless on tiny pages

        var widths = regions.Select(r => r.Bounds.Width).OrderBy(w => w).ToArray();
        float median = widths.Length % 2 == 1
            ? widths[widths.Length / 2]
            : (widths[widths.Length / 2 - 1] + widths[widths.Length / 2]) / 2f;
        float threshold = _crossLayoutWidthFactor * median;

        foreach (var r in regions)
        {
            if (r.Type is not (RegionType.Title or RegionType.Table or RegionType.Figure)) continue;
            if (r.Bounds.Width <= threshold) continue;

            int xOverlaps = regions.Count(o =>
                !ReferenceEquals(o, r) &&
                Math.Min(o.Bounds.X2, r.Bounds.X2) - Math.Max(o.Bounds.X1, r.Bounds.X1) > 0f);
            if (xOverlaps >= 2) masked.Add(r);
        }
        return masked;
    }

    private void Cut(List<LayoutRegion> items, List<LayoutRegion> output)
    {
        if (items.Count <= 1) { output.AddRange(items); return; }

        // Widest-gap axis selection: measure the largest whitespace gap on each axis and
        // cut the wider one first. This is what makes aligned two-column layouts read
        // column-major (the gutter is wider than any row gap) while dense forms still
        // read row-major (the row band is wider than the cell gutters).
        float hGap = WidestGap(items, horizontal: true);
        float vGap = WidestGap(items, horizontal: false);
        bool horizontalFirst = hGap >= vGap;

        var bands = SplitByGaps(items, horizontal: horizontalFirst);
        if (bands.Count > 1)
        {
            foreach (var band in bands) Cut(band, output);
            return;
        }

        bands = SplitByGaps(items, horizontal: !horizontalFirst);
        if (bands.Count > 1)
        {
            foreach (var band in bands) Cut(band, output);
            return;
        }

        output.AddRange(items.OrderBy(r => r.Bounds.Y1).ThenBy(r => r.Bounds.X1));
    }

    /// <summary>Largest whitespace gap (≥ minGap, else 0) in the items' projection onto an axis.</summary>
    private float WidestGap(List<LayoutRegion> items, bool horizontal)
    {
        var sorted = horizontal
            ? items.OrderBy(r => r.Bounds.Y1).ToList()
            : items.OrderBy(r => r.Bounds.X1).ToList();

        float widest = 0f;
        float maxEnd = horizontal ? sorted[0].Bounds.Y2 : sorted[0].Bounds.X2;
        foreach (var r in sorted.Skip(1))
        {
            float start = horizontal ? r.Bounds.Y1 : r.Bounds.X1;
            float end = horizontal ? r.Bounds.Y2 : r.Bounds.X2;
            if (start - maxEnd > widest) widest = start - maxEnd;
            maxEnd = Math.Max(maxEnd, end);
        }
        return widest >= _minGap ? widest : 0f;
    }

    private List<List<LayoutRegion>> SplitByGaps(List<LayoutRegion> items, bool horizontal)
    {
        var sorted = horizontal
            ? items.OrderBy(r => r.Bounds.Y1).ToList()
            : items.OrderBy(r => r.Bounds.X1).ToList();

        var bands = new List<List<LayoutRegion>> { new() { sorted[0] } };
        float maxEnd = horizontal ? sorted[0].Bounds.Y2 : sorted[0].Bounds.X2;

        foreach (var r in sorted.Skip(1))
        {
            float start = horizontal ? r.Bounds.Y1 : r.Bounds.X1;
            float end = horizontal ? r.Bounds.Y2 : r.Bounds.X2;
            if (start > maxEnd + _minGap) bands.Add(new List<LayoutRegion>());
            bands[^1].Add(r);
            maxEnd = Math.Max(maxEnd, end);
        }
        return bands;
    }
}
