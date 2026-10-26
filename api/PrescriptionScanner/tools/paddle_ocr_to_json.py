"""Run local PaddleOCR and save its complete OCR result as UTF-8 JSON.

Example:
    C:\\ocr-tools\\.venv313\\Scripts\\python.exe paddle_ocr_to_json.py \
        ..\\S__36208690.jpg --output ..\\output\\paddle_ocr.json
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

from paddleocr import PaddleOCR

# Windows PowerShell may default redirected output to CP950, which cannot represent
# every character returned by OCR. The C# host reads this protocol as UTF-8.
sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")


def create_ocr(profile: str = "original") -> PaddleOCR:
    # Keep the high-accuracy OCR pipeline but skip unused document preprocessing.
    # MKL-DNN is disabled because it causes an incompatibility on this Windows setup.
    threads = int(os.environ.get("PADDLE_OCR_CPU_THREADS", "10"))
    if threads < 1:
        raise ValueError("PADDLE_OCR_CPU_THREADS must be a positive integer")
    if profile not in ("original", "fast"):
        raise ValueError("Unknown OCR profile")
    models = {} if profile == "original" else {
        "text_detection_model_name": "PP-OCRv5_mobile_det",
        "text_recognition_model_name": "PP-OCRv5_mobile_rec",
    }
    return PaddleOCR(
        **models,
        device="cpu",
        cpu_threads=threads,
        enable_mkldnn=False,
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,
    )


def recognize(ocr: PaddleOCR, image: Path) -> dict:
    if not image.is_file():
        raise FileNotFoundError(f"Image not found: {image.resolve()}")

    # The public JSON property uses the same exporter as save_to_json, without
    # creating, writing, reading and deleting a temporary file for every image.
    data = None
    for result in ocr.predict(str(image)):
        data = result.json["res"]
    if data is None:
        raise RuntimeError("PaddleOCR returned no result")
    return data


def warm_up(ocr: PaddleOCR) -> None:
    # Loading weights alone does not execute either network. Exercise detection
    # and recognition using generated text, never a patient's prescription.
    import numpy as np
    from PIL import Image, ImageDraw, ImageFont

    image = Image.new("RGB", (960, 640), "white")
    draw = ImageDraw.Draw(image)
    font = ImageFont.load_default(size=42)
    for row in range(6):
        draw.text((40, 35 + row * 90), "OCR READY 0123456789", fill="black", font=font)
    for _ in ocr.predict(np.asarray(image)[:, :, ::-1].copy()):
        pass


def run_worker(warmup: bool = False) -> None:
    ocr = create_ocr()
    if warmup:
        warm_up(ocr)
    print(json.dumps({"type": "ready"}), flush=True)
    engines = {"original": ocr}

    for line in sys.stdin:
        try:
            request = json.loads(line)
            if request.get("type") == "shutdown":
                break
            image = Path(request["image"])
            profile = request.get("profile", "original")
            if profile not in ("original", "fast"):
                raise ValueError("Unknown OCR profile")
            if profile not in engines:
                engines[profile] = create_ocr(profile)
            result = recognize(engines[profile], image)
            print(json.dumps({"type": "result", "data": result}, ensure_ascii=False), flush=True)
        except Exception as error:  # Return an error in the protocol; do not leave C# waiting.
            print(json.dumps({"type": "error", "message": str(error)}, ensure_ascii=False), flush=True)


def main() -> None:
    parser = argparse.ArgumentParser(description="Convert a prescription image to PaddleOCR JSON.")
    parser.add_argument("image", type=Path, nargs="?", help="Path to the prescription image.")
    parser.add_argument(
        "--output",
        type=Path,
        default=Path("output/paddle_ocr.json"),
        help="JSON output path (default: output/paddle_ocr.json).",
    )
    parser.add_argument(
        "--worker",
        action="store_true",
        help="Keep the OCR model in memory and receive JSON-line requests from standard input.",
    )
    parser.add_argument("--warmup", action="store_true", help="Prime both OCR networks with generated text before worker readiness.")
    parser.add_argument("--profile", choices=("original", "fast"), default="original")
    args = parser.parse_args()

    if args.worker:
        run_worker(warmup=args.warmup)
        return

    if args.image is None:
        parser.error("image is required unless --worker is used")

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(recognize(create_ocr(args.profile), args.image), ensure_ascii=False), encoding="utf-8")

    print(f"OCR JSON saved to: {args.output.resolve()}")


if __name__ == "__main__":
    main()
