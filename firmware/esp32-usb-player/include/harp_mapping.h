#pragma once
#include <stdint.h>
#include <math.h>
#include <string.h>

namespace Harp {
struct Key {
    uint8_t usage = 0, mouse = 0;
    bool operator==(const Key &o) const { return usage == o.usage && mouse == o.mouse; }
    bool operator!=(const Key &o) const { return !(*this == o); }
};
inline Key mapPitch(int pitch, int baseOctave) {
    if (pitch < 0 || pitch > 127) return {};
    int octave = pitch / 12 - 1 - baseOctave, pc = pitch % 12;
    if (octave < -1 || octave > 2 || (octave == 2 && pc > 1)) return {};
    // USB HID physical usages, deliberately independent of host keyboard layout.
    const uint8_t usage[] = {0x1d,0x1d,0x1b,0x1b,0x06,0x19,0x19,0x05,0x05,0x11,0x11,0x10};
    Key result;
    result.usage = octave == 2 ? 0x36 : usage[pc];
    result.mouse = octave < 0 ? 1 : octave > 0 ? 2 : 0;
    if (pc == 1 || pc == 3 || pc == 6 || pc == 8 || pc == 10) result.mouse |= 4;
    return result;
}
inline int autoOctave(const uint32_t histogram[128], int transpose) {
    double sum = 0; uint32_t total = 0, bestCount = 0;
    int lo = 10, hi = -1, best = 4;
    for (int p = 0; p < 128; ++p) if (histogram[p] && p + transpose >= 0 && p + transpose < 128) {
        int o = (p + transpose) / 12 - 1;
        lo = o < lo ? o : lo; hi = o > hi ? o : hi;
        sum += double(o) * histogram[p]; total += histogram[p];
    }
    if (!total) return 4;
    double mean = sum / total;
    for (int b = lo; b <= hi; ++b) {
        uint32_t n = 0;
        for (int p = 0; p < 128; ++p) if (mapPitch(p + transpose, b).usage) n += histogram[p];
        if (n > bestCount || (n == bestCount && fabs(b - mean) < fabs(best - mean))) { best = b; bestCount = n; }
    }
    return best;
}
// A single selected track/channel. Overlapping notes reduce to the highest
// held pitch; sustain/CC123 are observed. State survives pause, HID state does not.
class Voice {
public:
    void clear() { memset(held, 0, sizeof(held)); memset(latched, 0, sizeof(latched)); sustain = false; }
    void event(uint8_t type, uint8_t a, uint8_t b) {
        if (a >= 128) return;
        if (type == 0x90 && b) { if (held[a] < 255) ++held[a]; latched[a] = false; }
        else if (type == 0x80 || type == 0x90) {
            if (held[a]) --held[a];
            if (!held[a]) latched[a] = sustain;
        } else if (type == 0xb0 && a == 64) {
            sustain = b >= 64;
            if (!sustain) memset(latched, 0, sizeof(latched));
        } else if (type == 0xb0 && (a == 120 || a == 123)) clear();
        else if (type == 0xb0 && a == 121) { sustain = false; memset(latched, 0, sizeof(latched)); }
    }
    int highest() const {
        for (int p = 127; p >= 0; --p) if (held[p] || latched[p]) return p;
        return -1;
    }
private:
    uint8_t held[128] = {};
    bool latched[128] = {}, sustain = false;
};
}
