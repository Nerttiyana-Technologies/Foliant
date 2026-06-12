# Foliant

**Layout-aware PDF document AI for .NET — fully local, no Python sidecar, no cloud APIs.**

[![NuGet](https://img.shields.io/nuget/v/Foliant.Pipeline.svg)](https://www.nuget.org/packages/Foliant.Pipeline)
[![CI](https://github.com/Nerttiyana/Foliant/actions/workflows/ci.yml/badge.svg)](https://github.com/Nerttiyana/Foliant/actions/workflows/ci.yml)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](LICENSE)

Foliant extracts structured content (Markdown / JSON / typed objects) from PDFs the way
commercial document-intelligence services do — layout detection, per-region OCR,
table-structure recognition, reading-order assembly — running entirely on your machine via
ONNX Runtime. Documents never leave the host.

```csharp
using Foliant.Pipeline;

// Models download once into a local cache (SHA-256 verified), then everything runs offline.
using var processor = await FoliantProcessor.CreateDefaultAsync();
var result = await processor.ProcessAsync(File.ReadAllBytes("document.pdf"));

Console.WriteLine(result.Markdown);          // layout-aware Markdown
string json = result.ToJson(indented: true); // structured regions, tables, bounds, confidence
```

## The gap this fills

Python has a rich open-source document-understanding ecosystem (surya, marker, docling,
unstructured). .NET has excellent text extraction (PdfPig, iText) but essentially nothing
open-source for *layout-aware* understanding: region classification, reading-order inference,
table-structure extraction. Handing whole pages to a general vision-language model produces
plausible-looking output that silently fabricates details — checkbox states, names, field
values — which is disqualifying wherever the answers matter.

Foliant takes the decomposition approach the commercial services use, with open models:

```
PDF page
  → render (PDFium)
  → layout detection                DocLayout-YOLO          what is where
  → text, per region                embedded text layer when present (fast path),
                                    PaddleOCR v5 where pixels are the only source
  → table structure, per region     TableTransformer + ruling-line analysis
  → reading order                   recursive XY-cut
  → Markdown / JSON / typed DocumentResult
```

Every stage is an interface in `Foliant.Core` — swap any backend without forking.

## Trust properties

Foliant is built for documents where silent errors are unacceptable, and it treats
verifiability as a feature:

- **Lossless by construction.** A per-page coverage invariant guarantees every extracted line
  provably lands in the output (or is intentional page furniture, reported as such).
- **Self-scoring.** Pages with an embedded text layer are scored against it — the PDF itself
  is the answer key. On a 474-page federal-RFP reference corpus in forced-OCR mode:
  **99.7% average word recall, 100% of pages ≥95%, zero text loss, zero fabricated form
  values.** Generalization sweeps across ~1,940 pages (IRS, USCIS, Indian ITR, Air Force
  solicitations, scanned business forms): **zero text loss, zero crashes**; structure
  extraction on unseen scanned forms validated by inspection. Known measured limits:
  non-Latin scripts (multilingual recognition is roadmap) and colored watermark overprint.
- **Deterministic.** Same input, same output, every run. No temperature, no sampling.
- **Private by default.** No network calls at processing time; model downloads are the only
  network activity, cache them once and run air-gapped.

## Packages

| Package | Purpose |
|---|---|
| [`Foliant.Pipeline`](https://www.nuget.org/packages/Foliant.Pipeline) | Batteries-included default pipeline — start here |
| [`Foliant.Core`](https://www.nuget.org/packages/Foliant.Core) | Interfaces + DTOs only; depend on this to consume results or author backends |
| [`Foliant.Layout.DocLayoutNet`](https://www.nuget.org/packages/Foliant.Layout.DocLayoutNet) | DocLayout-YOLO layout-detection backend |
| [`Foliant.Ocr.PaddleOcr`](https://www.nuget.org/packages/Foliant.Ocr.PaddleOcr) | PaddleOCR det/rec backend with rotated-text handling |
| [`Foliant.Tables.TableTransformer`](https://www.nuget.org/packages/Foliant.Tables.TableTransformer) | TableTransformer + ruling-grid table-structure backend (default) |
| [`Foliant.Tables.PaddleStructure`](https://www.nuget.org/packages/Foliant.Tables.PaddleStructure) | SLANet-plus table backend for raster/screenshot tables (opt-in) |
| [`Foliant.Models`](https://www.nuget.org/packages/Foliant.Models) | Model catalog + SHA-256-verified local cache |

Model weights (~280 MB) are not inside the packages. They download on first use into the
local cache (`~/.local/share/Foliant/models` on macOS/Linux, `%LocalAppData%\Foliant\models`
on Windows), or pre-fetch with `scripts/download-models.sh` and pass the directory to
`FoliantProcessor.CreateDefault(modelsDir)`.

## Quick start

```bash
dotnet add package Foliant.Pipeline
```

Or run the sample against any PDF:

```bash
git clone https://github.com/Nerttiyana/Foliant.git
cd Foliant
dotnet run -c Release --project samples/Foliant.Sample.Console -- path/to/document.pdf
# → sample-out/document.md + sample-out/document.json
```

**Requirements:** .NET 10 SDK. Windows, macOS, and Linux (x64 + arm64). CPU-only works
everywhere; ONNX Runtime execution providers (CoreML, DirectML, CUDA) are a configuration
option for acceleration.

## Performance

Born-digital pages (embedded text layer) take the fast path: layout from pixels, characters
verbatim from the PDF — about **0.4 s/page** at 300 DPI on Apple-silicon CPU. Full-OCR pages
run around 4 s/page. Throughput scales linearly with cores; no GPU required.

## Quality methodology

Foliant doesn't ship a release without measured quality (`tests/Foliant.Verification`):

```bash
dotnet run -c Release --project tests/Foliant.Verification -- <pdf-dir>
# → scorecard.csv: per-page recall, coverage, timing, pass/fail against release gates
```

The harness enforces the coverage invariant and word-recall gates over a reference corpus on
every release. Run it on *your* corpus before adopting — if Foliant underperforms on your
documents, that's a bug report we want.

## Repository layout

```
src/        shipping packages (Core, Pipeline, backends, Models)
tests/      unit tests + the verification/scorecard harness
samples/    console sample (ASP.NET and Blazor samples planned)
spike/      Phase 0 throwaway prototype + measured results (RESULTS.md) — kept for history
scripts/    model download helper
```

Scanned pages routed to OCR get deterministic preprocessing automatically: deskew (±8°),
contrast normalization for faded scans, and despeckling (`ProcessingOptions.PreprocessScans`).

## Roadmap

- Watermark suppression (colored DRAFT-stamp overprint measurably degrades OCR underneath)
- Page-orientation and de-warp preprocessing (models already cataloged)
- Form-field key-value extraction as typed output
- Languages beyond English (PaddleOCR multilingual recognition models)
- Additional backends (`Foliant.Ocr.Tesseract`; community backends welcome — the
  interfaces are the contract, no central gatekeeping)
- Post-OCR language-model correction (local, optional)

`IDocumentProcessor` and the `Foliant.Core` contracts are treated as stable from 1.0;
backends iterate freely.

## Built on

[ONNX Runtime](https://onnxruntime.ai/) ·
[DocLayout-YOLO](https://github.com/opendatalab/DocLayout-YOLO) (Apache 2.0) ·
[PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) (Apache 2.0) ·
[TableTransformer](https://github.com/microsoft/table-transformer) (MIT) ·
[PDFtoImage](https://github.com/sungaila/PDFtoImage) / PDFium ·
[PdfPig](https://github.com/UglyToad/PdfPig)

## License

[Apache 2.0](LICENSE)
