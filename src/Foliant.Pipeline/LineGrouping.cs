namespace Foliant.Pipeline;

public static class LineGrouping
{
    /// <summary>Clusters text lines sharing a baseline into visual rows (Y-overlap), top-to-bottom.</summary>
    public static List<List<TextLine>> GroupLines(IEnumerable<TextLine> lines)
    {
        var groups = new List<List<TextLine>>();
        foreach (var l in lines.OrderBy(l => l.Bounds.CenterY))
        {
            float cy = l.Bounds.CenterY;
            if (groups.Count > 0)
            {
                var g = groups[^1];
                float gy = g.Average(t => t.Bounds.CenterY);
                float gh = g.Average(t => t.Bounds.Height);
                if (Math.Abs(cy - gy) < 0.6f * gh) { g.Add(l); continue; }
            }
            groups.Add(new List<TextLine> { l });
        }
        return groups;
    }

    /// <summary>Visual rows as (top Y, left X, text joined left-to-right), top-to-bottom.</summary>
    public static List<(float Y, float X, string Text)> GroupIntoVisualLines(IEnumerable<TextLine> lines) =>
        GroupLines(lines)
            .Select(g => (
                g.Min(t => t.Bounds.Y1),
                g.Min(t => t.Bounds.X1),
                string.Join("  ", g.OrderBy(t => t.Bounds.X1).Select(t => t.Text))))
            .ToList();
}
