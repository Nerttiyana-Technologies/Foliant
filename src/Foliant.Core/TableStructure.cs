namespace Foliant;

/// <summary>One cell of an extracted table grid.</summary>
public sealed record TableCell(
    int Row,
    int Column,
    string Text,
    BoundingBox Bounds);

/// <summary>Row/column grid extracted from a table region.</summary>
public sealed record TableStructure(
    int RowCount,
    int ColumnCount,
    IReadOnlyList<TableCell> Cells);

/// <summary>
/// Result of table-structure extraction for a single table region.
/// <paramref name="UnassignedLines"/> carries text lines inside the region that did not land in
/// any predicted cell — they must still be emitted by the composer (the no-text-loss invariant).
/// When <paramref name="Structure"/> is null the model found no usable grid; all region lines are
/// returned unassigned and the region degrades to a paragraph.
/// </summary>
public sealed record TableExtraction(
    TableStructure? Structure,
    IReadOnlyList<TextLine> UnassignedLines);
