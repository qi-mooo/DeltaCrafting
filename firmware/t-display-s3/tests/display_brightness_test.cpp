#include "display_brightness.h"
#include <assert.h>

int main()
{
    DisplayBrightness monitor(38);
    monitor.begin();
    assert(monitor.savedPercent() == 100 && backlightPin == 38 && backlightDuty == 255);
    monitor.preview(0);
    assert(backlightDuty == 26 && brightnessWrites == 0); // Preview stays readable and does not wear flash.
    monitor.cancel();
    assert(backlightDuty == 255 && storedBrightness == -1);
    monitor.preview(4);
    assert(backlightDuty == 128 && monitor.save() && storedBrightness == 50);
    assert(monitor.save() && brightnessWrites == 1); // Confirming the saved choice never rewrites it.

    DisplayBrightness audio(38); // Simulate booting the other partition.
    audio.begin();
    assert(audio.savedPercent() == 50 && audio.savedRow() == 4 && backlightDuty == 128);
    audio.setScreenOn(false);
    assert(backlightDuty == 0);
    audio.setScreenOn(true);
    assert(backlightDuty == 128); // Audio's screen toggle restores the saved level.

    monitor.preview(0);
    monitor.preview(DisplayBrightness::Levels); // Return cancels a preview.
    assert(backlightDuty == 128 && storedBrightness == 50);
    monitor.preview(2);
    writesSucceed = false;
    assert(!monitor.save() && monitor.savedPercent() == 50 && storedBrightness == 50);
    monitor.cancel();
    assert(backlightDuty == 128);
    writesSucceed = true;
    monitor.preview(2);
    storageAvailable = false;
    assert(!monitor.save() && storedBrightness == 50);
    monitor.cancel();
    storageAvailable = true;
    DisplayBrightness reboot(38);
    reboot.begin();
    assert(reboot.savedPercent() == 50 && backlightDuty == 128);

    // Missing or invalid persisted values must never leave the screen black.
    const int invalid[] = {-1, 0, 9, 11, 101, 255};
    for (int value : invalid) {
        storedBrightness = value;
        DisplayBrightness fresh(38);
        fresh.begin();
        assert(fresh.savedPercent() == 100 && backlightDuty == 255);
    }
}
