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

    /// <summary>
    /// PaddleOCR PP-LCNet textline-orientation classifier, 2 classes {0°, 180°} (Apache 2.0).
    /// Optional: enables single-pass rotated-text recognition; the OCR engine falls back to
    /// dual-rotation recognition when absent. Validated on the reference corpus 2026-06-11
    /// (Gate 4: questionnaire-class pages ≥95% recall).
    /// </summary>
    public static ModelAsset TextlineOrientation { get; } = new(
        "textline-orientation",
        "textline_orientation.onnx",
        "https://huggingface.co/monkt/paddleocr-onnx/resolve/main/preprocessing/textline-orientation/PP-LCNet_x1_0_textline_ori.onnx",
        "34ec07c0bcd591da2ae6651924a1d8fb85f7ca60ac9a58ac417ecf12a5fc1e1a",
        6_774_157);

    /// <summary>
    /// SLANet-plus table-structure recognition, official PaddlePaddle ONNX export (Apache 2.0).
    /// v0.2.0 raster-table backend (Foliant.Tables.PaddleStructure); emits HTML structure
    /// tokens with row/column spans + per-cell quads. Not yet the default — the default
    /// switches only if the corpus cell-accuracy scorecard proves it (KICKOFF quality roadmap).
    /// </summary>
    public static ModelAsset TableStructureSlanetPlus { get; } = new(
        "table-slanet-plus",
        "table_slanet_plus.onnx",
        "https://huggingface.co/PaddlePaddle/SLANet_plus_onnx/resolve/main/inference.onnx",
        "7790c0c13ce064782c9d22ebeb16b4da8216f83d3ba576da962c106ef58386da",
        7_782_138);

    /// <summary>Assets the default pipeline uses (textline orientation is optional but recommended).</summary>
    public static IReadOnlyList<ModelAsset> DefaultPipeline { get; } =
    [
        LayoutDetection, OcrDetection, OcrRecognitionEnglish, OcrRecognitionEnglishDict,
        TableStructure, TextlineOrientation,
    ];

    /// <summary>
    /// Real-ESRGAN x4plus super-resolution, ONNX graph (BSD-3-Clause; upstream
    /// https://github.com/xinntao/Real-ESRGAN, license confirmed on the export's model card).
    /// Used by Foliant.ScanUpscale.SuperResolution in the low-resolution RETRY role
    /// (ProcessingOptions.RetryLowResolutionPages) on hosts that opt in — CPU-capable, but a
    /// CUDA GPU is the intended production home. This export stores its weights in the companion
    /// <see cref="SuperResolutionData"/> file, which MUST sit next to the graph under its exact
    /// file name (the graph references it by name). Fixed input 128×128, batch 1 (tile batching
    /// unavailable with this export).
    /// </summary>
    public static ModelAsset SuperResolution { get; } = new(
        "sr-real-esrgan-x4plus",
        "real_esrgan_x4plus.onnx",
        "https://huggingface.co/Nerttiyana-Technologies/real-esrgan-x4plus-onnx/resolve/main/real_esrgan_x4plus.onnx",
        "36b217f0ef1c4a88c7bb493c188c15314724dec19f46ae5393f27c4fa7cfc5b4",
        446_757);

    /// <summary>External weights for <see cref="SuperResolution"/> (same license; see there).</summary>
    public static ModelAsset SuperResolutionData { get; } = new(
        "sr-real-esrgan-x4plus-data",
        "real_esrgan_x4plus.data",
        "https://huggingface.co/Nerttiyana-Technologies/real-esrgan-x4plus-onnx/resolve/main/real_esrgan_x4plus.data",
        "1bcfa110ca9d9c59594630d73a679d2582b947b9103b10de6743c762f6d006f6",
        66_737_664);

    /// <summary>Every cataloged asset.</summary>
    public static IReadOnlyList<ModelAsset> All { get; } =
    [
        LayoutDetection, OcrDetection, OcrRecognitionEnglish, OcrRecognitionEnglishDict,
        TableStructure, TableDetection, TextlineOrientation, TableStructureSlanetPlus,
        SuperResolution, SuperResolutionData,
    ];
}
