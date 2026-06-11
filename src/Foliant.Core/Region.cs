namespace Foliant;

/// <summary>A processed output region: classified, bounded, with its extracted content.</summary>
public sealed record Region(
    RegionType Type,
    string RawLabel,
    BoundingBox Bounds,
    string Text,
    TableStructure? Table,
    float Confidence);
