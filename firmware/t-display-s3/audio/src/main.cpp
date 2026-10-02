#include <Arduino.h>
#include <HTTPClient.h>
#include <TFT_eSPI.h>
#include <WiFi.h>

#include "AudioBridge.h"
#include "config.h"
#include "program_switch.h"
#include "display_brightness.h"
#include "device_sleep.h"
#include <DeltaOta.h>

namespace {

constexpr uint16_t COLOR_BACKGROUND = 0x0841;
constexpr uint16_t COLOR_HEADER = 0x10A2;
constexpr uint16_t COLOR_PANEL = 0x18E3;
constexpr uint16_t COLOR_TEXT = TFT_WHITE;
constexpr uint16_t COLOR_DIM = 0x8410;
constexpr uint16_t COLOR_GREEN = 0x4FEA;
constexpr uint16_t COLOR_RED = 0xF2AA;
constexpr uint16_t COLOR_AMBER = 0xFD20;
constexpr uint16_t COLOR_CYAN = 0x4DFF;

constexpr uint32_t POLL_INTERVAL_MS = 1000;
constexpr uint32_t ERROR_POLL_INTERVAL_MS = 2500;
constexpr uint32_t WIFI_RETRY_INTERVAL_MS = 5000;
constexpr uint32_t BUTTON_DEBOUNCE_MS = 35;
constexpr uint32_t BUTTON_LONG_PRESS_MS = 600;
constexpr uint32_t BUTTON_REPEAT_MS = 250;
constexpr uint32_t BATTERY_UPDATE_INTERVAL_MS = 5000;
constexpr uint32_t DISPLAY_UPDATE_INTERVAL_MS = 100;

TFT_eSPI display;
DisplayBrightness brightness(PIN_LCD_BL);
AutoSleep autoSleep;
WakeButtonGate wakeButtons;
TFT_eSprite canvas(&display);
AudioBridge audioBridge;

enum class ScreenState {
    Starting,
    ConnectingWifi,
    ConnectingServer,
    Ready,
    ServerError,
    AudioError,
};

enum class ButtonEvent {
    None,
    ShortPress,
    LongRepeat,
};

enum class RequestType {
    GetState,
    ToggleMute,
    VolumeUp,
    VolumeDown,
};

struct ButtonState {
    explicit ButtonState(uint8_t buttonPin)
        : pin(buttonPin)
    {
    }

    uint8_t pin;
    bool rawLevel = HIGH;
    bool stableLevel = HIGH;
    uint32_t changedAt = 0;
    uint32_t pressedAt = 0;
    uint32_t nextRepeatAt = 0;
    bool longPressActive = false;
};

ButtonState gpio0Button{PIN_BUTTON_LEFT};
ButtonState gpio14Button{PIN_BUTTON_RIGHT};

ScreenState screenState = ScreenState::Starting;
wl_status_t previousWifiStatus = WL_IDLE_STATUS;
bool muteKnown = false;
bool muted = false;
bool requestInProgress = false;
bool screenOn = true;
bool audioBridgeStarted = false;
bool firmwareRecovery = false;
int volumePercent = 0;
int lastHttpStatus = 0;
int batteryMillivolts = 0;
int batteryPercent = 0;
bool batteryPresent = false;
uint32_t nextPollAt = 0;
uint32_t nextWifiRetryAt = 0;
uint32_t nextBatteryReadAt = 0;
uint32_t nextDisplayUpdateAt = 0;

constexpr size_t LEVEL_HISTORY_POINT_COUNT = 91;
uint8_t levelHistory[LEVEL_HISTORY_POINT_COUNT] = {};
size_t levelHistoryCount = 0;

typedef struct {
    uint8_t command;
    uint8_t data[14];
    uint8_t length;
} LcdCommand;

const LcdCommand lcdInitCommands[] = {
    {0x11, {0}, 0x80},
    {0x3A, {0x05}, 1},
    {0xB2, {0x0B, 0x0B, 0x00, 0x33, 0x33}, 5},
    {0xB7, {0x75}, 1},
    {0xBB, {0x28}, 1},
    {0xC0, {0x2C}, 1},
    {0xC2, {0x01}, 1},
    {0xC3, {0x1F}, 1},
    {0xC6, {0x13}, 1},
    {0xD0, {0xA7}, 1},
    {0xD0, {0xA4, 0xA1}, 2},
    {0xD6, {0xA1}, 1},
    {0xE0, {0xF0, 0x05, 0x0A, 0x06, 0x06, 0x03, 0x2B, 0x32, 0x43, 0x36, 0x11, 0x10, 0x2B, 0x32}, 14},
    {0xE1, {0xF0, 0x08, 0x0C, 0x0B, 0x09, 0x24, 0x2B, 0x22, 0x43, 0x38, 0x15, 0x16, 0x2F, 0x37}, 14},
};

bool timeReached(uint32_t deadline)
{
    return static_cast<int32_t>(millis() - deadline) >= 0;
}

String makeDeviceId()
{
    const uint64_t chipId = ESP.getEfuseMac();
    char result[18];
    snprintf(
        result,
        sizeof(result),
        "%02X%02X%02X%02X%02X%02X",
        static_cast<uint8_t>(chipId >> 40),
        static_cast<uint8_t>(chipId >> 32),
        static_cast<uint8_t>(chipId >> 24),
        static_cast<uint8_t>(chipId >> 16),
        static_cast<uint8_t>(chipId >> 8),
        static_cast<uint8_t>(chipId));
    return String(result);
}

int batteryPercentFromMillivolts(int millivolts)
{
    struct BatteryPoint {
        uint16_t millivolts;
        uint8_t percent;
    };

    static const BatteryPoint curve[] = {
        {3300, 0},
        {3500, 5},
        {3600, 10},
        {3700, 20},
        {3800, 40},
        {3900, 60},
        {4000, 75},
        {4100, 90},
        {4200, 100},
    };

    if (millivolts <= curve[0].millivolts) {
        return curve[0].percent;
    }

    for (size_t index = 1; index < sizeof(curve) / sizeof(curve[0]); ++index) {
        if (millivolts <= curve[index].millivolts) {
            const BatteryPoint &low = curve[index - 1];
            const BatteryPoint &high = curve[index];
            return low.percent
                + (millivolts - low.millivolts) * (high.percent - low.percent)
                    / (high.millivolts - low.millivolts);
        }
    }
    return 100;
}

void readBatterySensor()
{
    constexpr int sampleCount = 12;
    uint32_t totalMillivolts = 0;
    for (int sample = 0; sample < sampleCount; ++sample) {
        totalMillivolts += analogReadMilliVolts(PIN_BATTERY_VOLTAGE);
        delay(2);
    }

    batteryMillivolts = static_cast<int>(totalMillivolts / sampleCount) * 2;
    batteryPresent = batteryMillivolts >= 2500 && batteryMillivolts <= 4300;
    batteryPercent = batteryPresent ? batteryPercentFromMillivolts(batteryMillivolts) : 0;
    nextBatteryReadAt = millis() + BATTERY_UPDATE_INTERVAL_MS;
}

void drawStatusDot(int x, const char *label, bool active, uint16_t activeColor)
{
    canvas.setTextDatum(MC_DATUM);
    canvas.setTextColor(COLOR_DIM, COLOR_HEADER);
    canvas.drawString(label, x, 8, 1);
    canvas.fillCircle(x, 20, 4, active ? activeColor : COLOR_RED);
}

void drawBatteryIndicator()
{
    constexpr int x = 205;
    constexpr int y = 9;
    constexpr int width = 20;
    constexpr int height = 11;

    uint16_t color = COLOR_DIM;
    if (batteryPresent) {
        color = batteryPercent <= 15 ? COLOR_RED : (batteryPercent <= 40 ? COLOR_AMBER : COLOR_GREEN);
    }

    canvas.drawRect(x, y, width, height, color);
    canvas.fillRect(x + width, y + 3, 2, height - 6, color);
    if (batteryPresent) {
        const int fillWidth = map(constrain(batteryPercent, 0, 100), 0, 100, 0, width - 4);
        canvas.fillRect(x + 2, y + 2, fillWidth, height - 4, color);
    }

    canvas.setTextDatum(ML_DATUM);
    canvas.setTextColor(color, COLOR_HEADER);
    canvas.drawString(batteryPresent ? (String(batteryPercent) + "%") : "USB", 230, 15, 1);
}

void drawHeader(const AudioBridgeStats &stats)
{
    canvas.fillRect(0, 0, 320, 30, COLOR_HEADER);
    canvas.setTextDatum(ML_DATUM);
    canvas.setTextColor(COLOR_TEXT, COLOR_HEADER);
    canvas.drawString("WINDOWS AUDIO", 10, 15, 2);
    drawBatteryIndicator();
    drawStatusDot(273, "W", WiFi.status() == WL_CONNECTED, COLOR_GREEN);
    drawStatusDot(294, "P", stats.serverDiscovered, COLOR_CYAN);
    drawStatusDot(315, "U", stats.usbMounted, COLOR_GREEN);
}

void appendLevelHistory(uint8_t level)
{
    if (levelHistoryCount < LEVEL_HISTORY_POINT_COUNT) {
        levelHistory[levelHistoryCount++] = level;
        return;
    }

    for (size_t index = 1; index < LEVEL_HISTORY_POINT_COUNT; ++index) {
        levelHistory[index - 1] = levelHistory[index];
    }
    levelHistory[LEVEL_HISTORY_POINT_COUNT - 1] = level;
}

void drawPeakHistory()
{
    constexpr int x = 10;
    constexpr int right = 190;
    constexpr int top = 130;
    constexpr int baseline = 164;
    constexpr int maxHeight = baseline - top;
    constexpr int pointSpacing = 2;

    canvas.fillRect(x, top, right - x + 1, baseline - top + 1, TFT_BLACK);
    if (levelHistoryCount == 0) {
        return;
    }

    int previousX = right - (levelHistoryCount - 1) * pointSpacing;
    int previousY = baseline - map(
        constrain(levelHistory[0], 0, 100), 0, 100, 0, maxHeight);
    for (size_t index = 1; index < levelHistoryCount; ++index) {
        const int pointX = previousX + pointSpacing;
        const int height = map(constrain(levelHistory[index], 0, 100), 0, 100, 0, maxHeight);
        const int pointY = baseline - height;
        canvas.drawLine(previousX, previousY, pointX, pointY, TFT_WHITE);
        previousX = pointX;
        previousY = pointY;
    }
    canvas.fillCircle(previousX, previousY, 1, TFT_WHITE);
}

void drawReadyState(const AudioBridgeStats &stats)
{
    constexpr int volumeY = 30;
    constexpr int volumeHeight = 96;
    const int blockWidth = map(constrain(volumePercent, 0, 100), 0, 100, 0, 320);
    const uint16_t blockColor = muted ? COLOR_RED : COLOR_GREEN;

    if (blockWidth > 0) {
        canvas.fillRect(0, volumeY, blockWidth, volumeHeight, blockColor);
    }

    canvas.setTextDatum(ML_DATUM);
    canvas.setTextColor(COLOR_TEXT);
    canvas.drawString(muted ? "MUTED" : "SOUND ON", 14, 66, 4);
    canvas.setTextDatum(MR_DATUM);
    canvas.drawString((String(volumePercent) + "%").c_str(), 305, 91, 4);

    appendLevelHistory(stats.peakLevel);
    canvas.fillRect(0, 126, 320, 44, TFT_BLACK);
    drawPeakHistory();

    canvas.setTextDatum(TR_DATUM);
    canvas.setTextColor(TFT_WHITE, TFT_BLACK);
    canvas.drawString((String("BUF ") + stats.bufferMilliseconds + "ms").c_str(), 310, 128, 2);
    const uint32_t totalLoss = stats.missingPackets + stats.bufferUnderruns + stats.usbDroppedBlocks;
    canvas.drawString((String("LOSS ") + totalLoss).c_str(), 310, 149, 2);
}

void drawMessage(const char *title, const char *detail, uint16_t color)
{
    canvas.setTextDatum(MC_DATUM);
    canvas.setTextColor(color, COLOR_BACKGROUND);
    canvas.drawString(title, 160, 75, 4);
    canvas.setTextColor(COLOR_DIM, COLOR_BACKGROUND);
    canvas.drawString(detail, 160, 111, 2);
}

void drawScreen()
{
    if (!screenOn) {
        return;
    }

    const AudioBridgeStats stats = audioBridge.getStats();
    canvas.fillSprite(COLOR_BACKGROUND);
    drawHeader(stats);

    switch (screenState) {
    case ScreenState::Starting:
        drawMessage("STARTING", "USB AUDIO BRIDGE", COLOR_CYAN);
        break;
    case ScreenState::ConnectingWifi:
        drawMessage("CONNECTING", WIFI_SSID, COLOR_AMBER);
        break;
    case ScreenState::ConnectingServer:
        drawMessage("DISCOVERING", "WINDOWS BRIDGE", COLOR_CYAN);
        break;
    case ScreenState::AudioError:
        drawMessage("USB ERROR", "AUDIO BRIDGE FAILED", COLOR_RED);
        break;
    case ScreenState::ServerError:
    case ScreenState::Ready:
        drawReadyState(stats);
        if (screenState == ScreenState::ServerError) {
            canvas.setTextDatum(TR_DATUM);
            canvas.setTextColor(COLOR_RED, COLOR_PANEL);
            canvas.drawString((String("HTTP ") + lastHttpStatus).c_str(), 310, 111, 1);
        }
        break;
    }

    canvas.pushSprite(0, 0);
}

bool parseBooleanField(const String &json, const char *field, bool &value)
{
    String key = "\"";
    key += field;
    key += "\":";
    int position = json.indexOf(key);
    if (position < 0) {
        return false;
    }

    position += key.length();
    while (position < json.length() && isspace(static_cast<unsigned char>(json[position]))) {
        ++position;
    }
    if (json.substring(position, position + 4) == "true") {
        value = true;
        return true;
    }
    if (json.substring(position, position + 5) == "false") {
        value = false;
        return true;
    }
    return false;
}

bool parseIntegerField(const String &json, const char *field, int &value)
{
    String key = "\"";
    key += field;
    key += "\":";
    int position = json.indexOf(key);
    if (position < 0) {
        return false;
    }

    position += key.length();
    while (position < json.length() && isspace(static_cast<unsigned char>(json[position]))) {
        ++position;
    }

    int parsedValue = 0;
    bool hasDigit = false;
    while (position < json.length() && isdigit(static_cast<unsigned char>(json[position]))) {
        hasDigit = true;
        parsedValue = parsedValue * 10 + json[position] - '0';
        ++position;
    }
    if (!hasDigit) {
        return false;
    }
    value = parsedValue;
    return true;
}

bool applyServerResponse(const String &payload)
{
    bool parsedMuted = false;
    int parsedVolume = 0;
    if (!parseBooleanField(payload, "muted", parsedMuted)
        || !parseIntegerField(payload, "volumePercent", parsedVolume)) {
        return false;
    }

    muted = parsedMuted;
    volumePercent = constrain(parsedVolume, 0, 100);
    muteKnown = true;
    screenState = ScreenState::Ready;
    lastHttpStatus = HTTP_CODE_OK;
    return true;
}

bool performRequest(RequestType requestType)
{
    const String serverBaseUrl = audioBridge.serverBaseUrl();
    if (WiFi.status() != WL_CONNECTED || serverBaseUrl.isEmpty()) {
        return false;
    }

    requestInProgress = true;
    WiFiClient client;
    HTTPClient http;
    String url = serverBaseUrl;

    switch (requestType) {
    case RequestType::GetState:
        url += "/api/mute";
        break;
    case RequestType::ToggleMute:
        url += "/api/mute/toggle";
        break;
    case RequestType::VolumeUp:
        url += "/api/volume/up";
        break;
    case RequestType::VolumeDown:
        url += "/api/volume/down";
        break;
    }

    http.setConnectTimeout(1000);
    http.setTimeout(1800);
    http.setReuse(false);
    http.useHTTP10(true);

    bool success = false;
    if (http.begin(client, url)) {
        int statusCode;
        if (requestType == RequestType::GetState) {
            statusCode = http.GET();
        } else {
            http.addHeader("Content-Type", "application/json");
            statusCode = http.POST("{}");
        }

        lastHttpStatus = statusCode;
        if (statusCode == HTTP_CODE_OK) {
            success = applyServerResponse(http.getString());
        }
        http.end();
    } else {
        lastHttpStatus = -1;
    }

    requestInProgress = false;
    if (!success) {
        screenState = ScreenState::ServerError;
    }
    nextPollAt = millis() + (success ? POLL_INTERVAL_MS : ERROR_POLL_INTERVAL_MS);
    return success;
}

ButtonEvent updateButton(ButtonState &button)
{
    const bool currentLevel = digitalRead(button.pin);
    if (currentLevel != button.rawLevel) {
        button.rawLevel = currentLevel;
        button.changedAt = millis();
    }

    if (button.stableLevel != button.rawLevel && millis() - button.changedAt >= BUTTON_DEBOUNCE_MS) {
        button.stableLevel = button.rawLevel;
        if (button.stableLevel == LOW) {
            button.pressedAt = millis();
            button.nextRepeatAt = button.pressedAt + BUTTON_LONG_PRESS_MS;
            button.longPressActive = false;
            return ButtonEvent::None;
        }

        const bool wasLongPress = button.longPressActive;
        button.longPressActive = false;
        return wasLongPress ? ButtonEvent::None : ButtonEvent::ShortPress;
    }

    if (button.stableLevel == LOW) {
        if (!button.longPressActive && timeReached(button.pressedAt + BUTTON_LONG_PRESS_MS)) {
            button.longPressActive = true;
            button.nextRepeatAt = millis() + BUTTON_REPEAT_MS;
            return ButtonEvent::LongRepeat;
        }
        if (button.longPressActive && timeReached(button.nextRepeatAt)) {
            button.nextRepeatAt = millis() + BUTTON_REPEAT_MS;
            return ButtonEvent::LongRepeat;
        }
    }
    return ButtonEvent::None;
}

void setScreenPower(bool enabled)
{
    if (enabled == screenOn) {
        return;
    }

    if (enabled) {
        display.writecommand(0x11);
        delay(120);
        brightness.setScreenOn(true);
        screenOn = true;
        drawScreen();
    } else {
        brightness.setScreenOn(false);
        display.writecommand(0x10);
        screenOn = false;
    }
}

void beginWifi()
{
    WiFi.persistent(false);
    WiFi.setHostname("t-display-audio");
    WiFi.mode(WIFI_STA);
    WiFi.setSleep(false);
    WiFi.setAutoReconnect(true);
    WiFi.begin(WIFI_SSID, WIFI_PASSWORD);

    screenState = ScreenState::ConnectingWifi;
    previousWifiStatus = WiFi.status();
    nextWifiRetryAt = millis() + WIFI_RETRY_INTERVAL_MS;
}

void updateWifiState()
{
    const wl_status_t currentStatus = WiFi.status();
    if (currentStatus != previousWifiStatus) {
        previousWifiStatus = currentStatus;
        if (currentStatus == WL_CONNECTED) {
            screenState = ScreenState::ConnectingServer;
            nextPollAt = 0;
        } else {
            screenState = ScreenState::ConnectingWifi;
            muteKnown = false;
        }
    }

    if (currentStatus != WL_CONNECTED && timeReached(nextWifiRetryAt)) {
        WiFi.disconnect();
        WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
        nextWifiRetryAt = millis() + WIFI_RETRY_INTERVAL_MS;
    }

    if (currentStatus == WL_CONNECTED && audioBridge.hasServer()
        && screenState != ScreenState::ServerError && screenState != ScreenState::AudioError) {
        screenState = ScreenState::Ready;
    } else if (currentStatus == WL_CONNECTED && !audioBridge.hasServer()
        && screenState != ScreenState::AudioError) {
        screenState = ScreenState::ConnectingServer;
        muteKnown = false;
    }
}

void initializeDisplay()
{
    pinMode(PIN_POWER_ON, OUTPUT);
    digitalWrite(PIN_POWER_ON, HIGH);

    display.begin();
    for (const auto &entry : lcdInitCommands) {
        display.writecommand(entry.command);
        for (uint8_t index = 0; index < (entry.length & 0x7F); ++index) {
            display.writedata(entry.data[index]);
        }
        if (entry.length & 0x80) {
            delay(120);
        }
    }

    display.setRotation(1);
    display.invertDisplay(true);
    brightness.begin();

    canvas.setColorDepth(16);
    canvas.createSprite(320, 170);
    canvas.setSwapBytes(true);
    if (!firmwareRecovery) drawScreen();
}

void drawFirmwareProgress(int percent, const String &detail, bool retry = false)
{
    static String lastDetail;
    static int lastPercent = -1;
    static bool lastRetry = false;
    static uint32_t lastDrawAt = 0;
    percent = constrain(percent, 0, 100);
    const uint32_t now = millis();
    if (lastPercent >= 0 && detail == lastDetail && retry == lastRetry
        && (percent == lastPercent || (percent < 100 && now - lastDrawAt < 100))) return;

    // Reuse the audio framebuffer: clearing the LCD for every percentage exposes
    // a black frame between drawing the title, progress bar and footer.
    canvas.fillSprite(TFT_BLACK);
    canvas.setTextDatum(TL_DATUM);
    canvas.setTextColor(TFT_WHITE, TFT_BLACK);
    canvas.drawString("Firmware update (2/2)", 10, 16, 2);
    canvas.drawString(detail.isEmpty() ? "Updating monitor" : detail, 10, 50, 2);
    canvas.drawRect(10, 82, 300, 16, TFT_WHITE);
    if (percent > 0) canvas.fillRect(12, 84, 296 * percent / 100, 12, TFT_WHITE);
    canvas.drawString("Keep power on", 10, 118, 2);
    if (retry) canvas.drawString("Retry in 20 seconds", 10, 145, 2);
    canvas.pushSprite(0, 0);
    lastDetail = detail;
    lastPercent = percent;
    lastRetry = retry;
    lastDrawAt = now;
}

void updateProgress(const char *, int percent, const String &detail)
{
    drawFirmwareProgress(percent, detail);
}

void recoverFirmware(void *)
{
    WiFi.mode(WIFI_STA); WiFi.persistent(false); WiFi.setAutoReconnect(true);
    for (;;) {
        DeltaOta::Config config;
        String error;
        auto job = DeltaOta::loadJob();
        if (DeltaOta::loadConfig(config, error)) {
            if (WiFi.status() != WL_CONNECTED) {
                updateProgress("", 0, "Connecting Wi-Fi");
                WiFi.begin(config.ssid.c_str(), config.password.c_str());
                uint32_t start = millis();
                while (WiFi.status() != WL_CONNECTED && millis() - start < 20000) delay(100);
            }
            if (WiFi.status() != WL_CONNECTED) error = "Wi-Fi unavailable";
            else if (DeltaOta::installMonitor(config, job, updateProgress, error)) {
                updateProgress("", 100, "Complete; restarting");
                delay(400); ESP.restart();
            }
        }
        drawFirmwareProgress(0, error, true);
        delay(20000);
    }
}

void handleAutoSleep()
{
    bool held = !digitalRead(PIN_BUTTON_LEFT) || !digitalRead(PIN_BUTTON_RIGHT);
    if (!autoSleep.due(millis(), held, firmwareRecovery || requestInProgress)) return;
    bool paused = audioBridge.pauseForSleep();
    if (paused && digitalRead(PIN_BUTTON_LEFT) && digitalRead(PIN_BUTTON_RIGHT)
        && DeviceSleep::prepare(true)) DeviceSleep::enter(display, brightness);
    audioBridge.cancelSleep();
    autoSleep.activity(millis());
}

} // namespace

void setup()
{
    DeviceSleep::releasePins();
    SleepResume::consume(false); // Also clear the marker if the bootloader resumed audio directly.
    // Return selection is committed before audio initialization. No button or
    // menu exits this program; RESET (or a power cycle) starts the monitor.
    firmwareRecovery = DeltaOta::loadJob().stage != DeltaOta::Stage::None;
    esp_err_t returnStatus = firmwareRecovery ? DevicePrograms::enterAudio() : DevicePrograms::returnToMonitorOnReset();
    Serial.begin(115200);

    analogReadResolution(12);
    analogSetPinAttenuation(PIN_BATTERY_VOLTAGE, ADC_11db);
    readBatterySensor();

    pinMode(PIN_BUTTON_LEFT, INPUT_PULLUP);
    pinMode(PIN_BUTTON_RIGHT, INPUT_PULLUP);
    gpio0Button.rawLevel = gpio0Button.stableLevel = digitalRead(PIN_BUTTON_LEFT);
    gpio14Button.rawLevel = gpio14Button.stableLevel = digitalRead(PIN_BUTTON_RIGHT);

    initializeDisplay();
    autoSleep.begin(millis());
    if (firmwareRecovery) {
        updateProgress("", 0, "Preparing recovery");
        if (returnStatus != ESP_OK || xTaskCreate(recoverFirmware, "delta-ota", 12288, nullptr, 1, nullptr) != pdPASS)
            updateProgress("", 0, "Recovery init failed");
        return;
    }
    if (returnStatus != ESP_OK || !AudioMode::loadWifi()) {
        display.fillScreen(TFT_BLACK);
        display.setTextColor(TFT_WHITE, TFT_BLACK);
        display.drawString(returnStatus != ESP_OK ? "Monitor firmware unavailable" : "Wi-Fi not configured", 10, 55, 2);
        display.drawString("Press RESET to return", 10, 85, 2);
        return;
    }
    audioBridgeStarted = audioBridge.begin(makeDeviceId(), AUDIO_BRIDGE_NAME);
    if (!audioBridgeStarted) {
        screenState = ScreenState::AudioError;
        drawScreen();
        return;
    }
    beginWifi();
}

void loop()
{
    handleAutoSleep();
    if (!audioBridgeStarted) {
        delay(1000);
        return;
    }

    updateWifiState();

    if (timeReached(nextBatteryReadAt)) {
        readBatterySensor();
    }

    const ButtonEvent gpio0Event = updateButton(gpio0Button);
    const ButtonEvent gpio14Event = updateButton(gpio14Button);
    bool released = gpio0Button.rawLevel && gpio0Button.stableLevel && gpio14Button.rawLevel && gpio14Button.stableLevel;
    if (!released || gpio0Event != ButtonEvent::None || gpio14Event != ButtonEvent::None) autoSleep.activity(millis());
    bool allowButtons = wakeButtons.allow(released, millis());

    if (allowButtons && gpio14Event == ButtonEvent::ShortPress) {
        setScreenPower(!screenOn);
    } else if (allowButtons && gpio14Event == ButtonEvent::LongRepeat && !requestInProgress && audioBridge.hasServer()) {
        performRequest(RequestType::VolumeUp);
    }

    if (allowButtons && gpio0Event == ButtonEvent::ShortPress && !requestInProgress && audioBridge.hasServer()) {
        performRequest(RequestType::ToggleMute);
    } else if (allowButtons && gpio0Event == ButtonEvent::LongRepeat && !requestInProgress && audioBridge.hasServer()) {
        performRequest(RequestType::VolumeDown);
    }

    if (!requestInProgress && audioBridge.hasServer() && timeReached(nextPollAt)) {
        performRequest(RequestType::GetState);
    }

    if (screenOn && timeReached(nextDisplayUpdateAt)) {
        drawScreen();
        nextDisplayUpdateAt = millis() + DISPLAY_UPDATE_INTERVAL_MS;
    }

    delay(5);
}
