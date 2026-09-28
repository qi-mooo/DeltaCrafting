#pragma once

#if __has_include("config.local.h")
#include "config.local.h"
#endif

#ifndef DELTA_WIFI_SSID
#define DELTA_WIFI_SSID ""
#endif
#ifndef DELTA_WIFI_PASSWORD
#define DELTA_WIFI_PASSWORD ""
#endif
#ifndef DELTA_BASE_URL
#define DELTA_BASE_URL "http://192.168.1.100:17890"
#endif
#ifndef DELTA_API_KEY
#define DELTA_API_KEY ""
#endif
#ifndef DELTA_POLL_MS
#define DELTA_POLL_MS 3000
#endif
#ifndef DELTA_LCD_NEW_PANEL
#define DELTA_LCD_NEW_PANEL 1
#endif

static_assert(DELTA_POLL_MS >= 1000 && DELTA_POLL_MS <= 60000, "Poll interval must be 1000..60000 ms");
