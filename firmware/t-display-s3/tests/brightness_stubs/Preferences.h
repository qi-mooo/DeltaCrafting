#pragma once
#include <assert.h>
#include <stdint.h>
#include <string.h>
#include <stddef.h>

inline int storedBrightness = -1;
inline unsigned brightnessWrites = 0;
inline bool storageAvailable = true, writesSucceed = true;

class Preferences {
public:
    bool begin(const char *name, bool readOnly)
    {
        assert(strcmp(name, "delta-display") == 0);
        readOnly_ = readOnly;
        return storageAvailable;
    }
    uint8_t getUChar(const char *key, uint8_t fallback)
    {
        assert(strcmp(key, "brightness") == 0);
        return storedBrightness < 0 ? fallback : storedBrightness;
    }
    size_t putUChar(const char *key, uint8_t value)
    {
        assert(!readOnly_ && strcmp(key, "brightness") == 0);
        ++brightnessWrites;
        if (!writesSucceed) return 0;
        storedBrightness = value;
        return 1;
    }
    void end() {}
private:
    bool readOnly_ = true;
};
