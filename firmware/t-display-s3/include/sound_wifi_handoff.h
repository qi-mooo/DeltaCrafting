#pragma once
#include <Preferences.h>
#include <WiFi.h>
#include <esp_system.h>

namespace SoundWifiHandoff {
// Written once on tool entry, never by background polling. No credentials/IP lease.
inline void save()
{
    Preferences prefs;
    if (!prefs.begin("delta-sound", false)) return;
    if (WiFi.status() == WL_CONNECTED && WiFi.BSSID()) {
        String hint = WiFi.SSID() + "\n" + String(WiFi.channel()) + "\n" + WiFi.BSSIDstr();
        prefs.putString("wifi-hint", hint);
    } else prefs.remove("wifi-hint");
    prefs.end();
}
inline bool begin(const char *ssid, const char *password)
{
    Preferences prefs;
    if (!prefs.begin("delta-sound", false)) return false;
    String hint = prefs.getString("wifi-hint", "");
    prefs.remove("wifi-hint");
    prefs.end();
    if (esp_reset_reason() != ESP_RST_SW || !hint.startsWith(String(ssid) + "\n")) return false;
    int start = strlen(ssid) + 1, split = hint.indexOf('\n', start);
    if (split < 0) return false;
    int channel = hint.substring(start, split).toInt();
    unsigned values[6];
    if (channel < 1 || channel > 14 || sscanf(hint.c_str() + split + 1, "%x:%x:%x:%x:%x:%x",
        &values[0], &values[1], &values[2], &values[3], &values[4], &values[5]) != 6) return false;
    uint8_t bssid[6];
    for (int i = 0; i < 6; ++i) { if (values[i] > 255) return false; bssid[i] = values[i]; }
    WiFi.begin(ssid, password, channel, bssid);
    return true;
}
}
