namespace Foliant;

/// <summary>Controls use of the PDF's embedded text layer.</summary>
public enum TextLayerMode
{
    /// <summary>
    /// Use the text layer when the page has at least <see cref="ProcessingOptions.MinTextLayerWords"/>
    /// words; otherwise OCR. The right default for mixed corpora.
    /// </summary>
    Auto = 0,

    /// <summary>Use the text layer whenever any words exist; OCR only on pages with none.</summary>
    Always,

    /// <summary>Never use the text layer; OCR every page (it is still used for verification).</summary>
    Never,
}

/// <summary>Options for document processing.</summary>
public sealed record ProcessingOptions
{
    public static ProcessingOptions Default { get; } = new();

    /// <summary>Render resolution. 300 DPI is the quality/speed sweet spot from the Phase 0 spike.</summary>
    public int Dpi { get; init; } = 300;

    public TextLayerMode TextLayer { get; init; } = TextLayerMode.Auto;

    /// <summary>
    /// In <see cref="TextLayerMode.Auto"/>, pages with fewer text-layer words than this are
    /// treated as scanned and routed to OCR (guards against stamp-only text layers).
    /// </summary>
    public int MinTextLayerWords { get; init; } = 5;

    /// <summary>
    /// In <see cref="TextLayerMode.Auto"/>, pages whose text layer lost more than this
    /// fraction of its characters to unusable word geometry (see
    /// <see cref="TextLayerPage.DroppedCharFraction"/>) are treated as having an
    /// untrustworthy layer and routed to OCR. Guards against old PDFs with non-embedded
    /// fonts whose words exist in the layer but carry degenerate boxes (the "formmsd"
    /// class found in corpus verification: page recall 4% while the fast path reported
    /// a healthy word count). 0.3 keeps normal pages (fraction ~0) on the fast path
    /// while firing decisively on the broken class (fraction &gt; 0.9 observed).
    /// </summary>
    public float MaxTextLayerDroppedCharFraction { get; init; } = 0.3f;

    /// <summary>
    /// In <see cref="TextLayerMode.Auto"/>, pages whose text layer is more than this fraction
    /// control/non-printable characters (see <see cref="TextLayerPage.UndecodableCharFraction"/>)
    /// are treated as having an undecodable layer and routed to OCR. Guards against subset CID
    /// fonts with no ToUnicode map (some "PDF optimizer" tools strip it), where the glyphs have
    /// valid geometry but extract as garbage control codes — observed on magazine corpora where
    /// affected pages scored 0% recall while the page renders perfectly. 0.2 fires decisively on
    /// the broken class (fraction &gt; 0.8 observed) while leaving normal pages (fraction ~0)
    /// on the fast path; a stray bullet glyph or two never trips it.
    /// </summary>
    public float MaxTextLayerUndecodableFraction { get; init; } = 0.2f;

    /// <summary>
    /// Compute per-page verification (coverage invariant + text-layer word recall).
    /// Cheap; leave on except in throughput-critical scenarios.
    /// </summary>
    public bool Verify { get; init; } = true;

    /// <summary>1-based page numbers to process; null processes all pages.</summary>
    public IReadOnlyCollection<int>? Pages { get; init; }

    /// <summary>
    /// Run image preprocessing (deskew, contrast, despeckle) on pages routed to OCR.
    /// Has no effect on text-layer fast-path pages, which never need it.
    /// </summary>
    public bool PreprocessScans { get; init; } = true;
}
