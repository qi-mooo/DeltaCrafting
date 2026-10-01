#pragma once
#include <stdint.h>
#include <string>

class UiMarquee {
public:
    void reset() { active = false; }

    int offset(const char *value, int textWidth, int viewportWidth, uint32_t now)
    {
        if (!active || text != value || width != textWidth || viewport != viewportWidth) {
            text = value;
            width = textWidth;
            viewport = viewportWidth;
            startedAt = now;
            active = true;
        }
        int overflow = textWidth - viewportWidth;
        if (overflow <= 0) return 0;
        constexpr uint32_t pause = 1200, msPerPixel = 40;
        uint32_t travel = uint32_t(overflow) * msPerPixel;
        uint32_t phase = uint32_t(now - startedAt) % (2 * (pause + travel));
        if (phase < pause) return 0;
        phase -= pause;
        if (phase < travel) return int(phase / msPerPixel);
        phase -= travel;
        if (phase < pause) return overflow;
        return overflow - int((phase - pause) / msPerPixel);
    }

private:
    std::string text;
    int width = 0, viewport = 0;
    uint32_t startedAt = 0;
    bool active = false;
};
