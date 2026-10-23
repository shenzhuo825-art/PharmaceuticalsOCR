"""Run local PaddleOCR and save its complete OCR result as UTF-8 JSON.

Example:
    C:\\ocr-tools\\.venv313\\Scripts\\python.exe paddle_ocr_to_json.py \
        ..\\S__36208690.jpg --output ..\\output\\paddle_ocr.json
"""

from __future__ import annotations

import argparse
from pathlib import Path

from paddleocr import PaddleOCR


def main() -> None:
    parser = argparse.ArgumentParser(description="Convert a prescription image to PaddleOCR JSON.")
    parser.add_argument("image", type=Path, help="Path to the prescription image.")
    parser.add_argument(
        "--output",
        type=Path,
        default=Path("output/paddle_ocr.json"),
        help="JSON output path (default: output/paddle_ocr.json).",
    )
    args = parser.parse_args()

    if not args.image.is_file():
        raise FileNotFoundError(f"Image not found: {args.image.resolve()}")

    # Keep the high-accuracy OCR pipeline but skip unused document preprocessing.
    # MKL-DNN is disabled because it causes an incompatibility on this Windows setup.
    ocr = PaddleOCR(
        device="cpu",
        enable_mkldnn=False,
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,
    )

    args.output.parent.mkdir(parents=True, exist_ok=True)
    for result in ocr.predict(str(args.image)):
        result.save_to_json(str(args.output))

    print(f"OCR JSON saved to: {args.output.resolve()}")


if __name__ == "__main__":
    main()
