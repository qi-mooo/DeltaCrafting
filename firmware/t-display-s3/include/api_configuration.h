#pragma once
#include <ArduinoJson.h>
#include <algorithm>
#include <cstdio>
#include <cstdlib>
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
    const char *url = request["baseUrl"].as<const char *>();
    if (std::strncmp(url, "http://", 7) != 0) return "invalid_api_configuration";
    const char *host = url + 7;
    const char *colon = std::strrchr(host, ':');
    if (!colon || colon == host || std::strchr(host, ':') != colon) return "invalid_api_configuration";
    for (const char *p = host; *p; ++p)
        if (static_cast<unsigned char>(*p) <= ' ' || *p == '@' || *p == '?' || *p == '#' || *p == '/')
            return "invalid_api_configuration";
    char *portEnd = nullptr;
    long port = std::strtol(colon + 1, &portEnd, 10);
    if (port < 1024 || port > 65535 || portEnd == colon + 1 || *portEnd != '\0')
        return "invalid_api_configuration";
    unsigned octets[4] = {};
    int consumed = 0;
    if (std::sscanf(host, "%u.%u.%u.%u%n", &octets[0], &octets[1], &octets[2], &octets[3], &consumed) != 4
        || host + consumed != colon || std::any_of(octets, octets + 4, [](unsigned value) { return value > 255; }))
        return "invalid_api_configuration";
    if (octets[0] == 0 || octets[0] == 127 || octets[0] >= 224
        || (octets[0] == 169 && octets[1] == 254)) return "invalid_api_configuration";
    const char *key = request["apiKey"].as<const char *>();
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
