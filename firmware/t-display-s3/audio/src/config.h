#pragma once
#include <Arduino.h>

namespace AudioMode {
bool loadWifi();
const String &ssid();
const String &password();
}

#define WIFI_SSID AudioMode::ssid().c_str()
#define WIFI_PASSWORD AudioMode::password().c_str()
#define AUDIO_BRIDGE_NAME "T-Display S3"
#define PIN_POWER_ON 15
#define PIN_LCD_BL 38
#define PIN_BATTERY_VOLTAGE 4
#define PIN_BUTTON_LEFT 0
#define PIN_BUTTON_RIGHT 14
