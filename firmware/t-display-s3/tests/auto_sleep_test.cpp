#include "auto_sleep.h"
#include "ui_button.h"
#include <assert.h>

int main()
{
    AutoSleep monitor;
    monitor.begin(1000);
    assert(monitor.row() == 2); // Default one minute after boot/activity.
    assert(!monitor.due(60999, false, false));
    assert(monitor.due(61000, false, false));
    monitor.activity(62000);
    assert(!monitor.due(121999, false, false));
    assert(!monitor.due(122000, true, false)); // Holding a button is activity.
    assert(!monitor.due(182000, false, true)); // OTA keeps the device awake.
    assert(!monitor.due(241999, false, false));
    assert(monitor.due(242000, false, false));
    assert(monitor.save(1, 242000) && storedSleepSeconds == 30);
    assert(!monitor.due(271999, false, false) && monitor.due(272000, false, false));
    assert(monitor.save(1, 272000) && sleepWrites == 1);
    assert(!monitor.save(AutoSleep::Count, 0));

    AutoSleep reboot;
    reboot.begin(0);
    assert(reboot.row() == 1 && reboot.due(30000, false, false));
    assert(reboot.save(0, 0));
    assert(!reboot.due(UINT32_MAX, false, false)); // Disabled never sleeps.
    assert(reboot.save(2, UINT32_MAX - 999));
    assert(!reboot.due(58999, false, false) && reboot.due(59000, false, false));
    writesSucceed = false;
    assert(!reboot.save(5, 0) && reboot.row() == 2 && storedSleepSeconds == 60);
    writesSucceed = true;
    storedSleepSeconds = 7;
    AutoSleep invalid;
    invalid.begin(0);
    assert(invalid.row() == 2);

    // A held wake key and its debounced release cannot turn into an action.
    WakeButtonGate gate;
    UiButton button;
    for (uint32_t t = 0; t < 2000; ++t) {
        bool pressed = button.update(false, t);
        assert(!gate.allow(button.raw && button.stable, t));
        assert(!pressed);
    }
    for (uint32_t t = 2000; t <= 2070; ++t) {
        button.update(true, t);
        assert(!gate.allow(button.raw && button.stable, t));
    }
    assert(gate.allow(true, 2071));
    assert(!button.update(false, 2100));
    assert(button.update(false, 2135) && gate.allow(false, 2135));

    assert(SleepResume::save(true));
    assert(SleepResume::consume(true)); // Honor a legacy v21 audio wake marker once.
    assert(!SleepResume::consume(true));
    assert(SleepResume::save(true));
    assert(!SleepResume::consume(false)); // RESET exits audio and clears the marker.
    assert(!SleepResume::consume(true));
    writesSucceed = false;
    assert(!SleepResume::save(true));
    writesSucceed = true;
    assert(SleepResume::save(true));
    writesSucceed = false;
    assert(!SleepResume::consume(true)); // A failed consume must not cause a restart loop.
    writesSucceed = true;
    assert(SleepResume::consume(true));
}
