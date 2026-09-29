#include "mono_display.h"
#include <assert.h>
#include <initializer_list>

int main()
{
    MonoDisplay screen;
    screen.begin();
    assert(screen.getDisplayWidth() == 320 && screen.getDisplayHeight() == 170);
    screen.clearBuffer();
    screen.drawPixel(319, 169);
    uint16_t row[320];
    screen.readRow(169, row);
    assert(row[319] == 0xFFFF && row[318] == 0);
    screen.readRow(168, row);
    assert(row[319] == 0);

    screen.clearBuffer();
    screen.drawBox(0, 0, 320, 170);
    screen.startTransition(true);
    screen.clearBuffer();
    for (int shift : {320, 160, 1, 0}) {
        screen.offset = shift;
        screen.readRow(169, row);
        for (int x = 0; x < 320; ++x) assert(row[x] == (x < shift ? 0xFFFF : 0));
    }
    screen.drawBox(0, 0, 320, 170);
    screen.startTransition(false);
    assert(screen.offset == -320);
    screen.clearBuffer();
    for (int shift : {-320, -160, -1, 0}) {
        screen.offset = shift;
        screen.readRow(0, row);
        for (int x = 0; x < 320; ++x) assert(row[x] == (x >= 320 + shift ? 0xFFFF : 0));
    }
    screen.offset = 0;
    screen.clearBuffer();
    uint16_t image[96 * 96];
    for (auto &pixel : image) pixel = 0xF800;
    screen.setImage(10, 38, image);
    screen.readRow(38, row);
    assert(row[9] == 0 && row[10] == 0xF800 && row[105] == 0xF800 && row[106] == 0);
    screen.startTransition(false);
    screen.clearImage();
    screen.offset = -160;
    screen.readRow(38, row);
    assert(row[170] == 0xF800 && row[265] == 0xF800 && row[266] == 0);
    screen.offset = 0;
    screen.readRow(38, row);
    for (auto pixel : row) assert(pixel == 0);
}
