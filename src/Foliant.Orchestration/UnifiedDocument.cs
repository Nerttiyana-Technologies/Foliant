namespace Foliant.Orchestration;

/// <summary>Which engine produced a piece of the unified result — carried on every page and chunk.</summary>
public enum ProducedBy
{
    /// <summary>ZeroDep structural fast lane (exact, model-free).</summary>
    ZeroDepFastLane,

    /// <summary>Foliant render + ML heavy lane.</summary>
    FoliantHeavyLane,
}

/// <summary>
/// A retrieval-ready chunk for the RAG/Q&amp;A feed (Phase 4 fills these). Skeleton only in Phase 0.
/// </summary>
/// <param name="PageNumber">1-based source page.</param>
/// <param name="Text">The chunk text.</param>
/// <param name="Bounds">Page-raster bounds, when known.</param>
/// <param name="Source">Which engine produced it.</param>
/// <param name="FieldName">Set when the chunk is a form-field-as-chunk; otherwise null.</param>
public sealed record DocumentChunk(
    int PageNumber,
    string Text,
    BoundingBox? Bounds,
    ProducedBy Source,
    string? FieldName = null);

/// <summary>
/// The unified output of the orchestrator: the existing Foliant <see cref="DocumentResult"/> plus the
/// routing manifest, retrieval chunks, and per-page provenance. Additive — Phase 2 fleshes out the contract;
/// in Phase 0 it simply wraps a <see cref="DocumentResult"/> so callers have a stable shape to target.
/// </summary>
/// <param name="Document">The page results + concatenated Markdown (unchanged Foliant shape).</param>
/// <param name="Plan">The routing manifest that produced this result (null when the fast lane was off).</param>
/// <param name="Chunks">Retrieval chunks (empty until Phase 4).</param>
/// <param name="Provenance">Per-page engine provenance, keyed by 1-based page number (empty until Phase 2).</param>
public sealed record UnifiedDocument(
    DocumentResult Document,
    PageRoutingPlan? Plan,
    IReadOnlyList<DocumentChunk> Chunks,
    IReadOnlyDictionary<int, ProducedBy> Provenance)
{
    /// <summary>Wrap a plain Foliant result (fast lane off / pass-through) with empty integration metadata.</summary>
    public static UnifiedDocument FromFoliantOnly(DocumentResult document) =>
        new(document, Plan: null, Chunks: Array.Empty<DocumentChunk>(),
            Provenance: new Dictionary<int, ProducedBy>());
}
