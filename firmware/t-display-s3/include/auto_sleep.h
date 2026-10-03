#pragma once
#include <stdint.h>
#include <Preferences.h>

class AutoSleep {
public:
    static constexpr uint8_t Count = 6;
    static constexpr uint16_t secondsAt(uint8_t row)
    {
        return row == 0 ? 0 : row == 1 ? 30 : row == 2 ? 60 : row == 3 ? 120 : row == 4 ? 300 : 600;
    }
    void begin(uint32_t now)
    {
        Preferences prefs;
        if (prefs.begin("delta-display", true)) {
            uint16_t value = prefs.getUShort("sleep-seconds", 60);
            for (uint8_t i = 0; i < Count; ++i) if (secondsAt(i) == value) row_ = i;
            prefs.end();
        }
        activity(now);
    }
    uint8_t row() const { return row_; }
    bool save(uint8_t row, uint32_t now)
    {
        if (row >= Count) return false;
        if (row != row_) {
            Preferences prefs;
            if (!prefs.begin("delta-display", false)) return false;
            bool ok = prefs.putUShort("sleep-seconds", secondsAt(row)) == sizeof(uint16_t);
            prefs.end();
            if (!ok) return false;
            row_ = row;
        }
        activity(now);
        return true;
    }
    void activity(uint32_t now) { lastActivity_ = now; }
    bool due(uint32_t now, bool buttonHeld, bool inhibited)
    {
        if (buttonHeld || inhibited) activity(now);
        return !buttonHeld && !inhibited && secondsAt(row_) != 0
            && uint32_t(now - lastActivity_) >= uint32_t(secondsAt(row_)) * 1000;
    }
private:
    uint8_t row_ = 2;
    uint32_t lastActivity_ = 0;
};

// Also suppress release-triggered and long-press actions after a wake/startup.
class WakeButtonGate {
public:
    bool allow(bool bothReleased, uint32_t now)
    {
        if (ready_) return true;
        if (!bothReleased) { releaseSeen_ = false; return false; }
        if (!releaseSeen_) { releaseSeen_ = true; releasedAt_ = now; }
        if (uint32_t(now - releasedAt_) >= 35) ready_ = true;
        return false;
    }
private:
    bool ready_ = false, releaseSeen_ = false;
    uint32_t releasedAt_ = 0;
};

namespace SleepResume {
// Boot selection remains the monitor, so RESET always exits audio. A deep-sleep
// wake consumes this one-shot marker before relaunching audio.
inline bool save(bool audio)
{
    if (!audio) return true;
    Preferences prefs;
    if (!prefs.begin("delta-display", false)) return false;
    bool ok = prefs.putBool("sleep-audio", true) == 1;
    prefs.end();
    return ok;
}
inline bool consume(bool deepWake)
{
    Preferences prefs;
    if (!prefs.begin("delta-display", false)) return false;
    bool audio = prefs.getBool("sleep-audio", false);
    bool cleared = !audio || prefs.remove("sleep-audio");
    prefs.end();
    return deepWake && audio && cleared;
}
}
