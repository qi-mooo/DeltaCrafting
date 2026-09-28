#pragma once
#include <Arduino.h>

namespace DeviceConfig {
void load();
void handleSerial();
void setScreenCapture(void (*capture)());
void setHealth(bool online, const String &error, int facilityCount);
bool valid();
const String &ssid();
const String &password();
const String &baseUrl();
const String &apiKey();
}
