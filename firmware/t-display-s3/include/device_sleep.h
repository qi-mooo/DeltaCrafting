#pragma once
#include <Arduino.h>
#include <WiFi.h>
#include <TFT_eSPI.h>
#include <esp_sleep.h>
#include <esp_idf_version.h>
#include <driver/rtc_io.h>
#include <driver/gpio.h>
#include "display_brightness.h"
#include "auto_sleep.h"

namespace DeviceSleep {
inline void releasePins()
{
    gpio_deep_sleep_hold_dis();
    gpio_hold_dis(GPIO_NUM_38);
    rtc_gpio_hold_dis(GPIO_NUM_15);
    rtc_gpio_deinit(GPIO_NUM_15);
    rtc_gpio_hold_dis(GPIO_NUM_14);
    rtc_gpio_deinit(GPIO_NUM_14);
}

inline bool prepare(bool audio)
{
    // GPIO 14 avoids waking through the BOOT strap. EXT1 works with RTC
    // peripherals off; hold its pull-up to keep the input defined during sleep.
    if (esp_sleep_disable_wakeup_source(ESP_SLEEP_WAKEUP_ALL) != ESP_OK
        || esp_sleep_enable_ext1_wakeup(1ULL << 14, ESP_EXT1_WAKEUP_ANY_LOW) != ESP_OK
        || !SleepResume::save(audio)) return false;
    return true;
}

inline void enter(TFT_eSPI &display, DisplayBrightness &brightness)
{
    WiFi.setAutoReconnect(false);
    WiFi.disconnect(true, false); // Keep provisioned credentials.
    WiFi.mode(WIFI_OFF);
    brightness.setScreenOn(false);
    display.writecommand(0x28);
    display.writecommand(0x10);
    delay(120);

    // Disable the board's display supply as in LILYGO's Sleep example.
    rtc_gpio_init(GPIO_NUM_15);
    rtc_gpio_set_direction(GPIO_NUM_15, RTC_GPIO_MODE_OUTPUT_ONLY);
    rtc_gpio_set_level(GPIO_NUM_15, 0);
    rtc_gpio_hold_en(GPIO_NUM_15);
    gpio_hold_en(GPIO_NUM_38);
    gpio_deep_sleep_hold_en();

    rtc_gpio_init(GPIO_NUM_14);
    rtc_gpio_set_direction(GPIO_NUM_14, RTC_GPIO_MODE_INPUT_ONLY);
    rtc_gpio_pulldown_dis(GPIO_NUM_14);
    rtc_gpio_pullup_en(GPIO_NUM_14);
    rtc_gpio_hold_en(GPIO_NUM_14);
    esp_sleep_pd_config(ESP_PD_DOMAIN_RTC_PERIPH, ESP_PD_OPTION_OFF);
    // Configuration/resume state lives in NVS. Disable optional RTC retention
    // where exposed by the SDK; newer S3 SDKs do not expose separate RAM domains.
#if ESP_IDF_VERSION_MAJOR < 5 || SOC_PM_SUPPORT_RTC_SLOW_MEM_PD
    esp_sleep_pd_config(ESP_PD_DOMAIN_RTC_SLOW_MEM, ESP_PD_OPTION_OFF);
#endif
#if ESP_IDF_VERSION_MAJOR < 5 || SOC_PM_SUPPORT_RTC_FAST_MEM_PD
    esp_sleep_pd_config(ESP_PD_DOMAIN_RTC_FAST_MEM, ESP_PD_OPTION_OFF);
#endif
    // No timer or network wake source: stay off until the cycle key or RESET.
    esp_deep_sleep_start();
}
}
