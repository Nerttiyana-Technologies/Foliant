// One-line construction of the default pipeline. Swap any stage by constructing
// DocumentProcessor directly with your own ILayoutDetector / IOcrEngine / ITableExtractor.

using Foliant.Layout.DocLayoutNet;
using Foliant.Models;
using Foliant.Ocr.PaddleOcr;
using Foliant.Tables.PaddleStructure;
using Foliant.Tables.TableTransformer;

namespace Foliant.Pipeline;

/// <summary>
/// Table-structure backend selection for <see cref="FoliantProcessor.CreateDefault(string, TableBackend)"/>.
/// TableTransformer (+ ruling-line hybrid) is the current default; PaddleStructure (SLANet-plus)
/// is the v0.2.0 raster-table candidate. The default switches only when the Gate 5 cell-accuracy
/// scorecard proves it on the reference corpus (KICKOFF quality roadmap).
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
        ReadingOrderBackend readingOrder = ReadingOrderBackend.XyCutPlusPlus)
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

        string? orientation = Path.Combine(modelsDirectory, OrientationModelFileName) is var o && File.Exists(o)
            ? o : null;

        var layout = new DocLayoutNetDetector(Require(ModelCatalog.LayoutDetection.FileName));
        var ocr = new PaddleOcrEngine(
            Require(ModelCatalog.OcrDetection.FileName),
            Require(ModelCatalog.OcrRecognitionEnglish.FileName),
            Require(ModelCatalog.OcrRecognitionEnglishDict.FileName),
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
            scanUpscaler: new ClassicalScanUpscaler());
    }

    /// <summary>
    /// Creates the default pipeline, downloading any missing models into the local cache
    /// (with SHA-256 verification) on first use.
    /// </summary>
    public static async Task<DocumentProcessor> CreateDefaultAsync(
        ModelCache? cache = null,
        IProgress<(string Id, double Fraction)>? downloadProgress = null,
        CancellationToken cancellationToken = default)
    {
        cache ??= new ModelCache();
        await cache.GetPathsAsync(ModelCatalog.DefaultPipeline, downloadProgress, cancellationToken)
            .ConfigureAwait(false);
        return CreateDefault(cache.CacheDirectory);
    }
}
