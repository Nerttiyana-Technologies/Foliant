namespace Foliant.Models;

/// <summary>
/// Known model assets for the default Foliant pipeline. All hosted on Hugging Face under
/// permissive licenses (Apache 2.0 / MIT). Checksums were computed from the assets
/// downloaded and validated during the Phase 0 spike (2026-06-11).
/// </summary>
public static class ModelCatalog
{
    /// <summary>DocLayout-YOLO DocStructBench, imgsz 1024 (Apache 2.0).</summary>
    public static ModelAsset LayoutDetection { get; } = new(
        "layout-doclayout-yolo",
        "layout_doclayout_yolo.onnx",
        "https://huggingface.co/wybxc/DocLayout-YOLO-DocStructBench-onnx/resolve/main/doclayout_yolo_docstructbench_imgsz1024.onnx",
        "fece9af02f618b603ff7921ccec6861d13e7e1f9830e091dfb7e8ad9311e5b21",
        75_324_598);

    /// <summary>PaddleOCR PP-OCRv5 server-grade text detection (Apache 2.0).</summary>
    public static ModelAsset OcrDetection { get; } = new(
        "ocr-det-v5",
        "ocr_det_v5.onnx",
        "https://huggingface.co/monkt/paddleocr-onnx/resolve/main/detection/v5/det.onnx",
        "61824840edf6e74581898930b8091b1b2318f4b2705a2e8a40ad3de7ac480133",
        88_030_804);

    /// <summary>PaddleOCR English recognition (Apache 2.0).</summary>
    public static ModelAsset OcrRecognitionEnglish { get; } = new(
        "ocr-rec-en",
        "ocr_rec_en.onnx",
        "https://huggingface.co/monkt/paddleocr-onnx/resolve/main/languages/english/rec.onnx",
        "4e16deb22c4da6468bdca539b2cd3c8687825538b67109177c47d359ab994cd7",
        7_830_888);

    /// <summary>Character dictionary for <see cref="OcrRecognitionEnglish"/>.</summary>
    public static ModelAsset OcrRecognitionEnglishDict { get; } = new(
        "ocr-rec-en-dict",
        "ocr_rec_en.dict.txt",
        "https://huggingface.co/monkt/paddleocr-onnx/resolve/main/languages/english/dict.txt",
        "e025a66d31f327ba0c232e03f407ae8d105e1e709e7ccb3f408aa778c24e70d6",
        1_416);

    /// <summary>Microsoft TableTransformer structure recognition v1.1-all, Xenova ONNX export (MIT).</summary>
    public static ModelAsset TableStructure { get; } = new(
        "table-structure",
        "table_structure.onnx",
        "https://huggingface.co/Xenova/table-transformer-structure-recognition-v1.1-all/resolve/main/onnx/model.onnx",
        "72cc56f6db91132df16fb7f99b2b7033a287948d6c5656ecbbec195db0caad03",
        115_819_060);

    /// <summary>Microsoft TableTransformer detection, Xenova ONNX export (MIT). Not used by the default pipeline (layout detection already locates tables); cataloged for custom pipelines.</summary>
    public static ModelAsset TableDetection { get; } = new(
        "table-detection",
        "table_detect.onnx",
        "https://huggingface.co/Xenova/table-transformer-detection/resolve/main/onnx/model.onnx",
        "5be82ec9d157814ea8616588398d7baec17aed0780b870f7adf24b280ee1b5aa",
        115_694_355);

    // NOTE: the optional PaddleOCR textline-orientation classifier
    // (textline_orientation.onnx) is intentionally NOT cataloged yet — its exact
    // Hugging Face path and checksum are pending verification (network was unavailable
    // when this catalog was authored). The OCR engine treats it as optional and falls
    // back to dual-rotation recognition; see scripts/download-models.sh.

    /// <summary>Assets the default pipeline requires.</summary>
    public static IReadOnlyList<ModelAsset> DefaultPipeline { get; } =
    [
        LayoutDetection, OcrDetection, OcrRecognitionEnglish, OcrRecognitionEnglishDict, TableStructure,
    ];

    /// <summary>Every cataloged asset.</summary>
    public static IReadOnlyList<ModelAsset> All { get; } =
    [
        LayoutDetection, OcrDetection, OcrRecognitionEnglish, OcrRecognitionEnglishDict,
        TableStructure, TableDetection,
    ];
}
