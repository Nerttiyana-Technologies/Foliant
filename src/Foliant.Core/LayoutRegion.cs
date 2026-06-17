namespace Foliant;

/// <summary>A layout region proposed by an <see cref="ILayoutDetector"/>.</summary>
/// <param name="Type">Normalized semantic class.</param>
/// <param name="RawLabel">The backend's native label (e.g. DocStructBench "plain text", "abandon").</param>
/// <param name="Confidence">Detector confidence in [0,1].</param>
/// <param name="Bounds">Region bounds in page raster coordinates.</param>
public sealed record LayoutRegion(
    RegionType Type,
    string RawLabel,
    float Confidence,
    BoundingBox Bounds);
