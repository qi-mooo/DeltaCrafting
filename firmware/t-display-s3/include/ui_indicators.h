#pragma once
#include <stdint.h>

inline int batteryBars(uint32_t millivolts)
{
    if (millivolts < 2500 || millivolts > 4300) return -1;
    if (millivolts >= 4050) return 4;
    if (millivolts >= 3850) return 3;
    if (millivolts >= 3700) return 2;
    if (millivolts >= 3500) return 1;
    return 0;
}

inline int progressPixels(int32_t remaining, int32_t total, int width)
{
    if (total <= 0 || remaining < 0) return -1;
    if (remaining >= total) return 0;
    return int(int64_t(total - remaining) * width / total);
}
