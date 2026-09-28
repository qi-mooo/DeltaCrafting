#pragma once
#include <U8g2lib.h>
#include <TFT_eSPI.h>
#include <string.h>

// Axeuh_UI renders into U8g2's vertical-bit tiles; TFT_eSPI owns the LCD bus.
class MonoDisplay : public U8G2 {
public:
    static constexpr unsigned Width = 320, Height = 170, TileRows = (Height + 7) / 8;

    MonoDisplay()
    {
        u8g2_SetupDisplay(getU8g2(), displayCallback, u8x8_cad_empty, u8x8_byte_empty, gpioCallback);
        u8g2_SetupBuffer(getU8g2(), pixels, TileRows, u8g2_ll_hvline_vertical_top_lsb, U8G2_R0);
    }

    void startTransition(bool forward)
    {
        memcpy(previous, pixels, sizeof(pixels));
        offset = forward ? int(Width) : -int(Width);
    }

    float offset = 0;

    void readRow(unsigned y, uint16_t *row) const
    {
        int shift = int(offset);
        for (int x = 0; x < int(Width); ++x) {
            int sourceX = x - shift;
            const uint8_t *source = pixels;
            if (sourceX < 0 || sourceX >= int(Width)) {
                source = previous;
                sourceX += shift > 0 ? int(Width) : -int(Width);
            }
            row[x] = source[(y / 8) * Width + sourceX] & (1u << (y % 8)) ? 0xFFFF : 0;
        }
    }

    void present(TFT_eSPI &display)
    {
        uint16_t row[Width];
        display.startWrite();
        for (unsigned y = 0; y < Height; ++y) {
            readRow(y, row);
            display.pushImage(0, y, Width, 1, row);
        }
        display.endWrite();
    }

private:
    uint8_t pixels[Width * TileRows]{};
    uint8_t previous[Width * TileRows]{};

    static uint8_t gpioCallback(u8x8_t *, uint8_t, uint8_t, void *) { return 1; }
    static uint8_t displayCallback(u8x8_t *screen, uint8_t message, uint8_t, void *)
    {
        static const u8x8_display_info_t info = [] {
            u8x8_display_info_t result{};
            result.tile_width = Width / 8;
            result.tile_height = TileRows;
            result.pixel_width = Width;
            result.pixel_height = Height;
            return result;
        }();
        if (message == U8X8_MSG_DISPLAY_SETUP_MEMORY)
            u8x8_d_helper_display_setup_memory(screen, &info);
        return 1;
    }
};
