# Changelog

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
