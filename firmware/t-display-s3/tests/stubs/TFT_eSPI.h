#pragma once
#include <stdint.h>
struct TFT_eSPI {
    void startWrite() {}
    void endWrite() {}
    void pushImage(int, int, int, int, uint16_t *) {}
};
