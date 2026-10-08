"""Test navigation, button debounce and the real U8g2 display/transition bridge."""
from concurrent.futures import ThreadPoolExecutor
import os
from pathlib import Path
import subprocess
import tempfile

project = Path(__file__).resolve().parents[1]
u8g2 = project / ".pio/libdeps/deltacrafter-monitor/U8g2/src"
cc, cxx = os.environ.get("CC", "cc"), os.environ.get("CXX", "c++")

def run(args):
    subprocess.run([str(arg) for arg in args], check=True)

with tempfile.TemporaryDirectory(prefix="s3-ui-test-") as temporary:
    build = Path(temporary)
    run([cxx, "-std=c++17", "-I" + str(project / "include"),
         project / "tests/ui_state_test.cpp", "-o", build / "navigation"])
    run([build / "navigation"])
    run([cxx, "-std=c++17", "-I" + str(project / "tests/brightness_stubs"),
         "-I" + str(project / "include"), project / "tests/display_brightness_test.cpp",
         "-o", build / "brightness"])
    run([build / "brightness"])
    run([cxx, "-std=c++17", "-I" + str(project / "tests/brightness_stubs"),
         "-I" + str(project / "include"), project / "tests/auto_sleep_test.cpp",
         "-o", build / "auto-sleep"])
    run([build / "auto-sleep"])
    run([cxx, "-std=c++17", "-I" + str(project / "include"),
         project / "tests/ui_resume_test.cpp", "-o", build / "ui-resume"])
    run([build / "ui-resume"])
    run([cxx, "-std=c++17", "-I" + str(project / "include"),
         "-I" + str(project / ".pio/libdeps/deltacrafter-monitor/ArduinoJson/src"),
         project / "tests/sound_control_test.cpp", "-o", build / "sound-control"])
    run([build / "sound-control"])
    run([cxx, "-std=c++17", "-I" + str(project / "tests/stubs"),
         "-I" + str(project / "include"), project / "tests/program_switch_test.cpp",
         "-o", build / "program-switch"])
    run([build / "program-switch"])
    run([cxx, "-std=c++17", "-I" + str(project / "lib/DeltaOta/src"),
         project / "tests/ota_transaction_test.cpp", "-o", build / "ota-transaction"])
    run([build / "ota-transaction"])

    sources = sorted((u8g2 / "clib").glob("*.c"))
    sources = [s for s in sources if s.name != "u8x8_fonts.c"]
    def compile_source(source):
        target = build / (source.stem + ".o")
        run([cc, "-w", "-DU8G2_USE_LARGE_FONTS", "-c", source, "-o", target])
        return target
    with ThreadPoolExecutor(max_workers=4) as pool:
        objects = list(pool.map(compile_source, sources))
    run(["ar", "rcs", build / "u8g2.a", *objects])
    run([cxx, "-std=c++17", "-I" + str(u8g2),
         "-I" + str(project / "tests/stubs"), "-I" + str(project / "include"),
         project / "tests/mono_display_test.cpp", build / "u8g2.a", "-o", build / "display"])
    run([build / "display"])
print("Navigation, buttons, brightness, deep-sleep timing/wake/page resume, sound state/USB host gating, program switching/RESET, interrupted OTA recovery, display bounds and transitions passed.")
