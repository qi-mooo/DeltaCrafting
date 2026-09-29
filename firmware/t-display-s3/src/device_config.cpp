#include "device_config.h"
#include "config.h"
#include <ArduinoJson.h>
#include <Preferences.h>
#include <WiFi.h>

namespace DeviceConfig {
namespace {
String wifiSsid, wifiPassword, url, key;
String serialLine;
bool overflow = false;
bool apiOnline = false;
String apiError;
int facilities = 0;
void (*screenCapture)() = nullptr;

bool validKey(const String &value)
{
    if (value.length() < 32 || value.length() > 128) return false;
    for (unsigned i = 0; i < value.length(); ++i) {
        char c = value[i];
        if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')))
            return false;
    }
    return true;
}

bool validUrl(const String &value)
{
    if (!value.startsWith("http://") || value.length() <= 7 || value.length() > 192) return false;
    for (unsigned i = 7; i < value.length(); ++i)
        if (value[i] <= ' ' || value[i] == '@' || value[i] == '?' || value[i] == '#') return false;
    String authority = value.substring(7);
    while (authority.endsWith("/")) authority.remove(authority.length() - 1);
    return !authority.isEmpty() && authority.indexOf('/') < 0;
}

void processLine(const String &line)
{
    StaticJsonDocument<2048> doc;
    if (deserializeJson(doc, line)) {
        Serial.println("{\"ok\":false,\"error\":\"invalid_json\"}");
        return;
    }
    const char *command = doc["command"] | "";
    if (strcmp(command, "screen") == 0 && screenCapture) {
        screenCapture();
        return;
    }
    if (strcmp(command, "info") == 0) {
        doc.clear();
        doc["ok"] = true;
        doc["firmware"] = "axeuh-tools-v9";
        doc["build"] = __DATE__ " " __TIME__;
        doc["controls"] = "GPIO0=confirm,GPIO14=cycle";
        doc["configured"] = valid();
        doc["wifiConnected"] = WiFi.status() == WL_CONNECTED;
        doc["ip"] = WiFi.localIP().toString();
        doc["apiOnline"] = apiOnline;
        doc["error"] = apiError;
        doc["facilities"] = facilities;
        doc["freeHeap"] = ESP.getFreeHeap();
        doc["uptimeMs"] = millis();
        serializeJson(doc, Serial);
        Serial.println();
        return;
    }
    if (strcmp(command, "configure") != 0 || !doc["wifiSsid"].is<const char *>()
        || !doc["wifiPassword"].is<const char *>() || !doc["baseUrl"].is<const char *>()
        || !doc["apiKey"].is<const char *>()) {
        Serial.println("{\"ok\":false,\"error\":\"invalid_command_or_fields\"}");
        return;
    }
    String newSsid = doc["wifiSsid"].as<String>();
    String newPassword = doc["wifiPassword"].as<String>();
    String newUrl = doc["baseUrl"].as<String>();
    String newKey = doc["apiKey"].as<String>();
    if (newSsid.isEmpty() || newSsid.length() > 32 || newPassword.length() > 64
        || !validUrl(newUrl) || !validKey(newKey)) {
        Serial.println("{\"ok\":false,\"error\":\"invalid_configuration\"}");
        return;
    }
    // Store one JSON blob so an interrupted save cannot mix old and new credentials.
    doc.remove("command");
    String saved;
    serializeJson(doc, saved);
    Preferences preferences;
    if (!preferences.begin("delta-monitor", false)) {
        Serial.println("{\"ok\":false,\"error\":\"storage_unavailable\"}");
        return;
    }
    bool ok = preferences.putString("config", saved) == saved.length();
    preferences.end();
    if (!ok) {
        Serial.println("{\"ok\":false,\"error\":\"storage_write_failed\"}");
        return;
    }
    Serial.println("{\"ok\":true,\"restart\":true}");
    Serial.flush();
    delay(300);
    ESP.restart();
}
}

void load()
{
    wifiSsid = DELTA_WIFI_SSID;
    wifiPassword = DELTA_WIFI_PASSWORD;
    url = DELTA_BASE_URL;
    key = DELTA_API_KEY;
    Preferences preferences;
    if (!preferences.begin("delta-monitor", true)) return;
    String saved = preferences.getString("config", "");
    preferences.end();
    StaticJsonDocument<2048> doc;
    if (saved.isEmpty() || deserializeJson(doc, saved)) return;
    if (!doc["wifiSsid"].is<const char *>() || !doc["wifiPassword"].is<const char *>()
        || !doc["baseUrl"].is<const char *>() || !doc["apiKey"].is<const char *>()) return;
    wifiSsid = doc["wifiSsid"].as<String>();
    wifiPassword = doc["wifiPassword"].as<String>();
    url = doc["baseUrl"].as<String>();
    key = doc["apiKey"].as<String>();
}

void handleSerial()
{
    // Per-loop budget keeps display/buttons responsive even during a malformed transfer.
    for (int count = 0; count < 128 && Serial.available(); ++count) {
        char c = static_cast<char>(Serial.read());
        if (c == '\r') continue;
        if (c == '\n') {
            if (overflow) Serial.println("{\"ok\":false,\"error\":\"line_too_long\"}");
            else if (!serialLine.isEmpty()) processLine(serialLine);
            serialLine = "";
            overflow = false;
        } else if (serialLine.length() < 2048) {
            serialLine += c;
        } else overflow = true;
    }
}

// Called and read only from the Arduino loop task.
void setScreenCapture(void (*capture)()) { screenCapture = capture; }

void setHealth(bool online, const String &error, int facilityCount)
{
    apiOnline = online;
    apiError = error;
    facilities = facilityCount;
}

bool valid() { return validUrl(url) && validKey(key); }
const String &ssid() { return wifiSsid; }
const String &password() { return wifiPassword; }
const String &baseUrl() { return url; }
const String &apiKey() { return key; }
}
