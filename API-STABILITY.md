# Foliant API Stability

_Status: **1.0 — stable. This contract is now binding.**_

This document defines Foliant's **public API contract**: what callers may depend on, what is an
internal implementation detail, and the guarantees made from 1.0 onward. As of **1.0.0** the public
surface below is frozen under Semantic Versioning — the API has been proven across the 0.2–0.7
releases (Gates 1–8, 99.7% reference-corpus recall, 65k pages) and in production via FLUX.

## Versioning policy (in effect as of 1.0)

Foliant will follow [Semantic Versioning](https://semver.org):

- **MAJOR** — a breaking change to any public type listed under "Stable public contract" below:
  removing or renaming a type/member, changing a method signature, reordering constructor
  parameters, or changing documented behavior in a way that breaks a conforming caller.
- **MINOR** — additive, backward-compatible change: a new type, a new `init` property on an options
  record, a new **optional** parameter appended to a method/constructor, a new default backend
  (only after its quality gate proves it).
- **PATCH** — bug fixes and internal changes with no public-surface effect.

From 1.0.0 the contract below is frozen: breaking it requires a MAJOR (2.0) release. (Pre-1.0, the
0.x line made breaking changes freely; that period is over.)

The **model files** Foliant downloads are versioned separately (see `ModelCatalog`); swapping a
model's weights is a MINOR change to the library, since the public types do not change. Likewise,
the **form-profile companion packages** (`Foliant.Forms.*`) may add or refine `FormProfile`s as a
MINOR change — the profile *data* evolves; the profile *types* in core are frozen.

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
- Form fields (0.6/0.7): `FormField`, `FormProfile`, `FormFieldSpec`, plus `PageResult.FormFields`
- Enums: `RegionType`, `TextSource`, `TextLayerMode`, `FieldKind`, `FormFieldSource`, `ValueAnchor`

### Extension points (interfaces)

Implement these to swap a pipeline stage; Foliant commits to keeping them stable:

- `IDocumentProcessor` — top-level entry
- `IPageRenderer`, `ILayoutDetector`, `IOcrEngine`, `ITableExtractor`,
  `IReadingOrderAssembler`, `ITextLayerReader`, `IPagePreprocessor`
- `IPageImageTransform` — caller-supplied pre-processing / synthetic degradation
- `IScanResolutionEstimator` — effective-DPI estimate driving `PageResult.LowResolution`
- `IScanUpscaler` — pre-OCR upscaling seam (the GPU/ML super-resolution drop-in point)
- `IFormFieldExtractor` — typed key-value form-field extraction seam

### Default implementations (`Foliant.Pipeline`)

- `DocumentProcessor` (the assembled pipeline; see "Construction" below)
- `PdfPageRenderer`, `PdfTextLayerReader`, `PdfImageScanResolutionEstimator`,
  `DefaultPagePreprocessor`, `OrientationDetector`
- `XyCutReadingOrder`, `XyCutPlusPlusReadingOrder`
- `ClassicalScanUpscaler` — reference `IScanUpscaler` (Gate 8: net-negative, **not** wired by
  default; retained as the seam's reference impl)
- `ScanDegrader` — deterministic scan-degradation transforms for robustness measurement
- `AcroFormFieldExtractor`, `GeometricFormFieldExtractor`, `CompositeFormFieldExtractor`
- Enums: `TableBackend`, `ReadingOrderBackend`

### Backends and companion packages

- Backends: `DocLayoutNetDetector`, `PaddleOcrEngine`, `TextlineOrientationClassifier`,
  `TableTransformerExtractor`, `SlanetPlusExtractor`
- Model management: `ModelCache`, `ModelAsset`, `ModelCatalog`
- Form-profile packs (opt-in): `Foliant.Forms.UsFederal` (`FederalForms`),
  `Foliant.Forms.UsVirginia` (`VirginiaForms`) — the profile *types* are core-frozen; the profile
  *data* in these packs may grow as a MINOR change. New jurisdictions ship as new `Foliant.Forms.*`
  packages.

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

## Post-1.0 roadmap (all additive — MINOR, won't break the contract)

The frozen contract is designed to absorb the parked work without a MAJOR bump:

- **GPU super-resolution** — a GPU-backed `IScanUpscaler` injected via the existing seam;
  `ProcessingOptions.UpscaleLowResolutionScans` (the flag) and `PageResult.LowResolution` (the
  signal) already exist. Any new hints (e.g. target DPI) arrive as additive options.
- **ML form understanding** — a learned `IFormFieldExtractor` (LayoutLMv3/XFUND) for arbitrary
  forms beyond the deterministic profiles; same seam, no contract change.
- **Post-OCR language-model correction** and **multilingual OCR** — additive options/backends.
- **More form-profile packs** — new `Foliant.Forms.<jurisdiction>` packages; the core types stay
  frozen.
