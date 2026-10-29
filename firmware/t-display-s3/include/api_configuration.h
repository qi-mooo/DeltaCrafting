#pragma once
#include <ArduinoJson.h>
#include <cstring>

namespace ApiConfiguration {
inline bool validText(JsonVariantConst value, size_t minimum, size_t maximum)
{
    if (!value.is<const char *>()) return false;
    JsonString text = value.as<JsonString>();
    return text.size() >= minimum && text.size() <= maximum && std::strlen(text.c_str()) == text.size();
}

// Validate every field before changing the saved document. Omitted Wi-Fi fields are preserved.
inline const char *apply(JsonObjectConst request, JsonObject saved)
{
    if (!validText(request["baseUrl"], 8, 192) || !validText(request["apiKey"], 32, 128))
        return "invalid_api_configuration";
    const char *url = request["baseUrl"];
    if (std::strncmp(url, "http://", 7) != 0) return "invalid_api_configuration";
    for (const char *p = url + 7; *p; ++p)
        if (static_cast<unsigned char>(*p) <= ' ' || *p == '@' || *p == '?' || *p == '#' || *p == '/')
            return "invalid_api_configuration";
    const char *key = request["apiKey"];
    for (const char *p = key; *p; ++p)
        if (!((*p >= '0' && *p <= '9') || (*p >= 'a' && *p <= 'z') || (*p >= 'A' && *p <= 'Z')))
            return "invalid_api_configuration";
    const bool wifi = request.containsKey("wifiSsid") || request.containsKey("wifiPassword");
    if (wifi && (!validText(request["wifiSsid"], 1, 32) || !validText(request["wifiPassword"], 0, 64)))
        return "invalid_wifi_configuration";
    if (!wifi && (!validText(saved["wifiSsid"], 1, 32) || !validText(saved["wifiPassword"], 0, 64)))
        return "wifi_configuration_required";
    saved["baseUrl"] = request["baseUrl"];
    saved["apiKey"] = request["apiKey"];
    if (wifi) { saved["wifiSsid"] = request["wifiSsid"]; saved["wifiPassword"] = request["wifiPassword"]; }
    return nullptr;
}
}
