# Third-party components

TinyMidiLoader by Bernhard Schelling is vendored under the ZLIB license, retained
in `lib/TinyMidiLoader/src/tml.h`.
Upstream: https://github.com/schellingb/TinySoundFont
Commit: `853a0a171759f1ddba0de1442133a75912bbeffa`.
Local changes retain track IDs, validate metadata and SysEx lengths, handle
allocation failures, initialize messages, reset running status between tracks,
reject zero tempo, bound duration, widen cumulative ticks, and permit cooperative
cancellation. The wrapper caps memory and input size. This is an altered source.

SdFat 2.2.3 by Bill Greiman (MIT):
https://github.com/greiman/SdFat/tree/2.2.3
ArduinoJson 6.21.5 by Benoit Blanchon (MIT):
https://github.com/bblanchon/ArduinoJson/tree/v6.21.5

Lucide 0.468.0 icons (ISC), embedded as a local SVG symbol sprite:
https://github.com/lucide-icons/lucide/tree/0.468.0
License retained in `licenses/Lucide.txt`.

PlatformIO espressif32 6.5.0 uses Arduino-ESP32 2.0.14 (LGPL-2.1) and ESP-IDF /
TinyUSB components under their respective licenses. Corresponding sources:
https://github.com/platformio/platform-espressif32/tree/v6.5.0
https://github.com/espressif/arduino-esp32/tree/2.0.14

The bundle contains project source, ELF and pinned build configuration. Dependency
licenses are included in `licenses/`. Keyboard mappings were independently
implemented from observed HarpAutoPlayer behavior; its executable or decompiled
C# source is not distributed in this project.
