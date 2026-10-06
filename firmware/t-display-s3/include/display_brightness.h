#pragma once
#include <Arduino.h>
#include <Preferences.h>

// Both programs use the same NVS value; previewing never writes flash.
class DisplayBrightness {
public:
    static constexpr uint8_t Levels = 10;

    explicit DisplayBrightness(uint8_t pin) : pin_(pin) {}

    void begin()
    {
        Preferences preferences;
        if (preferences.begin("delta-display", true)) {
            uint8_t stored = preferences.getUChar("brightness", 100);
            if (stored >= 10 && stored <= 100 && stored % 10 == 0) saved_ = stored;
            preferences.end();
        }
        current_ = saved_;
        pinMode(pin_, OUTPUT);
        apply();
    }

    uint8_t savedPercent() const { return saved_; }
    uint8_t savedRow() const { return saved_ / 10 - 1; }

    void preview(uint8_t row)
    {
        // The return row previews the saved value instead of turning the screen off.
        uint8_t value = row < Levels ? (row + 1) * 10 : saved_;
        if (current_ == value) return;
        current_ = value;
        apply();
    }

    bool save()
    {
        if (current_ == saved_) return true;
        Preferences preferences;
        if (!preferences.begin("delta-display", false)) return false;
        bool ok = preferences.putUChar("brightness", current_) == 1;
        preferences.end();
        if (ok) saved_ = current_;
        return ok;
    }

    void cancel() { preview(Levels); }

    void setScreenOn(bool on)
    {
        on_ = on;
        apply();
    }

private:
    void apply() { analogWrite(pin_, on_ ? (current_ * 255 + 50) / 100 : 0); }
    uint8_t pin_, saved_ = 100, current_ = 100;
    bool on_ = true;
};
