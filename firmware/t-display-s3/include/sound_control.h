#pragma once
#include <ArduinoJson.h>
#include <HTTPClient.h>
#include <WiFi.h>
#include <http_parser.h>
#include "sound_state.h"

namespace SoundControl {
inline String url(const String &pairedUrl)
{
    http_parser_url parsed{};
    if (!pairedUrl.startsWith("http://") || http_parser_parse_url(pairedUrl.c_str(), pairedUrl.length(), 0, &parsed)
        || !(parsed.field_set & (1 << UF_HOST))
        || (parsed.field_set & ((1 << UF_USERINFO) | (1 << UF_QUERY) | (1 << UF_FRAGMENT)))) return "";
    const auto &field = parsed.field_data[UF_HOST];
    String host = pairedUrl.substring(field.off, field.off + field.len);
    if (host.indexOf(':') >= 0) host = "[" + host + "]";
    return "http://" + host + ":8765";
}

inline State request(const String &baseUrl, Action action = Action::Read)
{
    State state;
    if (baseUrl.isEmpty() || WiFi.status() != WL_CONNECTED) return state;
    const char *path = action == Action::Read ? "/api/mute"
        : action == Action::ToggleMute ? "/api/mute/toggle"
        : action == Action::VolumeUp ? "/api/volume/up" : "/api/volume/down";
    WiFiClient client;
    HTTPClient http;
    http.setConnectTimeout(500);
    http.setTimeout(800);
    http.setReuse(false);
    http.useHTTP10(true);
    if (!http.begin(client, baseUrl + path)) return state;
    if (action == Action::Read) state.httpStatus = http.GET();
    else {
        http.addHeader("Content-Type", "application/json");
        state.httpStatus = http.POST("{}"); // Never retry a mutating request.
    }
    if (state.httpStatus == HTTP_CODE_OK && http.getSize() >= 0 && http.getSize() <= 4096) {
        StaticJsonDocument<128> filter;
        filter["muted"] = true; filter["volumePercent"] = true;
        StaticJsonDocument<256> doc;
        client.setTimeout(800);
        if (!deserializeJson(doc, http.getStream(), DeserializationOption::Filter(filter))) apply(doc.as<JsonVariantConst>(), state);
    }
    http.end();
    state.checkedAt = millis();
    return state;
}
}
