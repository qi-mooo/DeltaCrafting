#pragma once
// Legacy compile-time example. The release build rejects config.local.h;
// use configure.py to provision Wi-Fi and the pairing key over USB instead.
// 2.4 GHz Wi-Fi. Empty SSID reuses credentials already stored in the board's NVS.
#define DELTA_WIFI_SSID "Your-WiFi"
#define DELTA_WIFI_PASSWORD "Your-WiFi-Password"
// Windows PC LAN IPv4 address; no trailing /api/v1/status.
#define DELTA_BASE_URL "http://192.168.1.100:17890"
// Settings -> T-Display-S3 -> copy pairing key (64 hex characters).
#define DELTA_API_KEY "PASTE_PAIRING_KEY_HERE"
#define DELTA_POLL_MS 3000
// Set 0 for early LCD panels if the new-panel initialization causes display issues.
#define DELTA_LCD_NEW_PANEL 1
