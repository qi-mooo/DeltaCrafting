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
    // Red game glyphs share page motion, while focus borders remain white.
    screen.clearBuffer();
    screen.setFont(u8g2_font_wqy16_t_gb2312);
    screen.drawRedText(12, 162, u8"游戏中");
    screen.drawFrame(1, 145, 228, 25);
    uint16_t gameRows[25][320];
    unsigned redCount = 0;
    for (unsigned y = 145; y < 170; ++y) {
        screen.readRow(y, gameRows[y - 145]);
        for (unsigned x = 0; x < 320; ++x) {
            if (gameRows[y - 145][x] == 0xF800) {
                assert(x >= 12 && x < 103 && y > 145 && y < 165);
                ++redCount;
            }
        }
        assert(gameRows[y - 145][1] == 0xFFFF);
    }
    assert(redCount > 100);
    for (bool forward : {true, false}) {
        screen.offset = 0;
        screen.clearBuffer();
        screen.drawRedText(12, 162, u8"游戏中");
        screen.drawFrame(1, 145, 228, 25);
        screen.startTransition(forward);
        screen.clearBuffer();
        for (int progress : {320, 160, 1, 0}) {
            screen.offset = forward ? progress : -progress;
            for (unsigned y = 145; y < 170; ++y) {
                screen.readRow(y, row);
                for (int x = 0; x < 320; ++x) {
                    int oldX = forward ? x + 320 - progress : x - 320 + progress;
                    assert(row[x] == (oldX >= 0 && oldX < 320 ? gameRows[y - 145][oldX] : 0));
                }
            }
        }
    }
    screen.offset = 0;
    screen.clearBuffer();
    screen.drawUTF8(82, 162, u8"进入设置");
    for (unsigned y = 145; y < 170; ++y) {
        screen.readRow(y, row);
        for (auto pixel : row) assert(pixel == 0 || pixel == 0xFFFF);
    }
    screen.clearBuffer();
    screen.drawRedText(12, 162, u8"游戏中");
    screen.drawBox(12, 145, 48, 18);
    screen.readRow(155, row);
    for (int x = 12; x < 60; ++x) assert(row[x] == 0xFFFF);

    // Every progress pixel and its black gutter survive a moving home focus.
    const int targets[][4] = {
        {1, 1, 158, 70}, {161, 1, 158, 70}, {1, 73, 158, 70},
        {161, 73, 158, 70}, {1, 145, 228, 25}, {230, 145, 89, 25}
    };
    const int fills[] = {-1, 0, 69, 138};
    for (const auto &from : targets) {
        for (const auto &to : targets) {
            for (int step = 0; step <= 20; ++step) {
                int focus[4];
                for (int i = 0; i < 4; ++i) focus[i] = from[i] + (to[i] - from[i]) * step / 20;
                screen.clearBuffer();
                screen.drawHomeFocus(focus[0], focus[1], focus[2], focus[3]);
                for (int cell = 0; cell < 4; ++cell)
                    screen.drawFacilityProgress(cell % 2 * 160 + 10, cell / 2 * 72 + 61, fills[cell]);
                for (int cell = 0; cell < 4; ++cell) {
                    int x = cell % 2 * 160 + 10, y = cell / 2 * 72 + 61;
                    for (int dy = -1; dy <= 4; ++dy) {
                        screen.readRow(y + dy, row);
                        for (int dx = -1; dx <= 140; ++dx) {
                            bool frame = dx >= 0 && dx < 140 && dy >= 0 && dy < 4
                                && (dx == 0 || dx == 139 || dy == 0 || dy == 3);
                            bool fill = dx >= 1 && dx <= fills[cell] && dy >= 1 && dy <= 2;
                            assert(row[x + dx] == (frame || fill ? 0xFFFF : 0));
                        }
                    }
                }
            }
        }
    }
    // At rest, the bottom corners leave two clear rows below the progress bar.
    for (int cell = 0; cell < 4; ++cell) {
        int x = cell % 2 * 160, y = cell / 2 * 72;
        screen.clearBuffer();
        screen.drawHomeFocus(x + 1, y + 1, 158, 70);
        for (int dy = 60; dy <= 66; ++dy) {
            screen.readRow(y + dy, row);
            for (int dx = 9; dx <= 150; ++dx) assert(row[x + dx] == 0);
        }
    }
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
