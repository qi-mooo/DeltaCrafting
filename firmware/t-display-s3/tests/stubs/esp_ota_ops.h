#pragma once
#include <stdint.h>
using esp_err_t = int;
constexpr int ESP_OK = 0, ESP_ERR_NOT_FOUND = -1;
enum esp_partition_type_t { ESP_PARTITION_TYPE_APP };
enum esp_partition_subtype_t { ESP_PARTITION_SUBTYPE_APP_OTA_0, ESP_PARTITION_SUBTYPE_APP_OTA_1 };
struct esp_partition_t { uint32_t address; };
const esp_partition_t *esp_partition_find_first(esp_partition_type_t, esp_partition_subtype_t, const char *);
const esp_partition_t *esp_ota_get_boot_partition();
esp_err_t esp_ota_set_boot_partition(const esp_partition_t *);
