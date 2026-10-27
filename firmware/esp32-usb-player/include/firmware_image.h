#pragma once
#include <stdint.h>
#include <stddef.h>
#include "firmware_identity.h"

// Streaming identity check; ESP-IDF separately verifies the complete image.
class FirmwareImageCheck {
public:
    void add(const uint8_t *data, size_t n) {
        for (size_t i=0;i<n;++i) {
            if (count<sizeof(header)) header[count]=data[i];
            ++count;
            if (data[i]==uint8_t(marker[matched])) {
                if (++matched==sizeof(marker)-1) { found=true; matched=0; }
            } else matched=data[i]==uint8_t(marker[0])?1:0;
        }
    }
    bool valid() const { return count>=sizeof(header) && header[0]==0xe9 && header[12]==9 && header[13]==0 && found; }
private:
    static constexpr char marker[]=HARP_IMAGE_MARKER;
    uint8_t header[24]={};
    size_t count=0, matched=0;
    bool found=false;
};
