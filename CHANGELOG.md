# Changelog

## 1.7.0 — 2026-07-11 (opt-in learned form key-value extraction)

Minor, **additive and non-breaking**. The default pipeline is unchanged; the learned arm is opt-in.

### Added
- **Learned form key-value extractor** (`Foliant.Forms.Lilt`, opt-in) — a LiLT token-classification
  arm (ADR-0001 Lever 2) for flattened/scanned federal forms where AcroForm/profile sources are
  absent. It runs behind the `IFormFieldExtractor` seam and **abstains rather than guesses** (spans
  below `MinConfidence` (0.65) are dropped; unpaired values surface only with `EmitUnpairedValues`).
  Enable it by composing after the exact-source extractors, which win wherever widget/profile values
  exist:
  ```csharp
  using var lilt = LiltFormFieldExtractor.Load("path/to/kv-model");   // bring your own weights
  var processor = FoliantProcessor.CreateDefault(
      formFields: new CompositeFormFieldExtractor(new AcroFormFieldExtractor(), lilt));
  ```
  **Measured** (TD-41 scanned holdout, rect-identity Gate 3): **70.1%** exact-value correct with
  cross-field fabrication **4/1,091**; the coverage invariants are unchanged on the 922-page promotion
  corpus (Gate 1 recall 100%, Gate 2 zero text loss, Gate 9 zero silent-empty).
- `LiltFormFieldExtractor.Load(modelDir, minConfidence)` — one-call construction returning an
  `IDisposable` extractor that owns its ONNX session.

### Notes
- **Weights are not bundled.** The learned arm is bring-your-own-weights in this release — point
  `Load` at a directory containing `model.onnx`, the tokenizer, and `config.json`. A published,
  fetchable model-catalog entry will follow in a later release.

## 1.6.0 — 2026-07-05 (sensitivity-marking detection + retry hardening)

Minor, **additive and non-breaking**.

### Added
- **CUI / sensitivity-marking detection** (`DetectSensitivityMarkings`, default **on**; advisory).
  Pages carrying CUI banner markings per 32 CFR 2002 (control banner, `CUI//category` strings,
  designation-indicator blocks, `REL TO` markings), legacy dissemination controls
  (FOUO / SBU / Law Enforcement Sensitive), DoDI 5230.24 distribution statements B–F, or
  national-security classification banners now report `PageResult.SensitivityMarking` (the
  matched banner text) and surface in `DocumentResult.SensitivityMarkedPages` — so controlled
  content never flows into downstream systems unnoticed. Detection is banner-position-aware
  (top/bottom bands + page furniture, uppercase-verbatim rules), so ordinary prose containing
  “confidential” does not flag. Extraction is never suppressed.
  **Measured** (synthetic marked corpus, ground truth): born-digital **100% of documents and
  pages** detected; physically-degraded scans at 72–120 DPI **99% of documents / 96% of pages**
  (banner text recovered through OCR); **zero false positives** across an 813-page unmarked-
  corpus sweep and precision-focused unit tests.
- Verification harness: per-page `sensitivity` scorecard column and corpus-level ⚠ warning;
  `--sample-pdfs N`/`--sample-seed` (seeded corpus subsets); recursive PDF discovery;
  `[i/N] + elapsed/ETA` progress on long runs; per-run error counts that make crashed pages
  impossible to miss; Gate 1 reports **N/A** (instead of a misleading FAIL) on corpora with no
  text-layer truth to score.

### Fixed
- **Low-resolution retry hardening.** The retry trigger, keep-better comparison, and
  `NeedsReview` now count only words at or above `LowResolutionRetryMinConfidence` (new option,
  default 0.5) — hallucinated texture read as confident-looking junk can no longer pose as a
  recovery or mask the honesty flag. Rung 2 no longer stacks the upscaler onto a re-render that
  already doubled the pixel budget (removes an overflow on large pages). Regression-verified:
  healthy corpora and real sub-150-DPI scans show zero new flags; the reference-corpus
  needs-review count is unchanged.
- In production-shape verification (a 1,000-document synthetic procurement corpus, 31,700
  pages), the 1.5.0 retry ladder recorded its first real recoveries: genuinely degraded
  110–120 DPI scan pages recovered via the 2× upscale and 600-DPI re-render rungs, with
  unrecoverable pages honestly flagged (56) rather than silent.

### Research note (measure-first)
- ML super-resolution (Real-ESRGAN-class, CUDA-validated) was evaluated for low-resolution
  recovery and measured **net-negative for document OCR** (word-recall Δ vs no upscale: +0.4 at
  150 DPI, −2.0 at 100 DPI, −12.3 at 72 DPI — photo-realism models hallucinate stroke texture
  that OCR misreads). It ships nowhere; the classical retry-only default stands. The experimental
  backend and its A/B harness remain in-tree for evaluating document-restoration models, which
  must beat the no-upscale baseline on the same ledger before earning a default.

### Verification
- Cumulative ledger now **3,704 documents / 98,652 pages / 21 corpora**, zero text loss
  (README carries the per-category table and measured throughput, including the corpus run
  above: 16.9 h single-core, 1.9 s/page, recall 100.0% average AND minimum on 26,952
  truth-bearing pages).

## 1.5.0 — 2026-07-03 (no more silent empty pages — recovery + honest flagging)

Minor, **additive and non-breaking**. Fixes two ways a page's visible content could vanish from the
output while every quality metric stayed green — reported in production use, where documents
containing such pages showed **recall 100%** with pages that emitted little or no text.

### Fixed
- **Mixed pages: embedded-image content recovered** (`RecoverEmbeddedImageText`, default **on**).
  A born-digital page with a healthy text layer can carry its real content as an embedded raster
  image — a scanned letter pasted into a proposal, a price table inserted as a screenshot. The
  fast path silently dropped that content, and recall — scored against the same image-less text
  layer — still reported 100%. Now, when embedded images cover ≥ `MinEmbeddedImageCoverage`
  (default 0.1) of a fast-path page, the rendered page is OCR'd once and lines not already covered
  by the text layer are merged in additively: layer text stays verbatim, image text (including
  table cell contents, which previously rendered as an empty grid) is recovered, and the page
  carries an informational `Notice`. If nothing can be recovered from a large embedded image, the
  page is flagged `NeedsReview` instead — never silent.
- **Low-resolution scans: retry ladder** (`RetryLowResolutionPages`, default **on**). An OCR-routed
  page flagged `LowResolution` that produced fewer than `LowResolutionRetryMinWords` (default 3)
  words is retried on an enlarged raster — first with the wired `IScanUpscaler` ×
  `LowResolutionUpscaleFactor`, then on a re-render at up to 600 DPI — keeping whichever attempt
  extracted the most words (ties keep the first pass, so a retry can never lose words). Distinct
  from the always-on `UpscaleLowResolutionScans` path, which stays off per the Gate 8 verdict:
  the retry runs only where the baseline produced ~nothing, so healthy pages are byte-identical.

### Added
- **Honest metrics** (all additive): `PageResult.NeedsReview` — true when a page is a failed or
  suspect extraction (empty OCR page with no text-layer truth, or unrecoverable embedded-image
  content); `PageResult.Notice` texts explaining each case, including the recovery notices;
  `DocumentResult.PagesNeedingReview` — page numbers callers MUST surface next to any recall
  aggregate. A document can no longer report 100% recall while silently missing content.
- `ProcessingOptions`: `RetryLowResolutionPages`, `LowResolutionRetryMinWords`,
  `RecoverEmbeddedImageText`, `MinEmbeddedImageCoverage`.
- `FoliantProcessor.CreateDefault` now wires `ClassicalScanUpscaler` by default (retry-only role;
  CPU-cheap, model-free). Hosts with a capable GPU should inject `OnnxSuperResolutionUpscaler`
  (`Foliant.ScanUpscale.SuperResolution`) instead — see the XML doc one-liner.
- Verification harness: **Gate 9 — no silent empty OCR page** (any OCR page with ~zero words must
  carry a Notice; needs-review count prints beside every recall summary), plus A/B switches
  (`--no-retry-ladder`, `--no-image-recovery`) and ADR-0004 ledger runners (`--lowres-repro`,
  `--wrap-scans`, `--scan-census`).

### Verification
- Mixed-page regression (production-shape pages: pasted letter at 45% coverage, table screenshot
  at 19.5%): letter paragraphs and all table cell values recovered into the output; both pages
  carry recovery notices; Gates 1/2/9 PASS.
- Synthetic low-DPI ladder (born-digital corpus degraded to image-only scans): recall degrades
  95.2% → 67.5% → 29.4% (72/50/40 DPI) with pages still word-bearing (no trigger, byte-identical
  path); at 30 DPI pages go empty and the machinery engages — every empty page either recovered
  via retry or flagged `NeedsReview`; zero silent empties.
- Real scans (categorized document-scan corpus, 300 pages at ~200–320 DPI + all 65 sub-150-DPI
  pages): zero empty pages, zero false notices — trigger and probe stay cold on healthy input.

## 1.4.0 — 2026-06-27 (ZeroDep-first orchestration — new `Foliant.Orchestration` package)

Minor, **additive and non-breaking**. Adds a new opt-in package, **`Foliant.Orchestration`**, that puts the
zero-dependency [ZeroDep](https://www.nuget.org/packages/ZeroDep) structural engine in front of the Foliant
pipeline as a **plan-then-execute router**: one ZeroDep scan classifies every page, born-digital text/form
pages are answered directly from ZeroDep (no render, no ML), and only the pages that need pixels escalate to
the full Foliant pipeline — then the per-page results merge back in original order. The default Foliant
pipeline is **unchanged**; the fast lane is **off by default** (`UseZeroDepFastLane = false`), so existing
callers get identical behaviour until they opt in. See `docs/ADR-0003`.

### Added
- **`Foliant.Orchestration`** package (`DocumentOrchestrator : IDocumentProcessor`). With the fast lane off
  it delegates verbatim to the wrapped pipeline (drop-in). With it on, it routes **per page** off ZeroDep's
  per-page classification, batches all escalated pages into a single pipeline call (models load once), and
  merges in page order.
  - **Table reclaim knob** (`TableRulingLineThreshold`, default 0 = escalate all tables) — opt-in fast-laning
    of low-ruling table-class pages as text, validated by ruling-line count.
  - **Safety guards (default on):** *abstention* — a page that decodes to almost nothing despite claiming
    text structure escalates instead of emitting empty; *decode-trust* — a page whose ZeroDep
    `TextDecodeConfidence` is low (plausibly-but-wrongly decoded font) escalates rather than emit wrong text.
  - **Unified output** — `IUnifiedDocumentProcessor.ProcessUnifiedAsync` returns a `UnifiedDocument`: the
    `DocumentResult` plus the routing plan and **per-page engine provenance** (fast vs heavy lane). Retrieval
    `chunks` are reserved (empty) for a future release.
- Consumes ZeroDep `2.1.1` (per-page classification, text-decode trust, inter-word spacing fidelity).

### Notes
- Validated on the real corpus: opt-in throughput rose to **~45%** of pages fast-laned overall (**~68%** on a
  born-digital SF-form workload), with fast-lane text fidelity **~99%** on that workload vs a clean text-layer
  reference. `Foliant.Core` is untouched; the cardinal rule holds — Foliant references ZeroDep, never the
  reverse.

## 1.3.3 — 2026-06-21 (fix: born-digital matched forms keep their clean grid body)

Bug fix. 1.3.x flattened the body of **every** template-matched page to reading-order prose
(`plainFormBody`), which was right for scanned/flattened forms (garbled OCR grid) but **wrong for
born-digital widget-matched forms**: it scattered each filled value away from its label (e.g. the
solicitation number rendered after the block 2–6 label run instead of in its `5. SOLICITATION NUMBER`
cell). Now `plainFormBody` applies **only to the scanned/by-identity path**; a born-digital widget match
keeps its `FederalFormTableRenderer` grid, so each value stays in its labelled cell. The appended
`### Form fields` section is unchanged (it's added regardless of body rendering). No API change.

## 1.3.2 — 2026-06-21 (ships the by-identity feature that 1.3.0/1.3.1 packages omitted)

Corrective release. The 1.3.0 and 1.3.1 **packages did not actually contain the by-identity routing** —
a release-branch merge dropped the wiring source (`IScannedFormRouter`, `TemplateRouter.TryRouteByDesignation`,
the `DocumentProcessor` fallback, `FormIdentifier.IdentifyRevisionYear`, the revision gate, and the SF-1449/
SF-30 `/TU` gold labels), so the published assemblies shipped only the orphaned extraction classes. 1.3.2
restores the complete, tested feature described in the 1.3.1 notes below. **1.3.0 and 1.3.1 are unlisted.**
No API changes beyond what 1.3.1 documented — `IScannedFormRouter` and `FormIdentifier.IdentifyRevisionYear`
now genuinely ship. Build + tests green.

## 1.3.1 — 2026-06-21 (scanned & flattened federal forms — extraction by printed identity)

Minor release. Extends template-aware extraction to **scanned and flattened federal forms that carry no
usable AcroForm widgets** — the case the 1.2.0 widget-matcher could not reach. Such a page is recognized by
its **printed form designation + GSA revision** and its values are read from the known template geometry
(checkbox state from pixels, text from OCR within each field's rect). **Additive and non-breaking** under
`API-STABILITY.md`: a new optional interface plus a federal-scoped pipeline fallback — the default pipeline
and the 1.2.0 widget path are unchanged.

### Added
- **By-identity routing for flattened/scanned federal forms.** New **`IScannedFormRouter`** (in
  `Foliant.Core`), implemented by `TemplateRouter`. When a page has no usable widget signature but prints a
  recognized Standard Form designation, Foliant binds it to the bundled template **of the same revision** and
  extracts deterministically — otherwise it abstains and the page falls back to the default pipeline.
  - **Form + revision is the trust key.** **`FormIdentifier.IdentifyRevisionYear`** reads the printed GSA
    revision (e.g. `REV. 11/2021`). A template's geometry is applied only on a page of the **same form and
    revision** — the same printed GSA layout even across agencies (only fillable-widget placement differs,
    which pixel/positional extraction ignores). A different revision abstains; an unreadable revision falls
    back to a strict layout-anchor check. This **generalizes across agencies**: a SEWP SF-1449 and an Air
    Force SF-1449 of the same revision both extract from one bundled template.
  - **OCR-free checkbox detection** (`CheckboxPixelDetector`): checkbox state from the dark-pixel fraction
    inside the known box — no model required.
  - **`ScannedFormExtractor`** reads each field from the OCR lines inside its known rect: every line is
    assigned to at most one field (nearest centre — no value copied across overlapping rects), printed-label
    echo is stripped, and junk marks (lone characters/punctuation) are dropped.
  - **`LayoutAnchorVerifier`** — the fallback layout guard used when the revision can't be read.

### Changed / Fixed
- **Cleaner matched-form body.** Matched federal pages now compose flowing prose instead of being forced into
  a grid table (`MarkdownComposer` plain-form-body), fixing scrambled/garbled body text on scanned amendments.
- **Gold field captions for SF-1449 and SF-30.** Text-field labels re-sourced from the blank forms'
  authoritative AcroForm `/TU` names, so values read with the right captions (e.g. `10. NAICS: 721214`,
  `10. SIZE STANDARD:`, `8. OFFER DUE DATE`, `7. … TELEPHONE NUMBER`) instead of mis-paired printed
  fragments. The set-aside / UCF **checkbox** gold labels from 1.2.0 are untouched.
- Re-kinded three SF-30 header/date elements (9B & 10B *DATED*, 11 *SOLICITATION AMENDED*) from checkbox to
  text so their values extract as text.

### Notes
- Resolves the SEWP VI (solicitation 80TECH24R0001) customer escalation on scanned SF-30 / SF-1449 copies,
  end to end. Verification gates pass (recall, zero text loss); a scanned page with no embedded text layer
  reports corpus-recall *n/a* by construction (no ground truth to score against) — not a regression.
- An experimental low-DPI super-resolution backend (`Foliant.ScanUpscale.SuperResolution`) ships in the
  solution and Gate-8 harness but is **not published to NuGet** (`IsPackable=false`) — Gate 8 measured
  upscaling net-negative for OCR. Parked pending the 2.0.0 learned-model work.

## 1.2.0 — 2026-06-21 (template-aware forms + bring-your-own-template library)

Minor release. Adds a **template-aware extraction** layer that binds fixed-layout forms to their *known*
geometry instead of guessing at runtime, plus a new **`Foliant.Templates`** package that lets consumers
register their **own** blank templates. **Additive and non-breaking** under `API-STABILITY.md` — the
default pipeline is unchanged unless a template router is wired in.

### Added
- **`Foliant.Templates` — new package (bring-your-own-template library).** Register a blank form once;
  Foliant learns its widget geometry, stores it (SQLite), and routes matching uploads to deterministic,
  label-bound extraction — falling back to the default pipeline for anything unrecognized.
  - **`FormLayout`** (in `Foliant.Core`): a form's fields/checkboxes at normalized (DPI-independent)
    positions, each with a semantic label, plus a layout fingerprint.
  - **`FormLayoutGenerator`** turns a blank PDF into a draft `FormLayout` (widget geometry + auto-paired
    labels, table column-label inheritance, overlapping-widget dedup).
  - **`FormMatcher`** fingerprints each page against the registered templates (conservative, biased to
    fallback — high precision, no false matches); **`TemplateExtractor`** reads each filled widget at its
    known position (`/V` text, `/AS` checkbox state) and emits the template's *known-correct* label.
  - **`TemplateRegistry`** merges bundled templates with a customer SQLite store (customer wins on id);
    **`TemplateRouter`** routes per page; **`TemplateLibrary`** is the productized façade
    (`Register` / `Update` / `Unregister` / `Router`) with a draft → review → commit label workflow.
- **12 bundled U.S. federal Standard Form templates** (SF-1449, SF-33, SF-1442, SF-30, SF-26, SF-18,
  SF-25/25A/25B, SF-1409/1410, SF-1413) ship as embedded resources — accurate out of the box, no setup.
- **Per-page template routing in the pipeline.** `DocumentProcessor` takes an optional
  `IPageTemplateRouter`; a recognized page gets deterministic `PageResult.FormFields` **and** an appended,
  label-bound Markdown section (e.g. `27b ADDENDA — ARE NOT ATTACHED`). Append-only — recall, reading
  order, and the base Markdown are untouched. Opt-in via `ProcessingOptions.UseTemplateRouting` (a no-op
  unless a router is wired; `FoliantProcessor.CreateDefault` wires none).
- **Checkbox state in the output.** Checked AcroForm/XFA boxes (state in the widget `/AS`, not the content
  stream) now emit `[X]`, so form selections survive into the Markdown instead of silently disappearing.
- **Federal-form schedule tables** render row-by-row (`FederalFormTableRenderer`), form-scoped behind
  printed-designation detection (`FormIdentifier`) — fixes multi-row line-item schedules collapsing into
  one row, with **zero blast radius** on the shared table path.

### Security
- **Pinned `SQLitePCLRaw.lib.e_sqlite3` to 3.50.3** (SQLite ≥ 3.50.2) to resolve **CVE-2025-6965**
  (high severity) pulled transitively by `Microsoft.Data.Sqlite`. Native-binary-only override; the managed
  layer is unchanged.

### Validation
- New `Foliant.Templates` test suite (generator, matcher, extractor, registry, router, library) all green;
  matcher precision validated on real forms (8/8 self-match, out-of-set form falls back on every page) and
  on a mixed multi-form package (each page → its own template or default).
- **Gate 1 recall 100% / Gate 2 zero text loss** with routing on; **Gate 6 reading-order avg τ 0.944 —
  unchanged from baseline** (the additive routing cannot reach the shared table/reading-order path).

### Engineering notes — the SF1449 journey

This release was driven by one form. A customer comparing a filled **SF-1449** PDF against Foliant's
Markdown (and feeding that Markdown to a Q&A tool) surfaced a chain of problems, each of which shaped a fix:

- **"Missing text" that wasn't missing.** Recall measured 100% — the values were present, but the whole
  form had collapsed into a single giant table region with scrambled reading order, so they *looked* lost.
  Upgrading recovered the literal field values; the deeper answer was to stop guessing form structure at
  runtime (below).
- **Checkbox selections silently disappeared.** A checked box stores its state in the widget `/AS`, not in
  the content-stream text — so the text layer carried the label but never the selection. Now emitted as
  `[X]`.
- **The line-item schedule collapsed 3 rows into 1.** With no visible row rules the table model predicted a
  single data row and piled every value into it. Fixed with form-scoped per-row column rendering — and
  gated so the shared table path is untouched.
- **The decisive one: 27a/27b mis-bound.** The dense `ADDENDA ARE / ARE NOT ATTACHED` clause checkboxes
  were paired to labels by runtime geometry, which scrambled them — and a mis-bound `[X]` is a *confidently
  wrong* Q&A answer, worse than a visible gap. This is what motivated the template approach: bind each
  widget to a *reviewed, known* position instead of guessing.
- **Building the template surfaced its own problems, each fixed at the source:** whole header rows merging
  into one "blob" label (fixed by splitting a row into separate labels on large X-gaps); lower schedule
  rows coming out `(unlabeled)` because only the top row sits under a printed header (fixed by inheriting
  the column header down the column); and the same value emitted twice when one filled widget overlapped
  two adjacent template rows at sub-row spacing (fixed by a 1:1 nearest-and-consume widget→element binding).

The result: SF-1449's set-aside block, 27a/27b clause selections, and line-item schedule now extract
deterministically with known-correct labels — the same machinery that powers the bundled federal templates
and customer-registered ones.

## 1.1.1 — 2026-06-17 (fix: dropped AcroForm/XFA field values)

Patch release. Fixes a correctness bug where **filled form-field values were silently dropped** on the
born-digital fast path — and the measurement blind spot that let it pass as 100% recall.

### Fixed
- **AcroForm/XFA filled field values are no longer lost.** On the text-layer fast path a fillable
  form's content stream carries only the printed labels; the typed-in values live in the field
  *widgets*, which the text layer (`GetWords()`) does not return — so the values were dropped from the
  output. The processor now recovers each visible widget's value (`/V`, with `/Parent` fallback), maps
  the widget rect into raster coordinates, and injects it as a positioned text line that flows into
  reading order and the output. (The OCR path already captured rendered values.)
- **Recall is now value-aware on forms.** `TextLayerRecall` compared output only against the PDF text
  layer — which *also* lacked the field values, so a value-less extraction still scored 100% (the
  metric was blind in the same way as the bug). The recall truth now includes form-field widget
  values, so Gate 1 detects and flags value loss instead of reporting a false 100%.

### Changed (build/release)
- **Package version is now derived from the git tag via MinVer**, replacing the hand-maintained
  `Directory.Build.props` version. Releasing is `./scripts/release.sh X.Y.Z` (see RELEASING.md) — this
  removes the version-file/tag mismatch that caused repeated release friction.

## 1.1.0 — 2026-06-17 (per-page progress reporting)

Minor release. Adds **opt-in per-page progress reporting** so consumers (e.g. a UI) can show real
progress instead of a page-count time estimate. **Additive and non-breaking** under
`API-STABILITY.md` — MINOR on the frozen 1.0 contract; existing callers are unaffected.

### Added
- **Per-page progress — `ProcessingProgress` + `ProcessingOptions.Progress`
  (`IProgress<ProcessingProgress>`, default `null`).** When set, the processor reports a
  `ProcessingProgress(TotalPages, CompletedPages, CurrentPage)` (with a `Fraction` helper) after each
  page completes, in page order. Progress reaches exactly 100% as the last page finishes — replacing
  the consumer workaround of a page-count-based time estimate capped below 100% until the result
  returned. `TotalPages` respects `ProcessingOptions.Pages` (the filtered set). Use
  `new Progress<ProcessingProgress>(...)` to auto-marshal callbacks to the UI thread. Additive to
  `ProcessingOptions` (the property defaults to `null`, so nothing is reported unless a sink is wired).

## 1.0.2 — 2026-06-15 (box-grid form scramble: ejection class fixed)

Patch release. Fixes the reading-order scramble flagged as a known issue in 1.0.1, for the case where
a box-grid form block is mis-detected as a table. No public API change (PATCH under
`API-STABILITY.md`); affects `Foliant.Pipeline` only. Validated on the forms corpus (474 pages):
reference recall held at 99.3% (no real tables reclassified), and the order-aware gate added in 1.0.1
confirms the previously-scrambled instruction rows now read in sequence.

### Fixed
- **Running text no longer scrambled when a form block is mis-gridded as a table (ejection class).**
  When the table-structure model imposes a grid on a box-grid form block, sentences spanning the
  cell borders were ejected outside the grid and re-appended out of order. `MarkdownComposer` now
  applies a **grid-fit guard**: if a table-detected region has a single column, or more than 25% of
  its text falls outside the predicted grid, the block is rendered as flowing reading-order prose
  instead of a scrambled table. Real data tables (which capture nearly all their text in cells) are
  unaffected; only mis-gridded prose blocks fall back. Covered by new regression tests.

### Known issue (still open, fix in progress)
- **Captured-but-reordered scramble on dense forms.** A distinct, harder case where the predicted
  grid *fits* (little text ejected, so the guard above does not fire) but the cells are still
  reordered because running sentences span multiple grid columns and are chopped and re-joined out
  of reading order — seen on some solicitation cover pages and CDRL forms. Tracked for a follow-up
  patch; it needs a column-spanning signal validated against table-heavy corpora to avoid
  reclassifying genuine wide tables. For forms with a `Foliant.Forms.*` profile, the cover-page
  key-values still extract correctly via the deterministic field path regardless of this issue.

## 1.0.1 — 2026-06-15 (form-extraction fixes; reading-order verification)

Patch release. Two defects surfaced on a standard **SF-30 (Amendment of Solicitation)** cover page in
production, plus the verification gap that let them ship. No public API change (PATCH under
`API-STABILITY.md`); affects `Foliant.Pipeline` and the `Foliant.Forms.UsFederal` pack only.

### Fixed
- **Label/value concatenation on box-grid form cells.** On dense federal forms a cell that carries
  both its printed label and the typed value in one box (e.g. SF-30 box 16A) was emitted as a single
  run-on string — the label text followed immediately by the filled-in value, with no separator
  between them. The `Foliant.Forms.UsFederal` **SF-30 profile now anchors the 15A/16A signer and
  contracting-officer name boxes** (and the "is required to sign" checkbox), so those values extract
  as clean typed fields instead of smushing into the label.

### Known issue (detected, fix in progress)
- **Running text scrambled on dense box-grid forms.** Whole instruction blocks on tightly gridded
  forms are classified as tables; the table-structure model then imposes a cell grid that does not
  match the form's logical reading flow, so a sentence spanning several cells is split across cells
  and rows and linearizes out of order. Every word is present (word-recall stays ~100%), but the
  sequence is wrong, which can invert meaning on instruction rows. This release **adds detection**
  for the failure (below); the fix — form-aware handling so spanning text is not carved into table
  cells — follows in a subsequent patch once validated against the reference corpus, to avoid
  regressing the 99.7% recall guarantee. For forms with a `Foliant.Forms.*` profile, the cover-page
  key-values still extract correctly via the deterministic field path regardless of this issue.

### Added
- **Order-aware verification gate (test harness).** Recall measured *set membership* — every word
  present scored 100%, so a permuted line was invisible to it. The scorecard now also reports a
  **reading-order fidelity** score per page: using the PDF text layer's natural word order as truth,
  it measures the longest run of output words kept in order (longest-increasing-subsequence over
  unique anchor words, O(n log n)). Pages with high recall but low order are now **flagged for
  review** — the exact signature of the box-grid scramble. This closes the measurement gap that let
  the defect through: "all the words are present" can no longer be mistaken for "the page reads
  correctly."

## 1.0.0 — 2026-06-15 (stable API)

The public API is now **frozen under Semantic Versioning** (see `API-STABILITY.md`). 1.0 is a
**stability commitment**, not a new feature: the layout-aware extraction pipeline — text-layer fast
path, scanned-document support (coarse-orientation correction + refinement, deskew, denoise,
contrast), XY-Cut++ reading order (+ opt-in enumerator ordering), table-structure extraction, the
low-DPI warning, watermark / CID / dynamic-XFA trust guards, coverage-invariant self-verification,
and typed form-field extraction with the `Foliant.Forms.UsFederal` / `Foliant.Forms.UsVirginia`
profile packs — has been **measured** (Gates 1–8: 99.7% reference-corpus recall across ~65k pages,
18 corpora) and is **proven in production** on real federal-RFP workloads.

From 1.0 onward, breaking the contract requires a MAJOR (2.0) release. The parked roadmap — GPU/ML
super-resolution, ML form understanding (LayoutLMv3/XFUND), post-OCR LM correction, multilingual
OCR, and additional `Foliant.Forms.*` jurisdiction packs — all lands **additively** (MINOR) on top
of the frozen 1.0 contract.

## 0.7.0 — 2026-06-15 (form-profile packs: US federal + Virginia)

### Added
- **`CreateDefaultAsync(formFields:)` overload** — the async auto-download factory now forwards a
  form-field extractor (plus `tableBackend` / `readingOrder`) to `CreateDefault`, so consumers can
  wire form-field extraction through the convenience path without dropping to `ModelCache` +
  `CreateDefault` themselves.
- **`Foliant.Forms.UsFederal` package** — ready-made `FormProfile`s for U.S. federal Standard Forms
  (FAR Part 53): **SF-33, SF-30, SF-1449** (validated against real instances) plus **SF-18, SF-1442,
  SF-26, OF-347, DD-1155** (layout drafts, pending first-instance validation). `FederalForms.All`
  hands the whole set to the extractor, which auto-selects the best-matching profile per page.
- **`Foliant.Forms.UsVirginia` package** — `VirginiaForms.CommonwealthRfp`, the Commonwealth of
  Virginia (eVA) RFP cover-page profile, validated against two agencies' real solicitations
  (VDACS, DSS).
- Both packs are **opt-in companions**; Foliant core stays jurisdiction-agnostic, so forms from any
  other state or country are profiled the same way (`Foliant.Forms.<jurisdiction>`).

## 0.6.0 — 2026-06-14 (form-field key-value extraction)

### Added
- **Typed key-value form-field extraction (foundation) — `FormField` / `FieldKind` /
  `FormFieldSource`, `IFormFieldExtractor`, `PageResult.FormFields`, and
  `ProcessingOptions.ExtractFormFields` (default off).** When extraction is enabled and an
  `IFormFieldExtractor` is wired, each page reports its form fields as typed `FormField`
  records (Name, Value, Kind, Bounds, Confidence, Source). This increment ships the
  `AcroFormFieldExtractor`, which reads exact field names and values from a PDF's fillable
  AcroForm dictionary via PdfPig (text fields and checkboxes); it returns nothing on
  flattened/scanned forms, which the geometric label→value fallback handles behind the same seam.
  Wired into `FoliantProcessor.CreateDefault`; off by default until the Gate 3 extraction scorecard
  proves it. Additive to `PageResult` (the field defaults to null).
- **Label-anchored geometric extraction for flattened forms — `FormProfile` / `FormFieldSpec` /
  `ValueAnchor`, `GeometricFormFieldExtractor`, `CompositeFormFieldExtractor`.** A deterministic
  (no-model, no-license) path for forms with no usable AcroForm: given a profile of label→field
  specs for a known form family (e.g. SF-33 / SIR solicitations), it locates each label on the
  page's recognized text and reads the associated value — inline (after the label), to the right,
  or below — and reads checkboxes by a mark glyph on the label's row. A min-label-match guard keeps
  it off pages that aren't the profile's form. `CompositeFormFieldExtractor` tries AcroForm first,
  then geometric, behind the `IFormFieldExtractor` seam. Profiles are caller-supplied domain
  knowledge, so the geometric path is opt-in (the default pipeline wires AcroForm only).
- **Gate 3 extraction scorer (`--gate3-extract`)** — scores the typed `FormFields` against the
  hand-labeled truth (value match for text, checked-state for checkboxes), reporting
  correct/wrong/missing. First measurement on the SF-33 solicitation profile: **4/5 profiled text
  fields and 13/17 checkboxes correct, with zero fabrication** (after column-scoped checkbox
  detection fixed the two-column TOC, and the buried `offer_due_date` was left unprofiled rather
  than guessed). The headline conclusion: deterministic label-anchored extraction
  is **viable and low-fabrication**; overall coverage is bounded by profiling effort (one profile
  per form family), not by extraction quality. `ExtractFormFields` stays off by default — it
  requires caller-supplied profiles — and is the deterministic baseline an ML form-understanding
  model would later have to beat.

## 0.5.0 — 2026-06-14 (low-DPI flag, reading-order & orientation refinement, API freeze)

### Changed
- **API stabilization review for 1.0 (see `API-STABILITY.md`).** Defines the public contract, the
  extension points, and the semver policy 1.0 will commit to. As part of narrowing the surface,
  four types that had leaked to `public` but are implementation details are now `internal`:
  `MarkdownComposer`, `ComposedPage`, `LineGrouping`, `ExtractionVerifier`. Callers consume
  `DocumentResult` / `PageResult` (and the self-verification results via `PageVerification`); none
  of these four were part of the intended contract. `FoliantProcessor.CreateDefault` is documented
  as the blessed entry point.

### Added
- **Orientation-detection refinement — text-quality guards against decorative-page misfires.**
  `OrientationDetector` now requires the winning rotation's recognized text to clear a **mean-confidence**
  floor (`minMeanConfidence`, default 0.5) and a **lexical-diversity** floor (`minDistinctWordRatio`,
  default 0.30) before a page is flipped, in addition to the existing upright-bias and min-character
  guards. This closes the decorative-front-matter hole: covers, blank endpapers and library-seal pages
  OCR into a page of repeating/low-confidence "text" from patterns — enough characters at enough
  confidence to clear the old guards — and were occasionally rotated spuriously. Genuine rotated body
  text (diverse, confident) clears the new floors comfortably, so real corrections are unaffected.
- **Enumerator-aware reading order — `ProcessingOptions.EnumeratorReadingOrder` (default off).** A
  post-pass after geometric ordering that reorders regions carrying a clean leading-number run
  (1,2,3,…) into numeric order, fixing numbered mosaics (magazine quizzes, instructional step grids)
  whose true order is the printed number — the documented Gate 6 τ≈0.733 boundary geometry can't see.
  Strict guard: it acts only on a complete consecutive run starting at 1 (≥3 regions, no gaps or
  duplicates) and otherwise leaves geometry completely untouched, so it cannot reorder a
  normally-ordered page; non-numbered regions keep their geometric slots. The geometric backends
  (`XyCut`, `XyCut++`) are unchanged. New verification flag `--enumerator-order` to A/B it on Gate 6.
  - **Measured neutral on the current Gate 6 magazine corpus** (avg τ 0.944 identical with and
    without the pass): the low-τ pages there are flowing multi-column *prose*, not numbered, while
    the one genuinely numbered page already orders correctly by geometry — so the strict guard found
    no qualifying page. It neither helped nor regressed. Kept as a default-off option for genuinely
    mis-ordered numbered documents (recipes, exams, step lists) outside this corpus.
- **Pre-OCR super-resolution seam — `IScanUpscaler` + `ProcessingOptions.UpscaleLowResolutionScans`
  (default off) + `LowResolutionUpscaleFactor`.** When an `IScanUpscaler` is injected and the option
  is on, pages flagged `LowResolution` are upscaled before orientation, preprocessing and OCR. The
  seam exists so an ML super-resolution backend can drop in without pipeline changes; the advisory
  `EffectiveDpi`/`LowResolution` fields continue to describe the original source scan, not the
  upscaled raster.
  - **`ClassicalScanUpscaler` (Catmull-Rom cubic) is provided as the reference implementation but is
    NOT wired into `FoliantProcessor.CreateDefault`.** The new Gate 8 ledger (born-digital corpus,
    forced OCR) measured classical upscaling as net-negative for OCR recall at every simulated
    low-DPI level — Δ −0.2 to −3.9 vs no upscale, worst at the lowest DPI, since interpolation
    enlarges blur/artifacts it cannot add detail to. So the default pipeline ships no upscaler and
    the option is a documented no-op until an ML backend proves a gain on Gate 8.
- **Gate 8 (verification harness) — super-resolution benefit ledger.** `--gate8 <born-digital-dir>`
  simulates low-DPI scans (`Downscale` 150/100/72) and A/Bs OCR recall with no upscale vs the
  classical upscaler at 1.5×/2×, scored against the pristine text layer. Ledger-first; never fails
  the build.
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
