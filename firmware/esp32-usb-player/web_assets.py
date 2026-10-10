"""Embed the offline device UI in flash; no CDN or writable SD assets are needed."""
import gzip
from pathlib import Path

Import("env")
root = Path(env["PROJECT_DIR"])
html = (root / "web/index.html").read_text()
html = html.replace("<!-- ICON_SPRITE -->", (root / "web/icons.svg").read_text())
data = gzip.compress(html.encode(), mtime=0)
header = root / "include/web_page.h"
content = "#pragma once\n#include <Arduino.h>\nstatic const uint8_t HARP_WEB_PAGE[] PROGMEM = {\n"
content += ",".join(str(byte) for byte in data) + "\n};\n"
if not header.exists() or header.read_text() != content:
    header.write_text(content)
