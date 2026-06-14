# Changelog

## 0.5.0 — unreleased

### Added
- **Classical pre-OCR upscaling for low-resolution scans — `IScanUpscaler` +
  `ClassicalScanUpscaler` + `ProcessingOptions.UpscaleLowResolutionScans` (default off) +
  `LowResolutionUpscaleFactor` (default 2.0).** When enabled, pages flagged `LowResolution`
  are enlarged by a Catmull-Rom cubic resampler before orientation, preprocessing and OCR.
  This presents existing glyphs at a larger pixel scale; it does not invent detail absent from
  the source (that requires an ML super-resolution model, which the `IScanUpscaler` seam lets a
  backend supply later without pipeline changes). Default-off on purpose: the toggle flips only
  once the Gate scorecard proves a recall gain on the low-DPI corpora — the same
  proven-by-scorecard discipline as the table and reading-order backends. The advisory
  `EffectiveDpi`/`LowResolution` fields continue to describe the original source scan, not the
  upscaled raster. Wired into `FoliantProcessor.CreateDefault`; off in the bare constructor.
- **Low-resolution scan warning — `IScanResolutionEstimator` + `ProcessingOptions.MinScanDpi`
  (default 150) + `PageResult.EffectiveDpi` / `PageResult.LowResolution`.** OCR-routed pages now
  report the estimated *effective* source resolution of their scan and are flagged when it falls
  below `MinScanDpi`. Effective DPI is the native pixel size of the page's dominant scan image
  relative to its physical placement on the page (`samples / (points / 72)`), read via PdfPig —
  distinct from the render `Dpi`, which is a fixed rasterization target and carries no information
  about scan quality (a 120-DPI scan rendered at 300 DPI is upsampled mush). The estimator ignores
  images covering less than half the page (logos, stamps) so a small graphic never trips a false
  warning, and uses the limiting (smaller) of the horizontal/vertical DPI since the worse axis
  governs legibility. The flag is advisory: it never changes routing or suppresses output; the
  page's Markdown is still produced. The estimate runs only on OCR-routed pages — born-digital
  fast-path pages are never touched. Wired into `FoliantProcessor.CreateDefault`; the bare
  `DocumentProcessor` constructor leaves it off (null) by default.

## 0.4.0 — 2026-06-13 (scanned-document support)

### Changed
- **Self-verification no longer scores recall against an undecodable text layer.** When a page's
  text layer is rejected as undecodable garbage (subset CID fonts with no ToUnicode map — the
  same `UndecodableCharFraction` signal that routes the page to OCR), it is no longer used as
  recall ground truth: `PageVerification.RecallPercent` is `null` (page flagged for review)
  rather than a misleading ~0% computed against the corruption itself. The dropped-char
  (formmsd) class is unaffected — its word text is usually real.

### Added
- **`OrientationDetector` + `ProcessingOptions.DetectOrientation`** (default on) — coarse
  page-orientation detection and correction (0/90/180/270°) for pages routed to OCR, by an
  OCR-confidence vote: the page is OCR'd at each cardinal rotation on a downscaled thumbnail and
  scored by Σ(confidence × recognized-text-length); the winning rotation is applied to the full
  page before fine deskew and the main OCR pass. Two guards prevent bad flips: an upright bias
  (a rotation must clearly beat the 0° reading) and a minimum-signal floor (the winning
  orientation must recognize enough text to be trusted), so low-text illustration/plate pages
  are left upright rather than flipped on noise. Pure — reuses the existing OCR engine, no new
  model or license dependency. Measured boundary: on decorative front-matter (covers, blank
  endpapers, library-seal pages) OCR can hallucinate characters from repeating patterns and the
  page may be rotated spuriously — harmless, as these carry no document content; no genuine
  body-text page was misrotated across the real-scan validation set. Targets the largest gap Gate 7 measured (a 180° page recovers from ~3%
  word recall toward baseline; 90°/270° likewise). Costs four thumbnail OCR passes per
  OCR-routed page; never runs on text-layer fast-path pages.
- **`IPageImageTransform` + `ProcessingOptions.ImageTransform`** — an optional pure transform
  applied to each rendered page image immediately after rasterization, before text-layer
  extraction, layout detection and OCR. Lets a caller inject their own preprocessing without
  forking the processor, and is the seam the degradation harness uses. Default `null` (no-op);
  born-digital pages still take the fast path unless combined with `TextLayerMode.Never`.
- **`ScanDegrader`** (`Foliant.Pipeline`) — deterministic, scan-like degradations as
  `IPageImageTransform` factories: rotation (fine skew and coarse 90/180/270°), JPEG
  recompression, Gaussian noise, Gaussian blur, low-DPI downscale-and-restore, and contrast
  fade, plus `Compose`. Pure and reproducible (same page + params → same pixels), built on the
  existing SkiaSharp dependency. Exists to *measure* robustness, not improve it.
- **Gate 7 — degradation robustness** (`--gate7 <born-digital-dir> [--gate7-pages N]` in the
  verification harness). Needs no hand-labeling: it runs on born-digital pages whose embedded
  text layer is exact ground truth, applies the `ScanDegrader` matrix in forced-OCR mode, and
  scores word recall against the text layer. Emits a per-degradation ledger
  (`gate7-ledger.csv`) and a console table of average recall and drop-from-baseline. The drop
  is the measured cost of each artifact and the yardstick for the scanned-doc features landing
  next (orientation detection, dewarp, super-res). Ledger-first: informational, does not fail
  the build (thresholds to be set once real numbers exist).

## 0.3.0 — 2026-06-13

### Added
- **`XyCutPlusPlusReadingOrder`** — reading-order backend adapted from XY-Cut++
  (arXiv 2504.10258): cross-layout elements (full-width titles/tables/figures spanning
  multiple columns, detected via β×median-width threshold) are masked out of the cut and
  re-inserted as band separators, and the cut axis is chosen by widest whitespace gap
  instead of horizontal-first. Fixes the classic XY-cut failure where row-aligned column
  blocks read interleaved (L1, R1, L2, R2) instead of column-major. Pure geometry — no
  model download, no inference cost. Selectable via
  `FoliantProcessor.CreateDefault(dir, readingOrder: ReadingOrderBackend.XyCutPlusPlus)`
  or `--reading-order xycut|xycut++` in the verification harness. **Default as of this
  release** — proven on the Gate 6 truth set (31 hand-verified pages, ebooks + magazines):
  ebooks tau 0.967 (tie — masking rarely fires on clean book pages), magazines 0.962 vs
  0.886 with 6/7 vs 5/7 pages perfectly ordered; reference corpus regression unchanged
  (99.7% recall, 0 flags). Known measured boundary: numbered step-grid layouts order by
  step number, a semantic cue geometry cannot see (both backends 0.733 on that page).
  Replaces the KICKOFF's planned LayoutReader integration: the production LayoutReader
  model is fine-tuned from LayoutLMv3 weights licensed CC-BY-NC-SA (non-commercial),
  which a commercially published library cannot ship.

- **Gate 6 — reading-order correctness** (`--gate6 <truth-dir>` in the verification
  harness). Truth files are ordered text snippets per page (no geometry to label);
  the runner locates each snippet in the composed Markdown and scores Kendall's tau
  between truth order and output order. Built to A/B `--reading-order xycut` vs
  `xycut++` on the same truth set; the reading-order default flips only when the
  candidate wins. Truth covers ebooks, magazines, and two-page landscape spreads;
  numbered-mosaic pages (step grids, numbered tip boxes), whose reading order is
  semantic rather than geometric, are kept as documented boundary cases outside the
  scored average (see `truth-gate6/boundary-cases/README.md`).

- **Dynamic XFA forms are now detected and flagged** instead of silently emitting the
  Adobe placeholder. Some fillable forms (many FDA forms, several inspection reports)
  store their content in an XFA XML packet; a non-Adobe viewer — and the rasterizer, and
  therefore OCR — see only the "Please wait… If this message is not eventually replaced…"
  placeholder. Previously that placeholder flowed into the output as if it were document
  text. `PdfTextLayerReader.IsDynamicXfaPlaceholder` now detects it; the page is emitted
  with an explanatory `<!-- Foliant: dynamic XFA form … -->` marker, a structured
  `PageResult.Notice`, and no spurious content, and is flagged for review. The content
  itself is unrecoverable without an Adobe XFA engine (documented limitation).

### Fixed
- **Undecodable CID text layers now route to OCR** (the "magazine optimizer" class).
  Some PDFs (observed: consumer magazines re-saved through a "PDF optimization" tool)
  carry subset CID fonts with no usable ToUnicode map. Unlike the formmsd class, the
  glyphs have valid bounding boxes — so the geometry-based guard never fires — yet they
  extract as C0/C1 control codes (the `(cid:N)` fingerprint), producing control-character
  garbage at ~0% recall while the page renders perfectly. `PdfTextLayerReader` now also
  reports `TextLayerPage.UndecodableCharFraction`, and `TextLayerMode.Auto` routes pages
  above `ProcessingOptions.MaxTextLayerUndecodableFraction` (default 0.2) to OCR. Found by
  the Test-Data-12 magazine sweep, which this fix turns from a Gate 1 failure back to green.
- **Untrustworthy text layers now route to OCR** (the "formmsd" class). Old PDFs with
  non-embedded fonts (e.g. 1990s PageMaker output relying on viewer-substituted
  Times/Helvetica with `/Differences`-remapped encodings) yield text-layer words whose
  glyph metrics cannot be resolved — the words exist but their bounding boxes collapse,
  and the fast path silently discarded them while stray embedded-font fragments kept the
  page above the word-count threshold (observed: 4% page recall on a "healthy" text
  layer; the loss was invisible to the coverage invariant because the words died before
  line-forming). `PdfTextLayerReader` now counts characters lost to degenerate word
  geometry and exposes `TextLayerPage.DroppedCharFraction`; in `TextLayerMode.Auto`,
  pages above `ProcessingOptions.MaxTextLayerDroppedCharFraction` (default 0.3) are
  treated as scanned and routed to OCR. `TextLayerMode.Always` remains an explicit
  override and is unaffected.

### Verification
- Expanded to **1,681 documents / 40,355 pages across 14 corpora** (government & tax forms,
  federal RFP/solicitation packages, complex AcroForm/XFA forms, academic ebooks, consumer
  magazines, and newspapers including multi-column Devanagari). **Zero text loss on every
  page.** Reference corpus holds **99.7% word recall in forced-OCR mode** (the non-circular
  metric); reading order **0.967 τ on ebooks, 0.962 on magazines**; **48/48 form fields,
  zero fabrication**. Measured boundaries are documented, not hidden: optimizer-corrupted
  CID text layers, dynamic XFA forms, semantic numbered layouts, and multi-article newspaper
  reading order. Full ledger and per-gate methodology in the README.

## 0.2.0 — 2026-06-12

### Added
- **`Foliant.Tables.PaddleStructure`** — SLANet-plus table-structure backend (official
  PaddlePaddle ONNX export, Apache 2.0). Purpose-built for raster/screenshot tables;
  emits HTML-style structure with row/column spans. Selectable via
  `FoliantProcessor.CreateDefault(dir, TableBackend.PaddleStructure)`. The default
  remains TableTransformer + ruling-line analysis, which measured better on
  vector-born ruled forms (86.7% vs 71.4% avg cell correctness on the reference
  tables); revisit per-table-class routing when raster-table corpora exist.
- **Scanned-page preprocessing** — `DefaultPagePreprocessor` behind the new
  `IPagePreprocessor` interface: projection-profile deskew (±8°), percentile contrast
  normalization for faded scans, and despeckling. Deterministic, model-free,
  unit-tested with synthetic pages. Applies only to pages routed to OCR;
  born-digital fast-path pages are untouched. Opt out with
  `ProcessingOptions.PreprocessScans = false`.
- `--table-backend tt|slanet` switch in the verification harness for backend A/B runs.

### Fixed
- Duplicate text emission when overlapping layout regions of different labels claimed
  the same lines (e.g. a title emitted as both heading and caption). Lines now have
  exactly one owning region: tables claim first, then titles, then the rest in
  reading order — exactly-once emission is structural, like the no-text-loss invariant.

### Verification
- Reference corpus (federal RFP, 474 pages, forced-OCR mode): 99.7% average word
  recall, 100% of pages ≥95%, zero text loss.
- Generalization sweeps on three additional corpora (~1,470 pages: IRS forms,
  Indian ITR forms, USCIS forms, Air Force solicitation, synthetic scanned business
  forms): zero text loss, zero crashes; scanned-form structure extraction validated
  by inspection. Known limitations measured: non-Latin scripts (multilingual
  recognition is v1.x scope) and colored watermark overprint (suppression planned).

## 0.1.0 — 2026-06-12

Initial release: layout-aware PDF extraction pipeline (DocLayout-YOLO layout
detection, PaddleOCR v5 text detection/recognition with rotated-text handling,
TableTransformer + ruling-line table structure, XY-cut reading order), born-digital
text-layer fast path, per-page self-verification (coverage invariant + text-layer
word recall), model catalog with SHA-256-verified lazy download.
