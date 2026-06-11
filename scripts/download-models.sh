#!/usr/bin/env bash
# Downloads ONNX model assets for the Foliant spike into models/.
# All repos and file paths verified against the HF API on 2026-06-11.
# Total download: ~330 MB.
set -euo pipefail

MODELS_DIR="$(cd "$(dirname "$0")/.." && pwd)/models"
mkdir -p "$MODELS_DIR"

hf_get() { # repo file dest
  local dest="$MODELS_DIR/$3"
  if [[ -f "$dest" ]]; then echo "✓ $3 (cached)"; return; fi
  local url="https://huggingface.co/$1/resolve/main/$2"
  echo "→ $3  ($url)"
  curl -fL --retry 3 -o "$dest" "$url"
}

# [2] Layout detection — DocLayout-YOLO DocStructBench, imgsz 1024 (Apache 2.0, 75 MB)
#     Repo also ships inference.py — reference for pre/post-processing (letterbox, NMS).
hf_get "wybxc/DocLayout-YOLO-DocStructBench-onnx" \
       "doclayout_yolo_docstructbench_imgsz1024.onnx" "layout_doclayout_yolo.onnx"

# [3] OCR — PaddleOCR ONNX (monkt/paddleocr-onnx, Apache 2.0)
#     Detection: PP-OCRv5 server-grade (88 MB). Mobile alt: detection/v3/det.onnx (2.4 MB).
hf_get "monkt/paddleocr-onnx" "detection/v5/det.onnx"            "ocr_det_v5.onnx"
hf_get "monkt/paddleocr-onnx" "detection/v5/config.json"         "ocr_det_v5.config.json"
#     Recognition: English (7.8 MB) + char dict
hf_get "monkt/paddleocr-onnx" "languages/english/rec.onnx"       "ocr_rec_en.onnx"
hf_get "monkt/paddleocr-onnx" "languages/english/dict.txt"       "ocr_rec_en.dict.txt"
hf_get "monkt/paddleocr-onnx" "languages/english/config.json"    "ocr_rec_en.config.json"

#     Textline-orientation classifier (PP-LCNet, 2 classes {0°,180°}, ~6.5 MB) — optional;
#     enables single-pass rotated-text handling (Gate 4). Engine falls back to
#     dual-rotation recognition when absent.
hf_get "monkt/paddleocr-onnx" "preprocessing/textline-orientation/PP-LCNet_x1_0_textline_ori.onnx" \
       "textline_orientation.onnx"

# [4] Tables — TableTransformer ONNX fp32 (Xenova exports of Microsoft checkpoints, MIT)
hf_get "Xenova/table-transformer-detection" "onnx/model.onnx"    "table_detect.onnx"
hf_get "Xenova/table-transformer-structure-recognition-v1.1-all" \
       "onnx/model.onnx" "table_structure.onnx"

echo "Done. Models in $MODELS_DIR"
