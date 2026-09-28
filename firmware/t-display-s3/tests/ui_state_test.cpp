#include "ui_state.h"
#include "ui_button.h"
#include "ui_indicators.h"
#include <assert.h>
#include <string>
#include <vector>

int main()
{
    static_assert(UI_CONFIRM_PIN == 0 && UI_CYCLE_PIN == 14, "Button roles must match the device labels");
    UiState ui;
    const uint8_t expected[] = {3, 0, 1, 2};
    for (uint8_t i = 0; i < 4; ++i) {
        assert(ui.home == i && ui.facility() == expected[i]);
        ui.move(1);
    }
    assert(ui.home == 4); // Status bar is a selectable fifth item.
    ui.move(1);
    assert(ui.home == 0);
    ui.move(-1);
    assert(ui.home == 4);
    ui.open(UiPage::Global);
    ui.move(-1);
    assert(ui.row == 5); // Return option, after start and sync actions.
    ui.move(1);
    assert(ui.row == 0 && ui.count() == 6);
    ui.open(UiPage::Home);
    assert(ui.home == 4);
    ui.move(-1);
    ui.open(UiPage::Facility);
    ui.move(-1);
    assert(ui.row == 2);
    ui.open(UiPage::CraftMode, 2);
    ui.move(1);
    assert(ui.row == 3);
    ui.move(1);
    assert(ui.row == 0 && ui.facility() == 2);
    ui.customMode = true;
    ui.open(UiPage::Facility);
    ui.move(-1);
    assert(ui.row == 3 && ui.count() == 4);
    std::vector<std::string> items(254);
    for (unsigned i = 0; i < items.size(); ++i) items[i] = std::to_string(i);
    ui.openItems(items, std::string("200"));
    assert(ui.row == 200 && ui.count() == 255);
    assert(ui.row * 29 + ui.initialScroll() == 29); // Selected item visible immediately, even far down the list.
    ui.open(UiPage::Items, 254);
    ui.move(1);
    assert(ui.row == 0);
    ui.move(-1);
    assert(ui.row == 254); // Return row, no 8-bit overflow.
    ui.openItems(std::vector<std::string>{}, std::string());
    assert(ui.row == 0 && ui.count() == 1);

    UiHoldConfirm hold;
    assert(!hold.update(true, false, false, 0)); // Entry press cannot arm confirmation.
    assert(!hold.update(false, false, true, 900));
    assert(!hold.update(true, false, true, 1000));
    assert(!hold.update(false, false, true, 1799));
    assert(hold.update(false, false, true, 1800));
    assert(!hold.update(false, false, true, 2600)); // Never repeat while held.
    assert(!hold.update(true, false, true, 3000));
    assert(!hold.update(false, true, true, 3100)); // Short press never saves.
    assert(!hold.update(false, false, true, 4000));
    assert(!hold.update(true, false, true, 5000));
    assert(!hold.update(false, false, false, 5500)); // Changing page/cycling cancels hold.
    assert(!hold.update(false, false, true, 5900));

    UiButton button;
    assert(!button.update(true, 100));
    assert(!button.update(false, 110));
    assert(!button.update(true, 120)); // Bounce must not select.
    assert(!button.update(false, 125));
    assert(!button.update(false, 159));
    assert(button.update(false, 160));
    assert(!button.update(false, 1660)); // Holding cannot confirm again.
    assert(!button.update(true, 1700));
    assert(!button.update(true, 1735));
    assert(!button.update(false, 1800));
    assert(button.update(false, 1835));

    UiButton bootHeld;
    assert(!bootHeld.update(false, 0));
    assert(!bootHeld.update(false, 50)); // BOOT held at startup is ignored.
    assert(!bootHeld.update(true, 100));
    assert(!bootHeld.update(true, 150));
    assert(!bootHeld.update(false, 200));
    assert(bootHeld.update(false, 250));
    assert(batteryBars(0) == -1 && batteryBars(4400) == -1);
    assert(batteryBars(4200) == 4 && batteryBars(3400) == 0);
    assert(progressPixels(1800, 3600, 138) == 69);
    assert(progressPixels(4000, 3600, 138) == 0);
    assert(progressPixels(0, 3600, 138) == 138);
    assert(progressPixels(2700, 3600, 100) == 25);
    assert(progressPixels(900, 3600, 100) == 75);
    assert(progressPixels(-1, 3600, 138) == -1);
    assert(progressPixels(1800, -1, 138) == -1);
}
