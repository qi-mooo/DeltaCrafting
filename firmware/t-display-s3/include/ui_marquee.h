#pragma once
#include <stdint.h>
#include <string>

class UiMarquee {
public:
    static constexpr int GapPixels = 24;
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
        if (textWidth <= viewportWidth) return 0;
        constexpr uint32_t msPerPixel = 40;
        return int(uint32_t(now - startedAt) / msPerPixel % uint32_t(textWidth + GapPixels));
    }

private:
    std::string text;
    int width = 0, viewport = 0;
    uint32_t startedAt = 0;
    bool active = false;
};
