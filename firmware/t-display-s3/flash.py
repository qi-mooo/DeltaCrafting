#!/usr/bin/env python3
"""Verify and flash the bundled images, preserving sectors outside the images."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", required=True, help="COM5 or /dev/cu.usbmodem...")
    parser.add_argument("--baud", type=int, default=921600)
    parser.add_argument("--verify-only", action="store_true")
    args = parser.parse_args()
    root = Path(__file__).resolve().parent
    manifest = json.loads((root / "flash-manifest.json").read_text(encoding="utf-8"))
    segments = []
    for entry in manifest["images"]:
        path = root / entry["file"]
        if path.parent != root or hashlib.sha256(path.read_bytes()).hexdigest() != entry["sha256"]:
            raise SystemExit("Firmware checksum/path mismatch: " + entry["file"])
        segments.extend([entry["offset"], str(path)])
    if args.verify_only:
        print("All firmware image checksums verified.")
        return
    subprocess.run([
        sys.executable, "-m", "esptool", "--chip", manifest["chip"], "--port", args.port,
        "--baud", str(args.baud), "--before", "default_reset", "--after", "hard_reset",
        "write_flash", "--flash_mode", manifest["flashMode"], "--flash_freq", manifest["flashFreq"],
        "--flash_size", manifest["flashSize"], *segments,
    ], check=True)


if __name__ == "__main__":
    main()
