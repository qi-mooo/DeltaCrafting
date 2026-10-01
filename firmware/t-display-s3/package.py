"""PlatformIO post-build: produce a generic firmware bundle with exact flash offsets."""
import hashlib
import json
import os
from pathlib import Path
import zipfile

Import("env")

project = Path(env.subst("$PROJECT_DIR"))
if (project / "include/config.local.h").exists():
    raise RuntimeError("Remove config.local.h before building a distributable firmware bundle; configure over USB instead.")


def package(source, target, env):
    build = Path(env.subst("$BUILD_DIR"))
    audio = project / "audio/.pio/build/windows-audio"
    if not (audio / "firmware.bin").exists():
        raise RuntimeError("Build the audio program first: pio run -d firmware/t-display-s3/audio")
    if (audio / "partitions.bin").read_bytes() != (build / "partitions.bin").read_bytes():
        raise RuntimeError("Monitor and audio partition tables must match to preserve NVS and RESET behavior")
    if int(env.subst("$ESP32_APP_OFFSET"), 0) != 0x10000:
        raise RuntimeError("Monitor must be installed in app0 at 0x10000")
    for image in (build / "firmware.bin", audio / "firmware.bin"):
        if image.stat().st_size > 0x640000:
            raise RuntimeError("Firmware exceeds its application partition: " + str(image))
    images = list(env.get("FLASH_EXTRA_IMAGES", []))
    images.append((env.subst("$ESP32_APP_OFFSET"), str(build / "firmware.bin")))
    images.append(("0x650000", str(audio / "firmware.bin")))
    manifest = {
        "chip": "esp32s3",
        "firmwareVersion": "axeuh-tools-v15",
        "flashMode": env.subst("${__get_board_flash_mode(__env__)}"),
        "flashFreq": env.subst("${__get_board_f_flash(__env__)}"),
        "flashSize": env.BoardConfig().get("upload.flash_size"),
        "images": [],
    }
    archive = build / "DeltaCrafter-esp32s3.zip"
    with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as bundle:
        for offset, filename in images:
            path = Path(env.subst(str(filename)))
            data = path.read_bytes()
            name = "audio-firmware.bin" if path == audio / "firmware.bin" else path.name
            manifest["images"].append({"offset": str(offset), "file": name,
                                       "sha256": hashlib.sha256(data).hexdigest()})
            bundle.writestr(name, data)
        bundle.writestr("flash-manifest.json", json.dumps(manifest, indent=2) + "\n")
        bundle.write(build / "firmware.elf", "firmware.elf")
        bundle.write(audio / "firmware.elf", "audio-firmware.elf")
        for name in ("flash.py", "configure.py", "README.md", "THIRD_PARTY_NOTICES.md"):
            bundle.write(project / name, name)
            bundle.write(project / name, "source/" + name)
        for folder in ("src", "include", "tests", "audio/src"):
            for path in sorted((project / folder).rglob("*")):
                if path.is_file():
                    bundle.write(path, "source/" + str(path.relative_to(project)))
        for name in ("platformio.ini", "package.py", "audio/platformio.ini"):
            bundle.write(project / name, "source/" + name)
        sdk = Path(os.environ["TDISPLAY_S3_DIR"])
        libdeps = Path(env.subst("$PROJECT_LIBDEPS_DIR")) / env.subst("$PIOENV")
        licenses = {
            "LILYGO.txt": sdk / "LICENSE",
            "TFT_eSPI.txt": sdk / "lib/TFT_eSPI/license.txt",
            "ArduinoJson.txt": libdeps / "ArduinoJson/LICENSE.txt",
            "U8g2.txt": libdeps / "U8g2/LICENSE",
            "Axeuh_UI.txt": libdeps / "Axeuh_UI/LICENSE",
        }
        for name, path in licenses.items():
            bundle.write(path, "licenses/" + name)
        # Include corresponding source for both the UI default and display font.
        fonts = (libdeps / "U8g2/src/clib/u8g2_fonts.c").read_bytes()
        for name in ("u8g2_font_wqy12_t_gb2312", "u8g2_font_wqy16_t_gb2312"):
            start = fonts.index(("const uint8_t " + name + "[").encode())
            end = fonts.index(b'";', start) + 2
            bundle.writestr("licenses/" + name + ".c", fonts[start:end] + b"\n")
    digest = hashlib.sha256(archive.read_bytes()).hexdigest()
    archive.with_suffix(".zip.sha256").write_text(f"{digest}  {archive.name}\n", encoding="utf-8")
    print(f"Firmware bundle: {archive}")


env.Depends("$BUILD_DIR/${PROGNAME}.bin", [str(project / name) for name in
    ("package.py", "flash.py", "configure.py", "README.md", "THIRD_PARTY_NOTICES.md",
     "audio/.pio/build/windows-audio/firmware.bin", "audio/.pio/build/windows-audio/partitions.bin")])
env.AddPostAction("$BUILD_DIR/${PROGNAME}.bin", package)
