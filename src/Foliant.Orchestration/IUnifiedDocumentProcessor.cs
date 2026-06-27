namespace Foliant.Orchestration;

/// <summary>
/// The Phase-2 unified-output seam (ADR-0003): processes a PDF and returns a <see cref="UnifiedDocument"/> —
/// the Foliant <see cref="DocumentResult"/> plus the routing plan, per-page engine provenance, and (Phase 4)
/// retrieval chunks. A consumer that only needs the plain result keeps using <see cref="IDocumentProcessor"/>;
/// one that wants provenance/plan/chunks (e.g. FoliantView) depends on this.
/// </summary>
public interface IUnifiedDocumentProcessor
{
    /// <summary>Process a PDF (bytes) into the unified result.</summary>
    Task<UnifiedDocument> ProcessUnifiedAsync(
        byte[] pdf, ProcessingOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Process a PDF (stream) into the unified result.</summary>
    Task<UnifiedDocument> ProcessUnifiedAsync(
        Stream pdf, ProcessingOptions? options = null, CancellationToken cancellationToken = default);
}
