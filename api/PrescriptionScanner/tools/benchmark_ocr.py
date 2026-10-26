"""Compare CPU thread settings on identical images, checking recognized text.

Run with the PaddleOCR Python environment. No patient text is printed or saved.
"""
import argparse
import os
import statistics
import time
from pathlib import Path

from paddle_ocr_to_json import create_ocr, recognize


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("images", nargs="+", type=Path)
    parser.add_argument("--threads", nargs="+", type=int, default=[10, 4])
    parser.add_argument("--repeats", type=int, default=2)
    args = parser.parse_args()
    if args.repeats < 1 or any(t < 1 for t in args.threads):
        parser.error("repeats and threads must be positive")
    if any(not image.is_file() for image in args.images):
        parser.error("all image paths must exist")
    baseline = {}
    for threads in args.threads:
        os.environ["PADDLE_OCR_CPU_THREADS"] = str(threads)
        start = time.perf_counter()
        ocr = create_ocr()
        print(f"threads={threads} init={time.perf_counter() - start:.3f}s", flush=True)
        for image in args.images:
            timings = []
            identical = True
            for _ in range(args.repeats):
                start = time.perf_counter()
                result = recognize(ocr, image.resolve())
                timings.append(time.perf_counter() - start)
                texts = result["rec_texts"]
                if image not in baseline:
                    baseline[image] = texts
                identical &= texts == baseline[image]
            print(f"threads={threads} image={image.name} median={statistics.median(timings):.3f}s "
                  f"runs={[round(t, 3) for t in timings]} text_matches_baseline={identical}", flush=True)
            if not identical:
                raise RuntimeError("Recognized text changed; do not adopt this configuration")
        del ocr


if __name__ == "__main__":
    main()
