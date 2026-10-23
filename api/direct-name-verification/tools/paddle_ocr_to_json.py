"""Run local PaddleOCR and save its complete OCR result as UTF-8 JSON.

Example:
    C:\\ocr-tools\\.venv313\\Scripts\\python.exe paddle_ocr_to_json.py \
        ..\\S__36208690.jpg --output ..\\output\\paddle_ocr.json
"""

from __future__ import annotations

import argparse
import json
import sys
import tempfile
from pathlib import Path

from paddleocr import PaddleOCR

# Windows PowerShell may default redirected output to CP950, which cannot represent
# every character returned by OCR. The C# host reads this protocol as UTF-8.
sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")


def create_ocr() -> PaddleOCR:
    # Keep the high-accuracy OCR pipeline but skip unused document preprocessing.
    # MKL-DNN is disabled because it causes an incompatibility on this Windows setup.
    return PaddleOCR(
        device="cpu",
        enable_mkldnn=False,
        use_doc_orientation_classify=False,
        use_doc_unwarping=False,
        use_textline_orientation=False,
    )


def recognize(ocr: PaddleOCR, image: Path) -> dict:
    if not image.is_file():
        raise FileNotFoundError(f"Image not found: {image.resolve()}")

    # PaddleOCR v3 provides a stable JSON exporter. Keep using it so the one-shot
    # command and the persistent worker return exactly the same schema.
    with tempfile.TemporaryDirectory() as directory:
        output = Path(directory) / "result.json"
        for result in ocr.predict(str(image)):
            result.save_to_json(str(output))
        return json.loads(output.read_text(encoding="utf-8"))


def run_worker() -> None:
    ocr = create_ocr()
    print(json.dumps({"type": "ready"}), flush=True)

    for line in sys.stdin:
        try:
            request = json.loads(line)
            if request.get("type") == "shutdown":
                break
            image = Path(request["image"])
            result = recognize(ocr, image)
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
    args = parser.parse_args()

    if args.worker:
        run_worker()
        return

    if args.image is None:
        parser.error("image is required unless --worker is used")

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(recognize(create_ocr(), args.image), ensure_ascii=False), encoding="utf-8")

    print(f"OCR JSON saved to: {args.output.resolve()}")


if __name__ == "__main__":
    main()
