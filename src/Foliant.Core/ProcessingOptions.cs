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

    /// <summary>
    /// Effective source resolution (DPI) below which an OCR-routed (scanned) page is flagged
    /// <see cref="PageResult.LowResolution"/>. The effective DPI is the native pixel size of the
    /// page's dominant scan image relative to its physical size on the page — not the render
    /// <see cref="Dpi"/>, which is a fixed rasterization target and carries no information about
    /// scan quality (a 120-DPI scan rendered at 300 DPI is upsampled mush). 150 DPI is the common
    /// OCR-quality floor; below it recognition accuracy degrades noticeably. The flag is advisory:
    /// it never changes routing or output, only surfaces low-confidence scans for caller review.
    /// </summary>
    public int MinScanDpi { get; init; } = 150;

    /// <summary>
    /// Upscale pages flagged <see cref="PageResult.LowResolution"/> with the pipeline's injected
    /// <see cref="IScanUpscaler"/> by <see cref="LowResolutionUpscaleFactor"/> before OCR. Off by
    /// default, and a no-op unless an upscaler is supplied: the default pipeline
    /// (<c>FoliantProcessor.CreateDefault</c>) wires <b>none</b>, because the Gate 8 ledger
    /// measured the classical upscaler as net-negative for OCR recall on low-DPI scans. The seam
    /// remains so an ML super-resolution backend can be injected and re-measured. No effect on
    /// text-layer fast-path pages or pages not flagged low-resolution.
    /// </summary>
    public bool UpscaleLowResolutionScans { get; init; } = false;

    /// <summary>
    /// Linear scale factor applied when <see cref="UpscaleLowResolutionScans"/> upscales a
    /// low-resolution page — and by the <see cref="RetryLowResolutionPages"/> retry ladder.
    /// 2.0 doubles each dimension. Values ≤ 1 disable the upscale.
    /// </summary>
    public float LowResolutionUpscaleFactor { get; init; } = 2.0f;

    /// <summary>
    /// Retry ladder for low-resolution pages whose OCR came back (near-)empty: when an OCR-routed
    /// page is flagged <see cref="PageResult.LowResolution"/> AND produced fewer than
    /// <see cref="LowResolutionRetryMinWords"/> words, the pixel stages (preprocess → OCR) are
    /// re-run on an enlarged raster — first with the wired <see cref="IScanUpscaler"/> ×
    /// <see cref="LowResolutionUpscaleFactor"/>, then (if still under the threshold) on a
    /// re-render at up to 600 DPI — keeping whichever attempt extracted the most words. On by
    /// default: unlike <see cref="UpscaleLowResolutionScans"/> (always-on upscaling, measured
    /// net-negative on pages OCR could already read), the retry only runs where the baseline
    /// produced ~nothing, so it can only add words; pages that never trigger are byte-identical.
    /// Unrecovered pages get <see cref="PageResult.NeedsReview"/> + <see cref="PageResult.Notice"/>.
    /// </summary>
    public bool RetryLowResolutionPages { get; init; } = true;

    /// <summary>
    /// Word-count threshold for the <see cref="RetryLowResolutionPages"/> trigger and for
    /// <see cref="PageResult.NeedsReview"/>: an OCR-routed page with fewer extracted words than
    /// this (and no text-layer truth vouching for it) is treated as a failed extraction.
    /// </summary>
    public int LowResolutionRetryMinWords { get; init; } = 3;

    /// <summary>
    /// Minimum OCR line confidence for a word to COUNT toward the retry trigger, the keep-better
    /// comparison, and <see cref="PageResult.NeedsReview"/>. On pathologically degraded scans an
    /// upscaler can hallucinate texture that OCR reads as garbage words — without this floor,
    /// those junk words would both win keep-better and mask the NeedsReview flag. Text-layer
    /// lines carry confidence 1.0 and are unaffected. Extraction output is NOT filtered — only
    /// the retry/honesty arithmetic is.
    /// </summary>
    public float LowResolutionRetryMinConfidence { get; init; } = 0.5f;

    /// <summary>
    /// Mixed pages: a born-digital page (healthy text layer → fast path) that also carries a
    /// page-covering EMBEDDED IMAGE — e.g. a scanned letter pasted into a proposal. The image's
    /// text exists only as pixels, so the fast path silently drops it while recall (scored
    /// against the same image-less text layer) still reports 100%. When on, such pages
    /// additionally run OCR on the rendered raster and merge in the lines that do not spatially
    /// overlap any text-layer line: born-digital text stays verbatim from the layer, the image's
    /// content is recovered, and the page carries an informational <see cref="PageResult.Notice"/>.
    /// On by default; costs one OCR pass only on pages that actually embed a page-covering image.
    /// </summary>
    public bool RecoverEmbeddedImageText { get; init; } = true;

    /// <summary>
    /// Minimum fraction (0..1) of a fast-path page's area that embedded raster images must cover
    /// before <see cref="RecoverEmbeddedImageText"/> probes it with OCR. 0.1 catches pasted
    /// letters AND table screenshots (a price table pasted into a proposal can cover under 20%
    /// of its page) while still ignoring logos, header rules and signature stamps (each under 1%
    /// is discounted entirely). Raise it if figure-heavy corpora over-trigger.
    /// </summary>
    public float MinEmbeddedImageCoverage { get; init; } = 0.1f;

    /// <summary>
    /// Scan banner-position lines for sensitivity markings — CUI per 32 CFR 2002 (control
    /// banner, CUI//category strings, "Controlled by:" designation indicator), legacy
    /// dissemination controls (FOUO / SBU / Law Enforcement Sensitive), and national-security
    /// classification banners (TOP SECRET / SECRET / CONFIDENTIAL). Detection is ADVISORY:
    /// extraction is never suppressed; marked pages report
    /// <see cref="PageResult.SensitivityMarking"/> and surface in
    /// <see cref="DocumentResult.SensitivityMarkedPages"/>, so a caller can warn its user or
    /// segregate controlled content before it flows into downstream systems. On by default
    /// (a cheap pattern scan over already-extracted lines).
    /// </summary>
    public bool DetectSensitivityMarkings { get; init; } = true;

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

    /// <summary>
    /// After geometric reading order, reorder regions that carry a clean leading-number sequence
    /// (1,2,3,…) into numeric order — fixes numbered mosaics (magazine quizzes, step grids) whose
    /// true order is the printed number, which geometry alone cannot see. Strict by design: it acts
    /// only on a complete consecutive run starting at 1 and otherwise leaves geometry untouched, so
    /// it cannot reorder a normally-ordered page. Off by default until the Gate 6 ledger proves the
    /// τ gain with no reference-corpus regression (proven-by-scorecard, like the reading-order backend).
    /// </summary>
    public bool EnumeratorReadingOrder { get; init; } = false;

    /// <summary>
    /// Extract typed key-value form fields (<see cref="PageResult.FormFields"/>) — from the PDF's
    /// fillable AcroForm dictionary when present, otherwise (a later release) by geometric
    /// label→value association on flattened/scanned forms. Off by default and a no-op unless an
    /// <see cref="IFormFieldExtractor"/> is wired into the pipeline. The default flips on once the
    /// Gate 3 extraction scorecard proves it.
    /// </summary>
    public bool ExtractFormFields { get; init; } = false;

    /// <summary>
    /// Route each page through the injected <see cref="IPageTemplateRouter"/>: a page recognized as a known
    /// form template (federal Standard Form or customer-registered) gets deterministic, label-bound fields
    /// (<see cref="PageResult.FormFields"/>) and an appended template-field Markdown section, instead of
    /// runtime geometric guessing. On by default, but a no-op unless a router is wired into the pipeline (the
    /// default <c>FoliantProcessor.CreateDefault</c> wires none). Additive only — unmatched pages and the base
    /// Markdown are untouched, so it cannot regress recall or reading order.
    /// </summary>
    public bool UseTemplateRouting { get; init; } = true;

    /// <summary>1-based page numbers to process; null processes all pages.</summary>
    public IReadOnlyCollection<int>? Pages { get; init; }

    /// <summary>
    /// Run image preprocessing (deskew, contrast, despeckle) on pages routed to OCR.
    /// Has no effect on text-layer fast-path pages, which never need it.
    /// </summary>
    public bool PreprocessScans { get; init; } = true;

    /// <summary>
    /// Detect and correct coarse page orientation (0/90/180/270°) on pages routed to OCR, by
    /// an OCR-confidence vote, before fine deskew and the main OCR pass. Fixes sideways and
    /// upside-down scans (Gate 7: a 180° page recovers from ~3% to near-baseline recall). Costs
    /// four thumbnail OCR passes per OCR-routed page; never runs on text-layer fast-path pages.
    /// </summary>
    public bool DetectOrientation { get; init; } = true;

    /// <summary>
    /// Optional transform applied to each rendered page image immediately after rasterization,
    /// before text-layer extraction, layout detection and OCR. Default <c>null</c> (no-op).
    /// Lets a caller inject their own preprocessing, and lets the verification harness inject
    /// synthetic degradations for robustness measurement (Gate 7). Because it runs before the
    /// text-layer decision, a born-digital page still takes the fast path unless the transform
    /// is combined with <see cref="TextLayerMode.Never"/>.
    /// </summary>
    public IPageImageTransform? ImageTransform { get; init; }

    /// <summary>
    /// Optional per-page progress sink. When set, the processor reports a <see cref="ProcessingProgress"/>
    /// after each page completes (in page order), so callers — e.g. a UI progress bar — can show real
    /// progress that reaches 100% as the last page finishes, instead of a time/page-count estimate.
    /// Default <c>null</c> (no reporting). Use <c>new Progress&lt;ProcessingProgress&gt;(...)</c> to
    /// auto-marshal callbacks to the UI thread. Added in 1.1.0; additive and non-breaking.
    /// </summary>
    public IProgress<ProcessingProgress>? Progress { get; init; }
}

/// <summary>Per-page progress, reported after each page completes (see <see cref="ProcessingOptions.Progress"/>).</summary>
/// <param name="TotalPages">Total pages being processed (respects <see cref="ProcessingOptions.Pages"/>).</param>
/// <param name="CompletedPages">Pages completed so far (1..TotalPages).</param>
/// <param name="CurrentPage">The 1-based page number that just completed.</param>
public sealed record ProcessingProgress(int TotalPages, int CompletedPages, int CurrentPage)
{
    /// <summary>Completion fraction in [0,1] (CompletedPages / TotalPages); 0 when TotalPages is 0.</summary>
    public double Fraction => TotalPages > 0 ? (double)CompletedPages / TotalPages : 0d;
}
