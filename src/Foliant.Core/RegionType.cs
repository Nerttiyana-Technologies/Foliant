namespace Foliant;

/// <summary>Semantic class of a detected layout region.</summary>
public enum RegionType
{
    Unknown = 0,
    Text,
    Title,
    Table,
    Figure,
    Caption,
    Footnote,
    Formula,

    /// <summary>
    /// Running headers, footers, page numbers. Excluded from the main reading flow but
    /// preserved as page metadata — they often carry document identifiers
    /// (solicitation/amendment numbers) needed for provenance.
    /// </summary>
    PageFurniture,
}
