# Changelog

## Unreleased

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
  candidate wins.

### Fixed
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
