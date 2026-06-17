// Duplicate suppression + recursive XY-cut reading order over layout regions.
// Ported unchanged from the Phase 0 spike.

namespace Foliant.Pipeline;

public sealed class XyCutReadingOrder : IReadingOrderAssembler
{
    private readonly float _minGap;

    /// <param name="minGap">
    /// Minimum whitespace gap (pixels at processing DPI) that separates bands. The default
    /// was validated at 300 DPI; scale it if you process at a very different resolution.
    /// </param>
    public XyCutReadingOrder(float minGap = 12f) => _minGap = minGap;

    /// <summary>Recursive XY-cut: split on horizontal whitespace gaps first, then vertical.</summary>
    public IReadOnlyList<LayoutRegion> Order(IReadOnlyList<LayoutRegion> regions)
    {
        var result = new List<LayoutRegion>(regions.Count);
        Cut(regions.ToList(), result, horizontalFirst: true);
        return result;
    }

    /// <summary>Drops lower-confidence regions heavily overlapping a kept same-class region.</summary>
    public static List<LayoutRegion> SuppressDuplicates(
        IReadOnlyList<LayoutRegion> regions, float overlapThreshold = 0.7f)
    {
        var kept = new List<LayoutRegion>();
        foreach (var r in regions.OrderByDescending(r => r.Confidence))
            if (!kept.Any(k => k.RawLabel == r.RawLabel &&
                               BoundingBox.IntersectionOverMinArea(r.Bounds, k.Bounds) > overlapThreshold))
                kept.Add(r);
        return kept;
    }

    private void Cut(List<LayoutRegion> items, List<LayoutRegion> output, bool horizontalFirst)
    {
        if (items.Count <= 1) { output.AddRange(items); return; }

        var bands = SplitByGaps(items, horizontal: horizontalFirst);
        if (bands.Count > 1)
        {
            foreach (var band in bands) Cut(band, output, !horizontalFirst);
            return;
        }

        bands = SplitByGaps(items, horizontal: !horizontalFirst);
        if (bands.Count > 1)
        {
            foreach (var band in bands) Cut(band, output, horizontalFirst);
            return;
        }

        output.AddRange(items.OrderBy(r => r.Bounds.Y1).ThenBy(r => r.Bounds.X1));
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
