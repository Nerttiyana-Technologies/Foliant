# OSS Document Annotation Tool (.NET) — Action Plan

**Positioning:** The document annotation platform for .NET shops — classification, span labeling, and *table-aware* PDF/docx annotation, with pre-annotation and active learning as first-class citizens (not plugins). No credible .NET competitor exists; Label Studio/Doccano are Python-only.

**Dual purpose:** This tool IS the annotation console of the RFP platform (ML_BUILD_PLAN.md Phases 1 & 9.3). Every feature ships because the RFP pipeline needs it — you are user #1, which is how good OSS gets built.

**Foliant integration — the differentiator:** Foliant is the native ingestion engine. Upload a PDF → Foliant returns regions with bounding boxes, table structure, confidence, and reading order → the tool renders the page image with Foliant regions as clickable annotation targets. This enables *layout-aware annotation* (label a table cell, a region, a span within a region) that text-only tools can't do, and it's all-.NET end to end. `NeedsReview` pages auto-enqueue for human review — Foliant's self-verification becomes the tool's triage signal. Product-family story: **"Foliant parses it; [tool] labels it"** — one org, two Apache-2.0 packages, each driving adoption of the other. Keep the ingestion interface pluggable (Foliant is the default `IDocumentIngester`, but plain-text import still works without it) so non-PDF users aren't locked out.

**Stack:** ASP.NET Core on .NET 10.0.301 · React 18 + TypeScript · PostgreSQL · Apache 2.0.

---

## Step 1 — Name & claim (this week, ~2 hours)

1. Pick a name; check github.com, nuget.org, npmjs, domain, trademark conflicts.
2. Create the GitHub org + repo (public from day one — building in the open beats a big-bang launch).
3. Add: `LICENSE` (Apache 2.0), `README.md` with the one-paragraph pitch + a GIF placeholder, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md`, issue templates.

## Step 2 — Architecture skeleton (Week 1)

```
/src
  Server/          ASP.NET Core 10 minimal API + SignalR
  Server.Data/     EF Core 10, PostgreSQL, multi-project schema
  Client/          React 18 + TS + Vite (shadcn/ui, dark+light themes,
                   IconButton-with-tooltip standard component)
/docs              Docusaurus or plain md
/deploy            docker-compose.yml (one-command spin-up — non-negotiable
                   for OSS adoption), Dockerfile, helm chart later
/tests             xUnit (server), Vitest + Playwright (client)
```

Key design decisions to bake in early (hard to retrofit):

- **Label Studio JSON import/export compatibility.** Their task/annotation format is the de-facto standard. Supporting it means teams can migrate *to* you in minutes — your single best adoption lever — and you can use either tool interchangeably in the RFP pipeline.
- **Pre-annotation-first data model.** Every task carries `predictions[]` (model output, with confidence + model version) separate from `annotations[]` (human-verified). The review UX is accept/correct, never blank-slate.
- **ML backend as a webhook contract.** Simple REST contract: tool POSTs tasks to any URL, gets predictions back; a second endpoint asks "which unlabeled tasks should humans see next?" (active learning). Language-agnostic — Python model servers plug in, so do ML.NET ones.
- **Multi-tenancy from day one** (org → project → task) — you need it for the RFP platform anyway; OSS users get team workspaces for free.

## Step 3 — MVP v0.1 (Weeks 2–6, maps to RFP Phase 1 needs)

Scope ruthlessly — v0.1 is exactly what the 100k-doc funnel needs:

1. Project setup with label taxonomy config (classification + span types).
2. Task import: JSON/CSV/zip of txt-md; **pre-annotations included in import**.
3. Document classification UI: doc rendered, predicted label pre-selected, hotkey accept/correct, auto-advance. Target: <5 seconds per accepted doc.
4. Span labeling UI for requirement extraction review (text highlighting + class assignment).
5. Export: Label Studio JSON + simple JSONL.
6. Review stats: throughput, agreement, per-class counts.
7. Auth: local accounts + OIDC.

Defer to v0.2+: Foliant-powered PDF region annotation (page image + bounding-box overlays — your differentiator; spec the `IDocumentIngester` contract in v0.1, ship the UI in v0.2), table-cell annotation, active-learning queue endpoint, Label Studio ML-backend protocol compatibility, docx rendering.

## Step 4 — Dogfood on the real corpus (Weeks 4–8)

Run your actual Phase 1 labeling campaign (1000+ docs, then customer onboarding sprints) through the tool. Every friction point you feel is a GitHub issue; every fix is validated by real workload before any external user hits it. This is also your demo content story: "we labeled 100k RFP documents with this."

## Step 5 — OSS launch (when v0.1 is dogfood-proven, ~Week 8–10)

1. Polish README: 30-second GIF of the accept/correct flow, one-command `docker compose up`, architecture diagram.
2. Docs site: quickstart, ML-backend contract, Label Studio migration guide.
3. Launch posts: r/dotnet, r/csharp, Hacker News (Show HN), dev.to, .NET community standup submission, X/LinkedIn. Lead with the niche: *"Label Studio alternative for .NET shops — document-first, pre-annotation-first, Apache 2.0."*
4. NuGet package for the client SDK (`YourTool.Client`) so .NET apps integrate in three lines — the ecosystem hook Python tools can't offer.
5. Good-first-issue backlog seeded (10–15 items) before launch day; respond to every issue/PR fast in the first months — responsiveness, not features, builds contributor communities.

## Step 6 — Sustainability decisions (Month 3+)

- Keep a clean core/enterprise boundary in mind (the HumanSignal model): OSS = full annotation capability; potential paid tier later = SSO/SCIM, audit exports, managed hosting. Apache 2.0 keeps this door open.
- Governance: you as BDFL initially; add maintainers from consistent contributors.
- Roadmap public via GitHub Projects; releases on a monthly cadence, semver.

---

## Interlock with ML_BUILD_PLAN.md

| RFP plan item | Served by |
|---|---|
| Phase 1.4 annotation (Label Studio) | Replaced by this tool from v0.1 |
| Phase 9.3 human review console | This tool's review UI, embedded/linked in the platform |
| Customer onboarding labeling sprints | Runs on this tool — and each sprint stress-tests it |
| Continuous improvement loop (Phase 8.6) | Corrections export → LoRA refresh pipeline |

**Risk to manage:** scope creep from OSS feature requests pulling you off the RFP roadmap. Rule: community features that the RFP pipeline doesn't need go to the backlog, not the sprint — until the platform ships.

## This week's checklist

- [ ] Name chosen, repo created, license + README pushed
- [ ] docker-compose skeleton: Postgres + API + client hot-reload
- [ ] Data model spike: project/task/prediction/annotation entities in EF Core 10
- [ ] Import one real parsed RFP doc + fake pre-annotation end-to-end
