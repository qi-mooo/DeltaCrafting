#include "program_switch.h"
#include <assert.h>

esp_partition_t monitor{0x10000}, audio{0x650000};
const esp_partition_t *boot = &monitor;
bool missing = false;
esp_err_t imageStatus = ESP_OK;
int writes = 0;
const esp_partition_t *esp_partition_find_first(esp_partition_type_t, esp_partition_subtype_t subtype, const char *)
{ return missing ? nullptr : subtype == ESP_PARTITION_SUBTYPE_APP_OTA_0 ? &monitor : &audio; }
const esp_partition_t *esp_ota_get_boot_partition() { return boot; }
esp_err_t esp_ota_set_boot_partition(const esp_partition_t *partition)
{
    if (imageStatus != ESP_OK) return imageStatus;
    ++writes;
    boot = partition;
    return ESP_OK;
}

int main()
{
    // Each tool entry launches the other program once. Its startup changes only
    // the NEXT boot, so RESET returns to monitor without a persisted audio mode.
    for (int cycle = 0; cycle < 3; ++cycle) {
        assert(DevicePrograms::enterAudio() == ESP_OK && boot == &audio);
        assert(DevicePrograms::returnToMonitorOnReset() == ESP_OK && boot == &monitor);
        int before = writes;
        assert(DevicePrograms::returnToMonitorOnReset() == ESP_OK && writes == before);
    }
    missing = true;
    assert(DevicePrograms::enterAudio() == ESP_ERR_NOT_FOUND && boot == &monitor);
    missing = false;
    audio.address = 0x10000; // Unexpected layout must not launch another image.
    assert(DevicePrograms::enterAudio() == ESP_ERR_NOT_FOUND && boot == &monitor);
    audio.address = 0x650000;
    imageStatus = -2; // Blank/corrupted audio image leaves the monitor selected.
    assert(DevicePrograms::enterAudio() == -2 && boot == &monitor);
    boot = &audio;
    assert(DevicePrograms::returnToMonitorOnReset() == -2); // Fatal startup result is surfaced.
}
