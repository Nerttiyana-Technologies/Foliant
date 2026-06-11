# Foliant

Layout-aware PDF document AI for .NET — fully local, no Python sidecar, no cloud APIs.

Foliant extracts structured content (Markdown / JSON / typed objects) from PDFs the way
commercial document-intelligence services do: layout detection, per-region OCR, table-structure
recognition, and reading-order assembly — all running on ONNX Runtime, entirely on your machine.
Documents never leave the host.

```csharp
using Foliant.Pipeline;

// Models download once into a local cache (SHA-256 verified), then everything runs offline.
using var processor = await FoliantProcessor.CreateDefaultAsync();
var result = await processor.ProcessAsync(File.ReadAllBytes("document.pdf"));

Console.WriteLine(result.Markdown);          // layout-aware Markdown
string json = result.ToJson(indented: true); // structured regions, tables, bounds, confidence
```

## Why Foliant

- **Born-digital fast path** — pages with an embedded text layer take characters verbatim
  (layout still analyzed from pixels); OCR runs only where needed. ~0.4 s/page at 300 DPI.
- **Forms done right** — recursive ruling-line decomposition keeps checkbox marks, field
  labels, and values associated on government-style forms; table grids come from a hybrid of
  Microsoft TableTransformer and rule-based ruling analysis.
- **Self-verifying** — every page enforces a coverage invariant (every extracted line provably
  lands in the output or is intentional page furniture) and reports word recall against the
  PDF's own text layer. On a 474-page federal-RFP reference corpus: 99.7% average word recall,
  100% of pages ≥95%, zero text loss, zero fabricated form values.
- **Pluggable** — every stage (layout, OCR, tables, reading order) is an interface in
  `Foliant.Core`; swap any backend without forking.

## Packages

| Package | Purpose |
|---|---|
| `Foliant.Pipeline` | Batteries-included default pipeline (start here) |
| `Foliant.Core` | Interfaces + DTOs only — for consumers and backend authors |
| `Foliant.Layout.DocLayoutNet` | DocLayout-YOLO layout detection backend |
| `Foliant.Ocr.PaddleOcr` | PaddleOCR detection/recognition backend with rotated-text handling |
| `Foliant.Tables.TableTransformer` | TableTransformer + ruling-grid table structure backend |
| `Foliant.Models` | Model catalog + verified local cache (weights from Hugging Face) |

Model weights (~280 MB total) are not inside the packages; they download on first use with
checksum verification, or pre-fetch them yourself and point `FoliantProcessor.CreateDefault`
at the directory.

Apache 2.0. English-language documents; scanned-document preprocessing is on the roadmap.
