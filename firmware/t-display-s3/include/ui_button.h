#pragma once
#include <stdint.h>

constexpr uint8_t UI_CONFIRM_PIN = 0;
constexpr uint8_t UI_CYCLE_PIN = 14;

struct UiButton {
    bool raw = true, stable = true, armed = false;
    uint32_t changed = 0;

    bool update(bool released, uint32_t now)
    {
        if (released != raw) { raw = released; changed = now; }
        if (uint32_t(now - changed) < 35) return false;
        if (stable == released) {
            if (released) armed = true;
            return false;
        }
        stable = released;
        if (released) { armed = true; return false; }
        bool pressed = armed;
        armed = false;
        return pressed;
    }
};
