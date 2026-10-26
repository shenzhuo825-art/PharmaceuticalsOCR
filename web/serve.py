"""Compatibility launcher for the integrated .NET OCR web service."""
import argparse
import subprocess
from pathlib import Path

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=8080)
    args = parser.parse_args()
    raise SystemExit(subprocess.call(["powershell", "-ExecutionPolicy", "Bypass", "-File",
        str(Path(__file__).resolve().with_name("start.ps1")), "-Port", str(args.port)]))
