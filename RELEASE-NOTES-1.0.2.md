# Foliant 1.0.2 — release notes

_Patch releases on top of the frozen 1.0 public API ([`API-STABILITY.md`](API-STABILITY.md)).
No breaking changes; both 1.0.1 and 1.0.2 are PATCH releases under Semantic Versioning._

These two patches harden form extraction on dense government-style box-grid forms, after issues
surfaced in production use, and add the verification needed to catch the whole failure class going
forward.

## 1.0.2 — box-grid form scramble (ejection class)

**Fixed — running text no longer scrambled when a form block is mis-gridded as a table.**
On dense forms, the layout model can classify a whole instruction block as a table, and the
table-structure model then imposes a cell grid whose borders cut across a running sentence. Text
that spanned those fake borders was ejected outside the grid and re-appended out of order, so the
sentence linearized scrambled — every word present, but the sequence wrong (which can invert meaning
on instruction rows).

`MarkdownComposer` now applies a **grid-fit guard**: if a table-detected region has a single column,
or more than 25% of its text falls outside the predicted grid, the block renders as flowing
reading-order prose instead of a scrambled table. Real data tables — which capture nearly all their
text inside cells — are unaffected; only mis-gridded prose blocks fall back. New regression tests
cover both directions (fallback fires on a mis-gridded block; a genuine table still grids).

Validated on the 474-page reference forms corpus: word recall held at 99.3% (no real tables
reclassified), and the order-aware gate confirms the previously-scrambled instruction rows now read
in sequence.

**Affected package:** `Foliant.Pipeline`. No public API change.

### Known issue (tracked for a follow-up)
A distinct, harder case remains: where the predicted grid *fits* (little text ejected, so the guard
above does not fire) but cells are still reordered because running sentences span multiple grid
columns — seen on some solicitation cover pages and CDRL forms. The order-aware gate flags these for
review. The fix needs a column-spanning signal validated against table-heavy corpora to avoid
reclassifying genuine wide tables. For forms with a `Foliant.Forms.*` profile, the cover-page
key-values still extract correctly via the deterministic field path regardless.

## 1.0.1 — form-extraction fixes and reading-order verification

**Fixed — label/value concatenation on box-grid form cells.** On dense federal forms a cell that
carries both its printed label and the typed value in one box (for example the signer and
contracting-officer name boxes) was emitted as a single run-on string. The
`Foliant.Forms.UsFederal` SF-30 profile now anchors the 15A/16A signer and contracting-officer name
boxes (and the "is required to sign" checkbox), so those values extract as clean typed fields
instead of smushing into the label.

**Added — order-aware verification gate.** Word recall measures set membership, so a permuted line
still scores 100% — it is structurally order-blind. The scorecard now also reports a reading-order
fidelity score per page: using the PDF text layer's natural word order as truth, it measures the
longest run of output words kept in order (a longest-increasing-subsequence over unique anchor
words). Pages with high recall but low order are flagged for review — the signature of the box-grid
scramble fixed in 1.0.2. This closes the measurement gap that let the defect through: "all the words
are present" can no longer be mistaken for "the page reads correctly."

**Affected packages:** `Foliant.Pipeline`, `Foliant.Forms.UsFederal`. No public API change.

## Upgrading

```
dotnet add package Foliant --version 1.0.2
```

All `Foliant.*` packages share one version. Drop-in for any 1.0.x consumer — no code changes
required.
