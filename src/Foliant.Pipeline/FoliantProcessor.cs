// One-line construction of the default pipeline. Swap any stage by constructing
// DocumentProcessor directly with your own ILayoutDetector / IOcrEngine / ITableExtractor.

using Foliant.Layout.DocLayoutNet;
using Foliant.Models;
using Foliant.Ocr.PaddleOcr;
using Foliant.Tables.PaddleStructure;
using Foliant.Tables.TableTransformer;

namespace Foliant.Pipeline;

/// <summary>
/// Table backend selection. TableTransformer (default) is the fastest, but TableTransformer
/// </summary>
public enum TableBackend
{
    TableTransformer,
    PaddleStructure,
}

/// <summary>
/// Reading-order backend selection. XyCutPlusPlus (cross-layout masking + widest-gap axis
/// selection, adapted from arXiv 2504.10258) is the default as of v0.3.0, proven on the
/// Gate 6 truth set: ebooks tau 0.967 (tie with XyCut), magazines 0.962 vs 0.886, with the
/// only sub-1.0 page being a numbered step-grid whose true order is semantic (both backends
/// 0.733 — geometry cannot see step numbers; documented boundary). Reference corpus
/// regression unchanged (99.7%, 0 flags). XyCut remains available for comparison runs.
/// </summary>
public enum ReadingOrderBackend
{
    XyCut,
    XyCutPlusPlus,
}

public static class FoliantProcessor
{
    /// <summary>Optional textline-orientation model file name (used when present).</summary>
    public const string OrientationModelFileName = "textline_orientation.onnx";

    /// <summary>
    /// Creates the default pipeline from a directory of pre-downloaded models
    /// (see scripts/download-models.sh for file names).
    /// </summary>
    public static DocumentProcessor CreateDefault(
        string modelsDirectory,
        TableBackend tableBackend = TableBackend.TableTransformer,
        ReadingOrderBackend readingOrder = ReadingOrderBackend.XyCutPlusPlus,
        IFormFieldExtractor? formFields = null,
        IPageTemplateRouter? templateRouter = null,
        string? recognitionModelPath = null,
        string? recognitionDictPath = null,
        IScanUpscaler? scanUpscaler = null)
    {
        string Require(string fileName)
        {
            string path = Path.Combine(modelsDirectory, fileName);
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Model missing: {path}. Run scripts/download-models.sh, " +
                    "or use CreateDefaultAsync() to download into the local cache.", path);
            return path;
        }

        // Recognition model is overridable so a stronger (server-grade) rec model can be A/B-tested against
        // the default mobile rec on low-DPI scans (Gate 8) before it's promoted. Null = catalog default.
        static string RequireOverride(string? path, string what) =>
            path is null ? throw new InvalidOperationException()   // unreachable; guarded by caller
            : File.Exists(path) ? path
            : throw new FileNotFoundException($"{what} not found: {path}", path);
        string recPath = recognitionModelPath is null
            ? Require(ModelCatalog.OcrRecognitionEnglish.FileName)
            : RequireOverride(recognitionModelPath, "recognition model");
        string recDict = recognitionDictPath is null
            ? Require(ModelCatalog.OcrRecognitionEnglishDict.FileName)
            : RequireOverride(recognitionDictPath, "recognition dict");

        string? orientation = Path.Combine(modelsDirectory, OrientationModelFileName) is var o && File.Exists(o)
            ? o : null;

        var layout = new DocLayoutNetDetector(Require(ModelCatalog.LayoutDetection.FileName));
        var ocr = new PaddleOcrEngine(
            Require(ModelCatalog.OcrDetection.FileName),
            recPath,
            recDict,
            orientation);
        ITableExtractor tables = tableBackend switch
        {
            TableBackend.PaddleStructure =>
                new SlanetPlusExtractor(Require(ModelCatalog.TableStructureSlanetPlus.FileName)),
            _ => new TableTransformerExtractor(Require(ModelCatalog.TableStructure.FileName)),
        };

        IReadingOrderAssembler assembler = readingOrder switch
        {
            ReadingOrderBackend.XyCutPlusPlus => new XyCutPlusPlusReadingOrder(),
            _ => new XyCutReadingOrder(),
        };

        return new DocumentProcessor(
            new PdfPageRenderer(), layout, ocr, tables,
            assembler, new PdfTextLayerReader(),
            ownsComponents: true,
            preprocessor: new DefaultPagePreprocessor(),
            scanResolution: new PdfImageScanResolutionEstimator(),
            scanUpscaler: scanUpscaler,   // null by default; an ML super-res backend can be injected and Gate-8 measured
            formFields: formFields ?? new AcroFormFieldExtractor(),
            templateRouter: templateRouter);
        // No IScanUpscaler is wired by default: the Gate 8 ledger measured classical (bicubic)
        // upscaling as net-negative for OCR recall on low-DPI scans (it enlarges artifacts it
        // cannot add detail to). The IScanUpscaler seam stays for a future ML super-resolution
        // backend; inject one here once a Gate 8 run proves it gains recall.
    }

    /// <summary>
    /// Creates the default pipeline, downloading any missing models into the local cache
    /// (with SHA-256 verification) on first use.
    /// </summary>
    public static async Task<DocumentProcessor> CreateDefaultAsync(
        ModelCache? cache = null,
        IProgress<(string Id, double Fraction)>? downloadProgress = null,
        CancellationToken cancellationToken = default,
        TableBackend tableBackend = TableBackend.TableTransformer,
        ReadingOrderBackend readingOrder = ReadingOrderBackend.XyCutPlusPlus,
        IFormFieldExtractor? formFields = null,
        IPageTemplateRouter? templateRouter = null,
        string? recognitionModelPath = null,
        string? recognitionDictPath = null,
        IScanUpscaler? scanUpscaler = null)
    {
        cache ??= new ModelCache();
        await cache.GetPathsAsync(ModelCatalog.DefaultPipeline, downloadProgress, cancellationToken)
            .ConfigureAwait(false);
        return CreateDefault(cache.CacheDirectory, tableBackend, readingOrder, formFields, templateRouter,
            recognitionModelPath, recognitionDictPath, scanUpscaler);
    }
}
