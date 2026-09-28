# Third-party components

The build uses LILYGO T-Display-S3 board definitions and display drivers pinned at
https://github.com/Xinyuan-LilyGO/T-Display-S3/tree/5c7b97a42e6ed4ec299004f0578c097ece412d6f
(MIT; bundled license), including TFT_eSPI (FreeBSD; bundled license) and Arduino_GFX
(see copyright notices in its source files).

ArduinoJson 6.21.5 (MIT) and U8g2 2.35.30 (BSD) licenses are in `licenses/`.
The Chinese Unifont data used by Arduino_GFX is included in `licenses/Unifont-source.h`.
Copyright (C) 1998-2021 Roman Czyborra, Paul Hardy, Qianqian Fang, Andrew Miller,
Johnnie Weaver, David Corbett, Nils Moskopp, Rebecca Bettencourt, et al.
License: SIL Open Font License 1.1 and GPL version 2 or later with the GNU Font
Embedding Exception. License text: https://unifoundry.com/LICENSE.txt
U8g2 source: https://github.com/olikraus/u8g2/tree/2.35.30

PlatformIO espressif32 6.5.0 uses Arduino-ESP32 2.0.14 (LGPL-2.1) and ESP-IDF
components with their respective licenses. Corresponding source and notices:
https://github.com/espressif/arduino-esp32/tree/2.0.14
https://github.com/platformio/platform-espressif32/tree/v6.5.0

The bundle includes monitor source and an ELF for debugging. Build instructions and
the pinned dependencies are in README.md and source/platformio.ini.
