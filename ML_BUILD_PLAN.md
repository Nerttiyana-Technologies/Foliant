# RFP/RFQ/RFI/DDQ Intelligence Platform — Step-by-Step Build Plan

**Decisions locked in:** Fine-tuned open-source LLM + RAG · Self-hosted · Milestones: (1) classification → (2) requirement matrix → (3) DDQ auto-answer → (4) draft proposal generation.

**Key reality:** Customer documents arrive scattered and unsorted. Your manually organized test corpus is the labeled seed data. The doc-type classifier you train in Phase 2 becomes the automatic triage stage that sorts customer documents in production.

---

## Phase 0 — Infrastructure & Environment (Week 1)

1. **GPU for training:** Rent, don't buy, initially. 1× A100 80GB (or H100) on RunPod / Lambda / Azure NC-series. LoRA fine-tuning of a 8B model fits on a single A100; you only pay during training runs (~$2–3/hr).
2. **GPU for inference (later, Phase 8):** 1× L40S or A100 40GB runs a quantized 8B model with vLLM at good throughput.
3. **Software stack (Python for ML — .NET comes in at the serving layer, Phase 8):**
   - Python 3.11, PyTorch 2.x, CUDA 12.x
   - `docling` (IBM) or `unstructured` — document parsing (PDF/docx/xlsx/pptx → structured JSON)
   - `axolotl` or `unsloth` — LoRA fine-tuning
   - `vllm` — inference serving
   - `qdrant` (Docker) — vector database
   - `sentence-transformers` — embeddings
   - `label-studio` — annotation UI
   - `mlflow` — experiment tracking
4. **Repo layout:**
   ```
   /data          raw | parsed | annotated | splits
   /pipelines     parsing, chunking, labeling scripts
   /training      configs, LoRA runs
   /eval          test harness, metrics, reports
   /serving       vLLM configs, .NET API (Phase 8)
   ```

---

## Phase 1 — Data Preparation (Weeks 1–3)

**Goal: every document, regardless of format, becomes clean structured JSON with labels.**

### Why three datasets, and why each stage exists

- **Training set — teaches the model.** The model adjusts its weights to fit these examples. Errors here become learned behavior, which is why annotation quality matters most on this set's reviewed portion.
- **Test set — steers development.** You evaluate every experiment (prompt change, hyperparameter, new checkpoint) against it and pick winners. Because you make hundreds of decisions using it, the model indirectly "overfits" to the test set over time — your reported numbers drift optimistic.
- **Verification (holdout) set — the honest final exam.** Touched exactly once, at Phase 7 sign-off. Because no decision was ever made using it, its numbers are the only ones you can quote to customers or put in a model card without lying to yourself. If verification scores drop far below test scores, you overfit and must revisit.
- **Why split at document level:** RFPs reuse boilerplate. If requirements from the same document land in both train and test, the model "recognizes" rather than "generalizes" — you'd ship a model that aces your test set and fails on the customer's 100k unseen docs.
- **Why classification comes first:** every downstream product (matrix, DDQ, proposals) consumes *classified requirements* as input. Errors compound — a 90%-accurate classifier feeding a 90%-accurate extractor yields ~81% end-to-end. Getting the first stage strong is the cheapest place to buy overall quality. It's also the production triage gate: the customer's scattered 100k docs enter here, so this model decides what the rest of the pipeline even sees.
- **Why a taxonomy frozen before annotation:** if the label set changes mid-annotation, everything labeled before the change is inconsistent with everything after — you pay for re-annotation. Freeze v1, version any later changes (v2 = new annotation round).

1. **Inventory:** Script that walks your train/test/verification folders and logs: filename, format, size, page count, source folder (= doc-type label for the organized set).
2. **Parse everything to a common schema.** Use **Foliant** (your own library) for all PDFs — layout detection, table structure, OCR, reading order, per-page self-verification, fully local (the data-privacy story customers are buying). Add Open XML SDK for docx/xlsx and trivial readers for md/txt, normalized into the same schema. Foliant's `NeedsReview` pages route straight to the human review queue. (`docling` remains a fallback benchmark to compare extraction quality against.)
   ```json
   {
     "doc_id": "...", "source_file": "...", "doc_type_label": "RFP",
     "sections": [{"heading": "...", "level": 1, "blocks": [
       {"type": "paragraph|table|list", "text": "...", "page": 3}
     ]}]
   }
   ```
   Tables matter enormously here — requirement matrices and DDQs are mostly tabular. Verify table extraction quality on 20 sample docs by hand before batch-processing.
3. **Define the label taxonomy** (do this before annotation, keep v1 small):
   - Doc types: `RFP, RFQ, RFI, DDQ, Proposal, Requirement_Matrix, Past_Performance, Questionnaire, Other`
   - Requirement classes (v1): `Technical, Functional, Compliance/Legal, Security, Financial/Pricing, Administrative/Submission, Past_Performance_Request`
   - Requirement attributes: `mandatory | optional | informational` (shall/must vs. should/may)
4. **LLM-assisted annotation** (solves your "no fine-grained labels" gap):
   - Use a strong LLM (Claude via API on your *own* non-customer training data — this is your data, not customer data) to pre-label: split sections into individual requirements, assign class + mandatory/optional.
   - Load pre-labels into Label Studio; **human-review 100%** of the test and verification sets, and at least 20–30% of training set (prioritize low-confidence items).
   - Rule of thumb targets: ≥3,000 labeled requirements for training, ≥500 each for test/verification.
5. **Split hygiene — critical:** Never split at requirement level. Split at *document* level (all requirements from one doc stay in one split), and ideally at *source-organization* level, or you'll leak near-duplicate boilerplate across splits and get inflated metrics. Keep the verification set locked away — touch it only at final sign-off (Phase 7).

### Labeling at scale — the customer's 100,000+ documents

**Core principle: you never label 100k documents by hand. You label a few thousand smartly-chosen ones; models label the rest; humans only check where models are unsure.**

The funnel (each stage cuts the human workload ~5–10×):

1. **Deduplicate first — `datasketch` (MinHash/LSH) + embedding similarity.**
   Corporate document stores are full of near-copies (v1/v2/final/FINAL2, template reuse). Expect 100k raw → 30–60k unique. Label one representative per duplicate cluster; propagate its label to the cluster. *Zero-cost labels.*

2. **Cluster and label by group, not by document — embed all docs (`bge-m3`), cluster with HDBSCAN/`BERTopic`.**
   Similar documents land together (DDQs cluster with DDQs). A human looks at 5–10 samples per cluster and labels the *cluster* — labeling 200 clusters instead of 50,000 documents. Impure clusters get flagged for finer treatment.

3. **Zero-/few-shot bootstrap — `SetFit` (sentence-transformer few-shot, needs only ~8–30 examples per class) or LLM pre-labeling.**
   Train on your small hand-organized corpus; run over everything; keep only high-confidence predictions as provisional labels. For requirement-level labels, cheap heuristics get surprisingly far first: `shall|must` → mandatory, `should|may` → optional, question marks + numbering → questionnaire items, filename/metadata patterns → doc type. Encode these as **weak supervision** rules (`skweak` or simple scripts) and combine votes.

4. **Active learning — Label Studio with an ML backend (or `Argilla`).**
   Instead of labeling randomly, the current model picks the documents it's *least sure about* and queues only those for humans. Typically reaches target accuracy with 3–10× fewer human labels than random selection. This is the single biggest multiplier and it's built into the tools already in the stack.

5. **Self-training loop.** Train on labeled seed → predict on unlabeled pool → adopt predictions above a confidence threshold (e.g., ≥0.95) as new training data → retrain → repeat 2–3 rounds. Each round expands the labeled set for free; the active-learning queue catches the model's remaining blind spots.

6. **Label-error detection — `cleanlab`.**
   Before final training runs, cleanlab cross-checks labels against model predictions and surfaces likely mistakes (both human and machine ones). Reviewing its top-flagged 2–5% catches most label noise — much cheaper than re-reviewing everything.

**Realistic human budget for 100k customer docs:** dedupe + clustering + few-shot bootstrap ≈ 0 manual labels for ~70% of the corpus; active learning ≈ 2,000–5,000 human-reviewed docs for the hard remainder; cleanlab review ≈ a few hundred corrections. That's *days of annotator time, not months* — and the same funnel becomes a repeatable onboarding pipeline for every future customer, which matters for the SaaS margin story.

**Important boundary:** LLM-API pre-labeling (step 3) is fine on *your* datasets. For customer documents, run labeling models self-hosted (SetFit, your fine-tuned classifier) or get explicit contractual consent before any third-party API sees their content.

---

## Phase 2 — Milestone 1: Classification (Weeks 3–5)

### 2a. Document-type classifier (the production triage stage)
1. Model: fine-tune `DeBERTa-v3-base` (or `ModernBERT-base`) on first ~2 pages + headings + filename features. Small, fast, cheap to run on CPU/small GPU — right tool for triage; don't burn LLM tokens on this.
2. Training: standard HF `Trainer`, ~10–20 epochs, early stopping on validation F1.
3. Target: macro-F1 ≥ 0.92 on test set. Add a confidence threshold — below it, route to `Other/human review` (essential for scattered customer data).
4. Handle multi-doc files: customers will send zips and combined PDFs. Add a page-level segmentation pass later (v2); for v1, classify whole files and flag low-confidence ones.

### 2b. Requirement extraction + classification (the LLM's first job)
1. Model: `Qwen2.5-7B-Instruct` (Apache 2.0 — cleanest license for commercial resale) or `Llama-3.1-8B-Instruct` (permissive but has Meta's license terms; fine under 700M MAU, review before resale).
2. Fine-tune with **LoRA** (r=16, alpha=32, lr 2e-4, 2–3 epochs) on instruction pairs:
   - *Input:* a parsed section (with table structure) → *Output:* JSON array of `{req_id, text, class, mandatory, source_page}`.
   - Build these pairs directly from your Phase 1 annotations.
3. Enforce JSON output with constrained decoding (vLLM structured output / `outlines`).
4. Metrics: extraction recall (did we find every requirement? — recall matters more than precision here; a missed "shall" requirement is a lost bid), classification F1 per class.
5. Baseline first: evaluate the *un-tuned* model with a good prompt before fine-tuning. If the baseline hits 85% and tuning gets 92%, that 7-point delta is your defensible IP — measure it.

---

## Phase 3 — RAG Corpus (Weeks 5–6, parallel with Phase 2)

1. Chunk past performance docs, prior proposals, and answered questionnaires: chunk by section (respect headings), 300–800 tokens, with metadata `{doc_type, customer_domain, date, tags}`.
2. Embeddings: `BAAI/bge-m3` or `nomic-embed-text` (both open, self-hostable). Store in Qdrant.
3. Retrieval = hybrid: dense vectors + BM25, then rerank top-20 → top-5 with `bge-reranker-v2-m3`.
4. Build a retrieval eval set now: 100 questions with known-correct source chunks. Target recall@5 ≥ 0.85. Generation quality (Phases 5–6) is capped by retrieval quality — fix this layer first.

---

## Phase 4 — Milestone 2: Requirement Matrix (Weeks 6–8)

1. Pipeline, not a new model: doc-type classifier → section parser → requirement extractor (Phase 2b) → deduplication (embedding similarity ≥0.92 flags near-dupes) → xlsx writer.
2. Output columns: `Req ID | Section ref | Requirement text | Class | Mandatory? | Page | Suggested owner | Compliance status (blank) | Response (blank)`.
3. Evaluate end-to-end on held-out test docs against hand-built "gold" matrices: row-level recall/precision, cell-level accuracy.

---

## Phase 5 — Milestone 3: DDQ Auto-Answer (Weeks 8–10)

1. Extract questions from incoming DDQ (reuse Phase 2b extractor — questions are just requirements phrased interrogatively; add a `question` flag to taxonomy).
2. For each question: retrieve top-5 from answer library (Phase 3) → LLM drafts answer *grounded in retrieved text only*, citing source chunks → confidence score.
3. Three-tier output: **auto-fill** (high confidence + high retrieval score), **draft for review**, **no answer found — human required**. Never silently guess on a DDQ; wrong compliance answers are a liability issue for your customers.
4. Fine-tune a second LoRA adapter for answer style if the base model's tone is off (same base model, swap adapters at inference — vLLM supports multi-LoRA serving).

---

## Phase 6 — Milestone 4: Draft Proposal Generation (Weeks 10–13)

1. Input: requirement matrix (Phase 4) + retrieved past-performance/prior-proposal chunks (Phase 3) + a win-theme prompt from the user.
2. Generate section-by-section (executive summary, technical approach, past performance, management), never whole-document — keeps context focused and lets users regenerate one section.
3. Every generated claim about capability/experience must carry a citation to a source chunk; uncited claims get flagged in the UI. This is your anti-hallucination guardrail and a selling point.
4. Fine-tune a third LoRA adapter on pairs of (requirements + retrieved context → winning proposal section) from your proposal corpus.
5. Evaluation: LLM-as-judge rubric (coverage of requirements, grounding, tone) + human review. Track "% of draft kept after human edit" once real users exist — that's the metric customers buy on.

---

## Phase 7 — Final Evaluation & Sign-off (Week 13)

1. Freeze all models/prompts. Run the **verification set** (untouched until now) through every pipeline.
2. Ship-gate thresholds (adjust to your risk tolerance): doc-type F1 ≥ 0.92 · requirement recall ≥ 0.90 · matrix row recall ≥ 0.85 · DDQ auto-fill precision ≥ 0.95 · zero uncited claims in proposal drafts.
3. Write a model card per component: training data description, metrics, known failure modes, intended use. Enterprise buyers and their procurement teams will ask for this.

---

## Customer Onboarding — Coverage-Gap Analysis & Mitigation

**The question this answers:** *"Our training data looks ~70% similar to the customer corpus — how do we know the model will work on their documents, and what do we do about the other 30%?"*

### Step 1 — Measure the gap (don't estimate it)

Run once per customer, before go-live (1–2 days of compute + review):

1. Embed the training corpus and a customer sample (≥1,000 docs, or all of them — embedding 100k docs costs a few GPU-hours) with the same model (`bge-m3`).
2. Cluster both corpora *together* (HDBSCAN/BERTopic).
3. Classify every customer cluster:
   - **Covered** — cluster contains training docs → expect test-level accuracy.
   - **Partially covered** — few/weak training neighbors → degraded accuracy likely.
   - **Uncovered** — no training neighbors at all → model is guessing; highest risk.
4. Output: a **coverage report** — % of customer corpus per category, with named examples ("their vendor security questionnaires use a template we've never seen"). This turns "roughly 70% similar" into an evidence-backed number leadership and the customer can both act on.

### Step 2 — Handle each category differently

| Coverage | Go-live behavior | Fix |
|---|---|---|
| Covered (~70%) | Auto-process at normal confidence thresholds | None needed; monitor |
| Partial | Process but with raised confidence bar → more routed to human review | Add to targeted labeling queue |
| Uncovered | Route 100% to human review; do **not** auto-answer | Targeted labeling sprint (below) |

### Step 3 — Close the gap fast (targeted labeling sprint)

1. From uncovered/partial clusters, pick 200–500 representative docs (cluster centroids + active-learning selections — not random).
2. Label with the Phase 1 funnel (LLM-assisted if contract permits, else self-hosted SetFit + human review). Days, not weeks, at this volume.
3. Fine-tune a customer-domain LoRA adapter (or continue training the existing one) on the new labels.
4. Re-run the coverage report + test-set eval → verify uncovered % shrank and nothing regressed.
5. Repeat once if needed. Typical result: uncovered share drops from ~30% to <10% within 2–4 weeks of onboarding.

### Step 4 — Safety net for whatever remains

- **Confidence gating:** every prediction below threshold goes to the human review console — unfamiliar documents cost review time, never silent errors.
- **Correction loop:** every human fix is captured as training data (annotation console, Phase 9.3) → weekly adapter refresh during onboarding, quarterly at steady state.
- **Drift alarms:** monitor confidence distribution and human-override rate per doc-type; a new unfamiliar document stream shows up in these metrics before it shows up as customer complaints.

### The one-slide answer for leadership

> We measure — not guess — what fraction of the customer's corpus our model has seen the likes of. The covered majority (est. ~70%) performs at tested accuracy from day one. The gap is quantified in a coverage report, closed by a 2–4 week targeted labeling sprint on a few hundred documents, and until closed, every unfamiliar document is confidence-gated to human review, so the failure mode is "human checks it," never "wrong answer ships." Accuracy therefore *improves* during the first month of each engagement, and the same playbook repeats for every new customer — onboarding cost shrinks as the training corpus grows.

---

## Phase 8 — Serving & Path to SaaS (Weeks 13+)

1. **Inference:** vLLM serving the base model + hot-swappable LoRA adapters (extraction / DDQ / proposal) behind one endpoint. Quantize to AWQ/GPTQ int4 for cost.
2. **API layer:** ASP.NET Core on **.NET 10.0.301** — REST API fronting the Python inference services; handles auth, tenancy, queuing (long docs are async jobs), xlsx/docx generation, SignalR for job-progress push to the UI.
3. **Multi-tenancy (non-negotiable for selling as a service):**
   - Per-tenant Qdrant collections and encrypted storage — one customer's past performance must never surface in another's retrieval results.
   - Per-tenant LoRA adapters if you later fine-tune on customer data (with explicit contractual consent).
   - Full audit log of every generation with its retrieved sources.
4. **Compliance runway:** start SOC 2 Type I early (3–6 months); enterprise RFP/DDQ buyers will DDQ *you*. Ironically, your own product answers those.
5. **Licensing check before resale:** Qwen2.5 (Apache 2.0) ✓ resale-clean. Llama 3.x — acceptable but review Meta's license. All other components listed are Apache/MIT. Verify `docling` (MIT ✓), `vllm` (Apache ✓), Qdrant (Apache ✓).
6. **Continuous improvement loop:** log human edits to drafts/answers (with consent) → periodic re-annotation → quarterly LoRA refresh → re-run Phase 7 gates before each release.

---

## Phase 9 — UI Layer (Weeks 14–20, overlaps Phase 8)

**Stack decided:** React 18 + TypeScript · Vite · executive-grade design system with **dark + light themes** · **icon-based buttons with tooltips** throughout.

1. **Foundation (Week 14):**
   - Component library: shadcn/ui (Radix + Tailwind) — clean executive look, full theming control via CSS variables; dark/light via `class` strategy with system-preference default and manual toggle.
   - State/data: TanStack Query against the .NET API; SignalR client for live job progress (parsing/extraction of large docs is async).
   - Auth: OIDC (Entra ID / Auth0) with tenant claim; every API call tenant-scoped.
   - Design tokens first: typography scale, spacing, semantic colors (both themes), elevation. Lock these before building screens.
   - All actions as icon buttons with `Tooltip` wrappers (Radix Tooltip) — standardize a single `IconButton` component so the convention is enforced, not remembered.

2. **Core workflow screens (Weeks 14–17):**
   - **Intake & triage:** drag-drop upload (zip/PDF/docx/xlsx), live parsing progress, triage results table showing predicted doc type + confidence; low-confidence rows visually flagged for human review with one-click reclassify.
   - **Requirement matrix grid:** virtualized data grid (TanStack Table) — inline edit of class/mandatory/owner, filter/group by class, dedupe suggestions surfaced inline, xlsx export. This grid is the workhorse; invest here.
   - **DDQ answer review:** three-tier queue (auto-filled / draft / needs-human), side-by-side question + drafted answer + retrieved source snippets with similarity scores; accept / edit / reject per answer, bulk-accept for high-confidence tier.
   - **Proposal editor:** section outline nav, TipTap rich-text editor per section, citation chips inline (click → source chunk panel), per-section regenerate with instruction box, uncited-claim warnings rendered as amber highlights.

3. **Human review / annotation console (Week 17):** reuses the matrix grid + a correction mode; every correction is logged and exported as training data for the quarterly LoRA refresh (closes the Phase 8.6 loop). Build immediately after the grid since it's ~80% reuse.

4. **Admin & tenant management (Week 18):** tenant onboarding wizard, user/role management (Admin, Proposal Manager, Contributor, Reviewer), answer-library CRUD with version history, audit log viewer (every generation + its sources — the SOC 2 evidence screen).

5. **Analytics dashboard (Weeks 19–20):** auto-fill rate, % of draft kept after human edit, turnaround time per document, model confidence trends, per-tenant usage. Recharts; keep it read-only v1.

6. **UI quality gates:** WCAG 2.1 AA in both themes (contrast-check the dark palette specifically), keyboard navigation on the grid and editor, Playwright E2E for the four core flows, Storybook for the design system.

---

## Sequencing summary

| Weeks | Deliverable |
|---|---|
| 1–3 | Parsed + annotated datasets, clean splits |
| 3–5 | Doc-type classifier + requirement extractor (Milestone 1) |
| 5–6 | RAG corpus + retrieval eval |
| 6–8 | Requirement matrix pipeline (Milestone 2) |
| 8–10 | DDQ auto-answer (Milestone 3) |
| 10–13 | Proposal draft generation (Milestone 4) |
| 13 | Verification sign-off, model cards |
| 13+ | .NET API, multi-tenancy, SOC 2 path |
| 14–17 | UI: core workflow screens (intake, matrix grid, DDQ review, proposal editor) |
| 17–18 | UI: annotation console, admin & tenant management |
| 19–20 | UI: analytics dashboard, accessibility & E2E hardening |

## Immediate next actions (this week)

1. Run the inventory script over your three dataset folders — get exact counts per format and per type.
2. Parse 20 representative docs with docling; hand-check table extraction quality.
3. Draft the label taxonomy v1 and freeze it before any annotation starts.
