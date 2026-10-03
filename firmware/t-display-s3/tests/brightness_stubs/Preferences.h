#pragma once
#include <assert.h>
#include <stdint.h>
#include <string.h>
#include <stddef.h>

inline int storedBrightness = -1;
inline unsigned brightnessWrites = 0;
inline bool storageAvailable = true, writesSucceed = true;
inline int storedSleepSeconds = -1;
inline unsigned sleepWrites = 0;
inline bool resumeAudio = false;

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
    uint16_t getUShort(const char *key, uint16_t fallback)
    {
        assert(strcmp(key, "sleep-seconds") == 0);
        return storedSleepSeconds < 0 ? fallback : storedSleepSeconds;
    }
    size_t putUShort(const char *key, uint16_t value)
    {
        assert(!readOnly_ && strcmp(key, "sleep-seconds") == 0);
        ++sleepWrites;
        if (!writesSucceed) return 0;
        storedSleepSeconds = value;
        return sizeof(uint16_t);
    }
    bool getBool(const char *key, bool)
    {
        assert(strcmp(key, "sleep-audio") == 0);
        return resumeAudio;
    }
    size_t putBool(const char *key, bool value)
    {
        assert(!readOnly_ && strcmp(key, "sleep-audio") == 0);
        if (!writesSucceed) return 0;
        resumeAudio = value;
        return 1;
    }
    bool remove(const char *key)
    {
        assert(!readOnly_ && strcmp(key, "sleep-audio") == 0);
        if (!writesSucceed) return false;
        resumeAudio = false;
        return true;
    }
    void end() {}
private:
    bool readOnly_ = true;
};
