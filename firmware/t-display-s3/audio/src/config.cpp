#include "config.h"
#include <ArduinoJson.h>
#include <Preferences.h>

namespace AudioMode {
namespace { String wifiSsid, wifiPassword; }
bool loadWifi()
{
    Preferences preferences;
    if (!preferences.begin("delta-monitor", true)) return false;
    String saved = preferences.getString("config", "");
    preferences.end();
    StaticJsonDocument<2048> doc;
    if (saved.isEmpty() || deserializeJson(doc, saved)
        || !doc["wifiSsid"].is<const char *>() || !doc["wifiPassword"].is<const char *>()) return false;
    wifiSsid = doc["wifiSsid"].as<String>();
    wifiPassword = doc["wifiPassword"].as<String>();
    return !wifiSsid.isEmpty() && wifiSsid.length() <= 32 && wifiPassword.length() <= 64;
}
const String &ssid() { return wifiSsid; }
const String &password() { return wifiPassword; }
}
