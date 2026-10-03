#pragma once
#include <Arduino.h>
#include "ota_transaction.h"

namespace DeltaOta {
struct Image { uint32_t size = 0; String sha256; };
struct Manifest { String id, version; uint32_t revision = 0; Image monitor, audio; };
struct Config { String ssid, password, url, key; };
struct Job { Stage stage = Stage::None; Manifest manifest; };
using Progress = void (*)(const char *phase, int percent, const String &detail);

bool loadConfig(Config &config, String &error);
Job loadJob();
bool check(const Config &config, Manifest &manifest, String &error);
bool installAudio(const Config &config, const Manifest &manifest, Progress progress, String &error);
// Called before the audio program selects the monitor for RESET.
bool installMonitor(const Config &config, const Job &job, Progress progress, String &error);
// Finish a completed transaction, or resume the handoff after a power loss.
bool monitorBoot(String &error);
}
