#pragma once
#include <esp_ota_ops.h>

// Both programs use the existing 16 MB partition layout. Selecting the other
// image reboots into an independent program with its own USB descriptors/tasks.
namespace DevicePrograms {
inline esp_err_t select(esp_partition_subtype_t subtype, uint32_t address)
{
    const esp_partition_t *target = esp_partition_find_first(ESP_PARTITION_TYPE_APP, subtype, nullptr);
    if (!target || target->address != address) return ESP_ERR_NOT_FOUND;
    const esp_partition_t *boot = esp_ota_get_boot_partition();
    if (boot && boot->address == target->address) return ESP_OK;
    // ESP-IDF validates the target image before changing boot selection.
    return esp_ota_set_boot_partition(target);
}

inline esp_err_t enterAudio() { return select(ESP_PARTITION_SUBTYPE_APP_OTA_1, 0x650000); }
// Call first in audio setup, before Wi-Fi/USB: RESET then always boots the monitor.
inline esp_err_t returnToMonitorOnReset() { return select(ESP_PARTITION_SUBTYPE_APP_OTA_0, 0x10000); }
}
