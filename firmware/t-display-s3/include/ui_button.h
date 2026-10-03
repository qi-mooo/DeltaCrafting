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

// Armed only by a new press inside the item picker; entry presses cannot save.
struct UiHoldConfirm {
    bool active = false;
    uint32_t started = 0;
    bool update(bool pressed, bool released, bool eligible, uint32_t now)
    {
        if (released || !eligible) active = false;
        if (pressed && eligible) { active = true; started = now; }
        if (!active || uint32_t(now - started) < 800) return false;
        active = false;
        return true;
    }
};
