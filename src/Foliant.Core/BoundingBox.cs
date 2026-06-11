namespace Foliant;

/// <summary>
/// Axis-aligned bounding box in page raster coordinates (pixels at the processing DPI).
/// Origin is top-left; Y grows downward.
/// </summary>
public readonly record struct BoundingBox(float X1, float Y1, float X2, float Y2)
{
    public float Width => X2 - X1;
    public float Height => Y2 - Y1;
    public float CenterX => (X1 + X2) / 2f;
    public float CenterY => (Y1 + Y2) / 2f;
    public float Area => Math.Max(0f, Width) * Math.Max(0f, Height);

    public bool Contains(float x, float y) => x >= X1 && x <= X2 && y >= Y1 && y <= Y2;

    /// <summary>True when the center point of <paramref name="other"/> lies inside this box.</summary>
    public bool ContainsCenterOf(BoundingBox other) => Contains(other.CenterX, other.CenterY);

    public static BoundingBox Union(BoundingBox a, BoundingBox b) => new(
        Math.Min(a.X1, b.X1), Math.Min(a.Y1, b.Y1),
        Math.Max(a.X2, b.X2), Math.Max(a.Y2, b.Y2));

    public static BoundingBox Intersect(BoundingBox a, BoundingBox b) => new(
        Math.Max(a.X1, b.X1), Math.Max(a.Y1, b.Y1),
        Math.Min(a.X2, b.X2), Math.Min(a.Y2, b.Y2));

    /// <summary>Intersection area divided by the smaller box's area (0 when either is empty).</summary>
    public static float IntersectionOverMinArea(BoundingBox a, BoundingBox b)
    {
        float ix = Math.Max(0f, Math.Min(a.X2, b.X2) - Math.Max(a.X1, b.X1));
        float iy = Math.Max(0f, Math.Min(a.Y2, b.Y2) - Math.Max(a.Y1, b.Y1));
        float minArea = Math.Min(a.Area, b.Area);
        return minArea <= 0f ? 0f : ix * iy / minArea;
    }
}
