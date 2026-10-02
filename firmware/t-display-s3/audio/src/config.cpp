#include "config.h"
#include <ArduinoJson.h>
#include <Preferences.h>
#include "sound_control.h"

namespace AudioMode {
namespace { String wifiSsid, wifiPassword, soundUrl; }
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
    soundUrl = SoundControl::url(doc["baseUrl"] | "");
    return !wifiSsid.isEmpty() && wifiSsid.length() <= 32 && wifiPassword.length() <= 64;
}
const String &ssid() { return wifiSsid; }
const String &password() { return wifiPassword; }
const String &controlUrl() { return soundUrl; }
}
