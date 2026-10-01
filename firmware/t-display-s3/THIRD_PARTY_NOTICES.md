# Third-party components

The build uses LILYGO T-Display-S3 board definitions and display drivers pinned at
https://github.com/Xinyuan-LilyGO/T-Display-S3/tree/5c7b97a42e6ed4ec299004f0578c097ece412d6f
(MIT; bundled license), including TFT_eSPI (FreeBSD; bundled license).

Axeuh_UI 1.0.1 is pinned at e5185d2c767732df039538129c46a4ea39223abe
(Apache-2.0; bundled license): https://github.com/Axeuh/Axeuh_UI

ArduinoJson 6.21.5 (MIT) and U8g2 2.35.30 (BSD) licenses are in `licenses/`.
The WenQuanYi Bitmap Song fonts are copyright (C) 2004-2010 WenQuanYi Project,
Board of Trustees and Qianqian Fang, licensed under GPL v2 with font embedding exception.
The U8g2 font arrays are included in `licenses/u8g2_font_wqy*_t_gb2312.c`.
Original font source and notices:
https://github.com/olikraus/u8g2/blob/2.35.30/tools/font/bdf/wenquanyi_12pt.bdf
https://github.com/olikraus/u8g2/blob/2.35.30/tools/font/bdf/wenquanyi_9pt.bdf
U8g2 source: https://github.com/olikraus/u8g2/tree/2.35.30

PlatformIO espressif32 6.5.0 uses Arduino-ESP32 2.0.14 (LGPL-2.1) and ESP-IDF
components with their respective licenses. Corresponding source and notices:
https://github.com/espressif/arduino-esp32/tree/2.0.14
https://github.com/platformio/platform-espressif32/tree/v6.5.0

The audio program is adapted from the local T-Display-S3 WindowsMuteController
example (AudioBridge.cpp, AudioBridge.h, AudioProtocol.h and its sketch).
It uses Arduino-ESP32 3.3.9 (LGPL-2.1), including USB Audio/TinyUSB and ESP-IDF
components under their respective licenses:
https://github.com/espressif/arduino-esp32/tree/3.3.9
https://github.com/pioarduino/platform-espressif32/releases/tag/55.03.39

The bundle includes both programs' source and ELFs for debugging. Build instructions
and pinned dependencies are in README.md, source/platformio.ini and
source/audio/platformio.ini.
