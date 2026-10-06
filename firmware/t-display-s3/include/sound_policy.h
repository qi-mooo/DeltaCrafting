#pragma once
#include <stdint.h>

namespace SoundPolicy {
// Detach/suspend closes PCM immediately; mounting is debounced to avoid flaps.
class UsbHostGate {
public:
    bool update(bool present, uint32_t now)
    {
        if (!present) { seen_ = mounted_ = false; return false; }
        if (!seen_) { seen_ = true; since_ = now; }
        if (uint32_t(now - since_) >= 120) mounted_ = true;
        return mounted_;
    }
private:
    bool seen_ = false, mounted_ = false;
    uint32_t since_ = 0;
};
}
