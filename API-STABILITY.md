# Foliant API Stability

_Status: 0.5.0 review, the bridge to a 1.0 stability commitment._

This document defines Foliant's **public API contract**: what callers may depend on, what is an
internal implementation detail, and what guarantees 1.0 will make. It is the output of the 0.5.0
API stabilization review (workstream E of the 0.5.0 plan).

## Versioning policy (effective at 1.0)

Foliant will follow [Semantic Versioning](https://semver.org):

- **MAJOR** — a breaking change to any public type listed under "Stable public contract" below:
  removing or renaming a type/member, changing a method signature, reordering constructor
  parameters, or changing documented behavior in a way that breaks a conforming caller.
- **MINOR** — additive, backward-compatible change: a new type, a new `init` property on an options
  record, a new **optional** parameter appended to a method/constructor, a new default backend
  (only after its quality gate proves it).
- **PATCH** — bug fixes and internal changes with no public-surface effect.

Until 1.0, minor versions (0.x) may make breaking changes; this review is the last planned
surface-narrowing before the contract is frozen.

The **model files** Foliant downloads are versioned separately (see `ModelCatalog`); swapping a
model's weights is a MINOR change to the library, since the public types do not change.

## Stable public contract

### Entry point (recommended path)

`FoliantProcessor.CreateDefault(modelsDirectory, …)` and `CreateDefaultAsync(…)` are the blessed
way to build the pipeline. **Most callers should use only these.** The two optional enum arguments
(`TableBackend`, `ReadingOrderBackend`) select backends; their defaults flip only when a quality
gate proves a candidate.

### Core data types (`Foliant.Core`)

Inputs/outputs and options — the heart of the contract:

- `ProcessingOptions` (record, `init` properties — new options are added as new properties)
- `DocumentResult`, `PageResult`, `PageVerification`
- `Region`, `LayoutRegion`, `TextLine`
- `TableStructure`, `TableCell`, `TableExtraction`
- `TextLayerPage`, `PreprocessedPage`, `PageImage`
- `BoundingBox` (readonly record struct) — the geometry primitive used across the data types
- Enums: `RegionType`, `TextSource`, `TextLayerMode`

### Extension points (interfaces)

Implement these to swap a pipeline stage; Foliant commits to keeping them stable:

- `IDocumentProcessor` — top-level entry
- `IPageRenderer`, `ILayoutDetector`, `IOcrEngine`, `ITableExtractor`,
  `IReadingOrderAssembler`, `ITextLayerReader`, `IPagePreprocessor`
- `IPageImageTransform` — caller-supplied pre-processing / synthetic degradation
- `IScanResolutionEstimator` — effective-DPI estimate driving `PageResult.LowResolution`
- `IScanUpscaler` — pre-OCR upscaling seam (the ML super-resolution drop-in point)

### Default implementations (`Foliant.Pipeline`)

- `DocumentProcessor` (the assembled pipeline; see "Construction" below)
- `PdfPageRenderer`, `PdfTextLayerReader`, `PdfImageScanResolutionEstimator`,
  `DefaultPagePreprocessor`, `OrientationDetector`
- `XyCutReadingOrder`, `XyCutPlusPlusReadingOrder`
- `ClassicalScanUpscaler` — reference `IScanUpscaler` (Gate 8: net-negative, **not** wired by
  default; retained as the seam's reference impl)
- `ScanDegrader` — deterministic scan-degradation transforms for robustness measurement
- Enums: `TableBackend`, `ReadingOrderBackend`

### Backends (separate packages) and model management

- `DocLayoutNetDetector`, `PaddleOcrEngine`, `TextlineOrientationClassifier`,
  `TableTransformerExtractor`, `SlanetPlusExtractor`
- `ModelCache`, `ModelAsset`, `ModelCatalog`

## Construction

`DocumentProcessor`'s constructor takes the pipeline stages plus several optional components
(preprocessor, orientation detector, scan-resolution estimator, scan upscaler). This is the
**advanced** path; it is public and supported, but `FoliantProcessor.CreateDefault` is the
recommended entry point and the one most callers should use.

Stability commitment for the constructor: **new components are appended as optional parameters**
(a MINOR change). Existing parameters will not be reordered or removed without a MAJOR bump. If the
parameter list grows unwieldy, a builder/options overload may be **added** (additive, MINOR) rather
than changing the existing constructor.

## Design notes

- **`OrientationDetector` is a concrete class, not an interface — by design.** It is not a swappable
  model backend like layout/OCR/tables; it orchestrates the existing `IOcrEngine` via a
  cardinal-rotation confidence vote. Keeping it concrete avoids inventing a one-implementation
  interface. If a learned orientation classifier is ever added, an interface can be introduced then
  (additive).
- **`ScanDegrader` is public on purpose.** Beyond Foliant's own Gate 7/8 harness, it lets callers
  measure their own OCR robustness; it is pure and deterministic.

## Internalized in 0.5.0 (removed from the public surface)

These had leaked to `public` but are implementation details, not contract. They are now `internal`
(still visible to the test assembly). Reducing them now keeps the 1.0 surface honest:

- `MarkdownComposer` and `ComposedPage` — page composition is an internal step of
  `DocumentProcessor`; callers consume `PageResult`/`DocumentResult`, not the composer.
- `LineGrouping` — internal text-row clustering helper.
- `ExtractionVerifier` — internal self-verification; its results are surfaced via the public
  `PageVerification` on each `PageResult`.

## Not yet frozen / open for 1.0

- The `IScanUpscaler` contract may gain a richer shape if the ML super-resolution backend needs it
  (e.g. target-DPI hints); changes will be additive where possible.
- Form-field key-value extraction (planned for 1.0) will extend `Region` (e.g. an added form-field
  property or a companion type). It is not yet present; adding it will be an additive MINOR change.
