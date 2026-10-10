"""Exercise the actual bounded MIDI library and keyboard mapping under sanitizers."""
from pathlib import Path
import os
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory(prefix="delta-harp-test-") as temporary:
    binary = Path(temporary) / "core"
    subprocess.run([os.environ.get("CXX", "c++"), "-std=c++17", "-g",
                    "-fsanitize=address,undefined", "-fno-omit-frame-pointer",
                    "-I" + str(root / "include"), "-I" + str(root / "lib/TinyMidiLoader/src"),
                    str(root / "src/midi_file.cpp"), str(root / "tests/core_test.cpp"),
                    "-o", str(binary)], check=True)
    subprocess.run([str(binary)], check=True, timeout=30)
