#pragma once
#include <Arduino.h>
#include <ArduinoJson.h>
#include <vector>
#include "harp_update.h"
#include "harp_discovery.h"

namespace HarpControl {
enum class Action : uint8_t { Refresh, Play, Pause, Resume, Stop, Usb, Speed, Loop, Songs, CheckFirmware, InstallFirmware, Scan, Connect };
struct Song { String path, name; };
struct State {
    bool configured = false, online = false, pending = false, busy = false, loop = false;
    bool sharedLibrary = false;
    String state, file, storage, error;
    int speed = 100, offset = 0, total = 0;
    uint32_t elapsedMs = 0, durationMs = 0, countdownMs = 0;
    std::vector<Song> songs;
    std::vector<HarpNetwork::Device> devices;
    String discoveryDetail;
    HarpUpdate::View firmware;
};
void begin();
void visible(bool value);
State snapshot();
bool submit(Action action, const String &file = "", int value = 0);
bool configure(JsonDocument &doc, String &error);
bool configured();
}
