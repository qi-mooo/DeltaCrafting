#pragma once
#include <Arduino.h>

namespace DeviceConfig {
void load();
void handleSerial();
void setScreenCapture(void (*capture)());
void setFirmwareControl(bool (*control)(bool install));
void setHealth(bool online, const String &error, int facilityCount);
void setSoundHealth(bool online);
void setStackHealth(uint32_t loopBytes, uint32_t networkBytes);
bool valid();
const String &ssid();
const String &password();
const String &baseUrl();
const String &apiKey();
}
