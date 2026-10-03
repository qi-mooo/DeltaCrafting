#pragma once
#include <ArduinoJson.h>
#include <stdint.h>

namespace SoundControl {
enum class Action { Read, ToggleMute, VolumeUp, VolumeDown };
struct State {
    bool online = false, muted = false;
    int volume = 0, httpStatus = 0;
    uint32_t checkedAt = 0;
};
inline bool fresh(const State &state, uint32_t now)
{
    return state.online && uint32_t(now - state.checkedAt) < 10000;
}
inline void apply(JsonVariantConst doc, State &state)
{
    state.online = false;
    if (!doc["muted"].is<bool>() || !doc["volumePercent"].is<int>()) return;
    int volume = doc["volumePercent"].as<int>();
    if (volume < 0 || volume > 100) return;
    state.volume = volume;
    state.muted = doc["muted"].as<bool>();
    state.online = true;
}
}
