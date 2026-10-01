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

    screen.setFont(u8g2_font_wqy16_t_gb2312);
    const char *title = u8"5\u7ea7 OLIGHT WARRIOR 3S \u6218\u672f\u624b\u7535";
    int overflow = screen.getUTF8Width(title) - 140;
    assert(overflow > 10);
    int cycle = screen.getUTF8Width(title) + UiMarquee::GapPixels;
    uint16_t before[170][320];
    uint16_t first[18][140];
    for (int cell = 0; cell < 4; ++cell) {
        int x = cell % 2 * 160, y = cell / 2 * 72;
        screen.clearBuffer();
        screen.drawFrame(x + 1, y + 1, 158, 70);
        screen.drawUTF8(x + 10, y + 21, "facility");
        screen.drawUTF8(x + 10, y + 56, "08:00:00");
        screen.drawBox(x + 10, y + 61, 70, 4);
        for (int line = 0; line < 170; ++line) screen.readRow(line, before[line]);
        for (int scroll : {0, overflow / 2, overflow, cycle - 80, cycle - 1, cycle}) {
            screen.setDrawColor(0);
            screen.drawBox(x + 10, y + 23, 140, 18);
            screen.drawScrollingText(x + 10, y + 39, 140, title, scroll);
            unsigned lit = 0, changed = 0;
            for (int line = 0; line < 170; ++line) {
                screen.readRow(line, row);
                for (int col = 0; col < 320; ++col) {
                    if (line >= y + 23 && line < y + 41 && col >= x + 10 && col < x + 150) {
                        auto &initial = first[line - y - 23][col - x - 10];
                        if (scroll == 0) initial = row[col];
                        else changed += initial != row[col];
                        if (scroll == cycle) assert(initial == row[col]);
                        if (scroll == cycle - 1 && col > x + 10)
                            assert(first[line - y - 23][col - x - 11] == row[col]);
                        lit += row[col] != 0;
                    } else assert(row[col] == before[line][col]);
                }
            }
            assert(lit > 50);
            if (scroll != 0 && scroll != cycle) assert(changed > 50);
        }
        screen.drawPixel(319, 169); // The item clip must not leak to later drawing.
        screen.readRow(169, row);
        assert(row[319] == 0xFFFF);
    }
}
