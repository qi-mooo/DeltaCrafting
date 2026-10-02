#pragma once
#include <U8g2lib.h>
#include <TFT_eSPI.h>
#include <string.h>
#include "ui_marquee.h"

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
        previousImage = currentImage;
        offset = forward ? int(Width) : -int(Width);
    }

    float offset = 0;

    void drawHomeFocus(int x, int y, int width, int height)
    {
        setDrawColor(1);
        for (int i = 0; i < 3; ++i)
            drawFrame(x + i, y + i, width - 2 * i, height - 2 * i);
        for (int side = 0; side < 2; ++side) {
            int dx = side * (width - 18);
            drawBox(x + dx, y, 18, 4);
            drawBox(x + dx, y + height - 4, 18, 4);
        }
    }

    void drawFacilityProgress(int x, int y, int pixels)
    {
        // Reserve a black gutter even while the moving focus crosses this row.
        setDrawColor(0);
        drawBox(x - 1, y - 1, 142, 6);
        setDrawColor(1);
        drawFrame(x, y, 140, 4);
        if (pixels > 0) drawBox(x + 1, y + 1, pixels, 2);
    }

    void drawScrollingText(int x, int baseline, int width, const char *text, int scroll)
    {
        setDrawColor(1);
        setClipWindow(x, baseline - 16, x + width, baseline + 2);
        drawUTF8(x - scroll, baseline, text);
        int textWidth = getUTF8Width(text);
        if (textWidth > width) {
            // A second copy follows the tail, so wrapping never reverses or jumps.
            int nextX = x - scroll + textWidth + UiMarquee::GapPixels;
            if (nextX < x + width) drawUTF8(nextX, baseline, text);
        }
        setMaxClipWindow();
    }

    void clearImage() { currentImage.visible = false; }
    void setImage(int x, int y, const uint16_t *data)
    {
        currentImage.x = x; currentImage.y = y; currentImage.visible = true;
        memcpy(currentImage.pixels, data, sizeof(currentImage.pixels));
    }


    void readRow(unsigned y, uint16_t *row) const
    {
        int shift = int(offset);
        for (int x = 0; x < int(Width); ++x) {
            int sourceX = x - shift;
            const uint8_t *source = pixels;
            const Image *picture = &currentImage;
            if (sourceX < 0 || sourceX >= int(Width)) {
                source = previous;
                picture = &previousImage;
                sourceX += shift > 0 ? int(Width) : -int(Width);
            }
            row[x] = source[(y / 8) * Width + sourceX] & (1u << (y % 8)) ? 0xFFFF : 0;
            if (picture->visible && sourceX >= picture->x && sourceX < picture->x + 96
                && int(y) >= picture->y && int(y) < picture->y + 96)
                row[x] = picture->pixels[(y - picture->y) * 96 + sourceX - picture->x];
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
    struct Image { uint16_t pixels[96 * 96]{}; int x = 0, y = 0; bool visible = false; };
    Image currentImage, previousImage;
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
