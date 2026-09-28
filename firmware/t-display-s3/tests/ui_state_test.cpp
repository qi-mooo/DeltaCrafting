#include "ui_state.h"
#include "ui_button.h"
#include "ui_indicators.h"
#include <assert.h>

int main()
{
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
    assert(ui.row == 3); // Return option.
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
    assert(progressPixels(0, 3600, 138) == 137);
    assert(progressPixels(1800, -1, 138) == -1);
}
