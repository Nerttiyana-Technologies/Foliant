// Stage 5 — duplicate suppression + recursive XY-cut reading order over layout regions.

namespace Foliant.Spike;

public static class ReadingOrder
{
    /// <summary>Drops lower-confidence regions heavily overlapping a kept same-class region.</summary>
    public static List<LayoutRegion> SuppressDuplicates(
        IReadOnlyList<LayoutRegion> regions, float overlapThreshold = 0.7f)
    {
        var kept = new List<LayoutRegion>();
        foreach (var r in regions.OrderByDescending(r => r.Confidence))
            if (!kept.Any(k => k.Label == r.Label && OverlapRatio(r, k) > overlapThreshold))
                kept.Add(r);
        return kept;
    }

    /// <summary>Recursive XY-cut: split on horizontal whitespace gaps first, then vertical.</summary>
    public static List<LayoutRegion> Order(IReadOnlyList<LayoutRegion> regions)
    {
        var result = new List<LayoutRegion>(regions.Count);
        Cut(regions.ToList(), result, horizontalFirst: true);
        return result;
    }

    private static void Cut(List<LayoutRegion> items, List<LayoutRegion> output, bool horizontalFirst)
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

        output.AddRange(items.OrderBy(r => r.Y1).ThenBy(r => r.X1));
    }

    private static List<List<LayoutRegion>> SplitByGaps(List<LayoutRegion> items, bool horizontal, float minGap = 12f)
    {
        var sorted = horizontal
            ? items.OrderBy(r => r.Y1).ToList()
            : items.OrderBy(r => r.X1).ToList();

        var bands = new List<List<LayoutRegion>> { new() { sorted[0] } };
        float maxEnd = horizontal ? sorted[0].Y2 : sorted[0].X2;

        foreach (var r in sorted.Skip(1))
        {
            float start = horizontal ? r.Y1 : r.X1;
            float end = horizontal ? r.Y2 : r.X2;
            if (start > maxEnd + minGap) bands.Add(new List<LayoutRegion>());
            bands[^1].Add(r);
            maxEnd = Math.Max(maxEnd, end);
        }
        return bands;
    }

    /// <summary>Clusters text lines sharing a baseline into visual rows (Y-overlap),
    /// orders left-to-right within each row, and returns rows top-to-bottom.</summary>
    public static List<(float Y, float X, string Text)> GroupIntoVisualLines(IEnumerable<TextLine> lines)
    {
        var groups = new List<List<TextLine>>();
        foreach (var l in lines.OrderBy(l => (l.Y1 + l.Y2) / 2))
        {
            float cy = (l.Y1 + l.Y2) / 2;
            if (groups.Count > 0)
            {
                var g = groups[^1];
                float gy = g.Average(t => (t.Y1 + t.Y2) / 2);
                float gh = g.Average(t => t.Y2 - t.Y1);
                if (Math.Abs(cy - gy) < 0.6f * gh) { g.Add(l); continue; }
            }
            groups.Add(new List<TextLine> { l });
        }

        return groups
            .Select(g => (
                g.Min(t => t.Y1),
                g.Min(t => t.X1),
                string.Join("  ", g.OrderBy(t => t.X1).Select(t => t.Text))))
            .ToList();
    }

    private static float OverlapRatio(LayoutRegion a, LayoutRegion b)
    {
        float ix = Math.Max(0, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1));
        float iy = Math.Max(0, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1));
        float inter = ix * iy;
        float minArea = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return minArea <= 0 ? 0 : inter / minArea;
    }
}
