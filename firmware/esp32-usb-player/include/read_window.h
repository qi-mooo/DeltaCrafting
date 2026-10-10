#pragma once
#include <stdint.h>

namespace Harp {
// Read-only filesystem views are valid only while no USB write has occurred.
// Callers serialize sector I/O and epoch changes with the same SD mutex.
struct ReadWindow {
    static constexpr uint32_t QuietMs = 2000, LimitMs = 10000;
    uint32_t epoch = 0, started = 0;
    bool active = false;
    bool begin(uint32_t revision, uint32_t now, uint32_t lastWrite, bool writing) {
        active = !writing && uint32_t(now-lastWrite) >= QuietMs;
        epoch = revision; started = now;
        return active;
    }
    bool valid(uint32_t revision, uint32_t now, bool writing) const {
        return active && !writing && revision == epoch && uint32_t(now-started) < LimitMs;
    }
};
}
