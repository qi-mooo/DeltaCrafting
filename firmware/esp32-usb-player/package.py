#!/usr/bin/env python3
"""Package the built USB player, without local credentials or flash backups."""
import argparse
import hashlib
import json
import os
import re
from pathlib import Path
import zipfile


def main():
    root = Path(__file__).resolve().parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--framework", type=Path, default=Path(os.environ.get("PLATFORMIO_CORE_DIR", str(Path.home() / ".platformio"))) / "packages/framework-arduinoespressif32")
    args = parser.parse_args()
    build = root / ".pio/build/esp32-usb-player"
    identity = (root / "include/firmware_identity.h").read_text()
    version = re.search(r'#define HARP_FIRMWARE_VERSION "([^"]+)"', identity).group(1)
    images = [("0x0", build / "bootloader.bin"), ("0x8000", build / "partitions.bin"),
              ("0xe000", args.framework / "tools/partitions/boot_app0.bin"),
              ("0x10000", build / "firmware.bin")]
    manifest = {"chip": "esp32s3", "board": "ESP32-S3-Dongle-v1.0g-FN8",
                "firmwareVersion": version, "flashMode": "dio", "flashFreq": "80m",
                "flashSize": "8MB", "images": []}
    archive = build / "DeltaHarp-esp32s3-usb.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as bundle:
        for offset, path in images:
            data = path.read_bytes()
            bundle.writestr(path.name, data)
            manifest["images"].append({"offset": offset, "file": path.name, "sha256": hashlib.sha256(data).hexdigest()})
        bundle.writestr("flash-manifest.json", json.dumps(manifest, indent=2) + "\n")
        firmware = (build / "firmware.bin").read_bytes()
        ota = {"schemaVersion": 1, "board": "esp32-s3-dongle-fn8", "layout": "dual-8mb-v1",
               "version": version, "size": len(firmware), "sha256": hashlib.sha256(firmware).hexdigest()}
        bundle.writestr("ota-manifest.json", json.dumps(ota, indent=2) + "\n")
        bundle.write(root / "flash.py", "flash.py")
        bundle.write(build / "firmware.elf", "firmware.elf")
        for name in ("control.py", "README.md", "VERIFIED.md", "THIRD_PARTY_NOTICES.md"):
            bundle.write(root / name, name)
        for folder in ("src", "include", "lib", "tests", "web"):
            for path in sorted((root / folder).rglob("*")):
                if path.is_file() and "__pycache__" not in path.parts:
                    bundle.write(path, "source/" + str(path.relative_to(root)))
        for name in ("platformio.ini", "control.py", "package.py", "web_assets.py", "flash.py", "README.md", "VERIFIED.md", "THIRD_PARTY_NOTICES.md"):
            bundle.write(root / name, "source/" + name)
        deps = root / ".pio/libdeps/esp32-usb-player"
        for name, path in (("SdFat.txt", deps / "SdFat/LICENSE.md"),
                           ("ArduinoJson.txt", deps / "ArduinoJson/LICENSE.txt"),
                           ("Arduino-ESP32.txt", root / "licenses/Arduino-ESP32.txt"),
                           ("Lucide.txt", root / "licenses/Lucide.txt")):
            bundle.write(path, "licenses/" + name)
            if path.is_relative_to(root / "licenses"):
                bundle.write(path, "source/licenses/" + name)
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    archive.with_suffix(".zip.sha256").write_text(f"{digest}  {archive.name}\n")
    print(archive)


if __name__ == "__main__":
    main()
