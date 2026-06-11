// One-line construction of the default pipeline. Swap any stage by constructing
// DocumentProcessor directly with your own ILayoutDetector / IOcrEngine / ITableExtractor.

using Foliant.Layout.DocLayoutNet;
using Foliant.Models;
using Foliant.Ocr.PaddleOcr;
using Foliant.Tables.TableTransformer;

namespace Foliant.Pipeline;

public static class FoliantProcessor
{
    /// <summary>Optional textline-orientation model file name (used when present).</summary>
    public const string OrientationModelFileName = "textline_orientation.onnx";

    /// <summary>
    /// Creates the default pipeline from a directory of pre-downloaded models
    /// (see scripts/download-models.sh for file names).
    /// </summary>
    public static DocumentProcessor CreateDefault(string modelsDirectory)
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
        var tables = new TableTransformerExtractor(Require(ModelCatalog.TableStructure.FileName));

        return new DocumentProcessor(
            new PdfPageRenderer(), layout, ocr, tables,
            new XyCutReadingOrder(), new PdfTextLayerReader(),
            ownsComponents: true);
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
