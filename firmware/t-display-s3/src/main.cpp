#include <Arduino.h>
#include <Arduino_GFX_Library.h>
#include <ArduinoJson.h>
#include <HTTPClient.h>
#include <TFT_eSPI.h>
#include <WiFi.h>
#include "config.h"
#include "lcd_init.h"
#include "device_config.h"

#if TFT_WIDTH != 170 || TFT_HEIGHT != 320 || TFT_WR != 8 || TFT_RD != 9 || TFT_BL != 38
#error "Select TFT_eSPI Setup206_LilyGo_T_Display_S3.h for this firmware"
#endif

namespace {
constexpr uint16_t COLOR_BG = 0x1082, COLOR_FG = 0xFFFF, COLOR_DIM = 0x9CD3;
constexpr uint16_t COLOR_GREEN = 0x4FEA, COLOR_RED = 0xF2AA, COLOR_CYAN = 0x4DFF, COLOR_AMBER = 0xFD20;
constexpr uint32_t STALE_MS = DELTA_POLL_MS * 3 + 5000;
const char *const KEYS[] = {"workbench", "pharmacy-lab", "armor-station", "tech-center"};
const char *const NAMES[] = {"工作台", "制药台", "防具台", "技术中心"};

struct Facility {
    String item, plannedItem, phase, reason;
    bool enabled = false;
    int32_t remaining = -1;
};

struct Snapshot {
    Facility facilities[4];
    String mode, detail, lastRun, error = "Connecting Wi-Fi", notice;
    bool valid = false, running = false, autoLoop = false, control = false;
    bool lastRunFailed = false;
    int32_t nextRun = -1;
    uint32_t fetchedAt = 0, noticeAt = 0;
};

enum class Command : uint8_t { Refresh, Start, Stop, Pause, Resume };
struct Button {
    explicit Button(uint8_t value) : pin(value) {}
    uint8_t pin;
    bool raw = HIGH, stable = HIGH, longSent = false;
    uint32_t changed = 0, pressed = 0;
};

TFT_eSPI display;
Arduino_Canvas canvas(320, 170, nullptr);
SemaphoreHandle_t stateMutex;
QueueHandle_t commands;
Snapshot shared;
Button left{0}, right{14};
uint8_t page = 0;

bool fresh(const Snapshot &s)
{
    return s.valid && s.error.isEmpty() && WiFi.status() == WL_CONNECTED
        && uint32_t(millis() - s.fetchedAt) < STALE_MS;
}

Snapshot readSnapshot()
{
    xSemaphoreTake(stateMutex, portMAX_DELAY);
    Snapshot copy = shared;
    xSemaphoreGive(stateMutex);
    return copy;
}

void setError(const String &error)
{
    xSemaphoreTake(stateMutex, portMAX_DELAY);
    shared.error = error;
    xSemaphoreGive(stateMutex);
}

void setNotice(const String &notice)
{
    xSemaphoreTake(stateMutex, portMAX_DELAY);
    shared.notice = notice;
    shared.noticeAt = millis();
    xSemaphoreGive(stateMutex);
}

String httpError(int code)
{
    if (code == 401) return "Pairing key rejected";
    if (code == 403) return "Control disabled";
    if (code == 409) return "Busy / update in progress";
    if (code == 404) return "API path not found";
    if (code <= 0) return "Server unreachable";
    return "HTTP " + String(code);
}

void beginHttp(HTTPClient &http, WiFiClient &client, const char *path)
{
    String base = DeviceConfig::baseUrl();
    while (base.endsWith("/")) base.remove(base.length() - 1);
    http.begin(client, base + path);
    http.useHTTP10(true);
    http.setConnectTimeout(1500);
    http.setTimeout(2000);
    http.setFollowRedirects(HTTPC_DISABLE_FOLLOW_REDIRECTS);
    http.addHeader("Authorization", String("Bearer ") + DeviceConfig::apiKey());
}

bool seconds(JsonVariantConst value, int32_t &result)
{
    if (value.isNull()) { result = -1; return true; }
    if (!value.is<int32_t>() || value.as<int32_t>() < 0) return false;
    result = value.as<int32_t>();
    return true;
}

bool parseStatus(JsonDocument &doc, Snapshot &next)
{
    if (doc["apiVersion"] != 1 || !doc["mode"].is<const char *>()
        || !doc["detail"].is<const char *>() || !doc["isRunning"].is<bool>()
        || !doc["autoLoopEnabled"].is<bool>() || !doc["controlEnabled"].is<bool>()
        || !doc["facilities"].is<JsonArray>() || doc["facilities"].size() != 4
        || !doc.containsKey("nextRunInSeconds")
        || !seconds(doc["nextRunInSeconds"], next.nextRun)) return false;
    next.mode = doc["mode"].as<String>();
    next.detail = doc["detail"].as<String>();
    next.running = doc["isRunning"];
    next.autoLoop = doc["autoLoopEnabled"];
    next.control = doc["controlEnabled"];
    next.lastRun = doc["lastRunSummary"] | "";
    next.lastRunFailed = doc["lastRunFailed"] | false;
    uint8_t seen = 0;
    for (JsonObject row : doc["facilities"].as<JsonArray>()) {
        int index = -1;
        for (int i = 0; i < 4; ++i)
            if (strcmp(row["key"] | "", KEYS[i]) == 0) index = i;
        if (index < 0 || (seen & (1 << index)) || !row["enabled"].is<bool>()
            || !row["phase"].is<const char *>() || !row["itemName"].is<const char *>()
            || !row["plannedItemName"].is<const char *>() || !row.containsKey("remainingSeconds")) return false;
        auto &f = next.facilities[index];
        if (!seconds(row["remainingSeconds"], f.remaining)) return false;
        seen |= 1 << index;
        f.enabled = row["enabled"];
        f.item = row["itemName"].as<String>();
        f.plannedItem = row["plannedItemName"].as<String>();
        f.phase = row["phase"].as<String>();
        f.reason = row["manualReason"] | "";
    }
    return seen == 15;
}

void pollStatus()
{
    WiFiClient client;
    HTTPClient http;
    beginHttp(http, client, "/api/v1/status");
    int code = http.GET();
    if (code != HTTP_CODE_OK) {
        http.end();
        setError(httpError(code));
        return;
    }
    // Our API always sends Content-Length. Bound allocations before reading a response.
    if (http.getSize() <= 0 || http.getSize() > 16384) {
        http.end();
        setError("Invalid response size");
        return;
    }
    String payload = http.getString();
    int expectedSize = http.getSize();
    http.end();
    DynamicJsonDocument doc(32768);
    Snapshot next;
    if (payload.length() != static_cast<unsigned>(expectedSize)
        || deserializeJson(doc, payload) || !parseStatus(doc, next)) {
        setError("Invalid API v1 response");
        return;
    }
    next.valid = true;
    next.error = "";
    next.fetchedAt = millis();
    xSemaphoreTake(stateMutex, portMAX_DELAY);
    next.notice = shared.notice;
    next.noticeAt = shared.noticeAt;
    shared = next;
    xSemaphoreGive(stateMutex);
}

void sendAction(Command command)
{
    Snapshot s = readSnapshot();
    if (!fresh(s) || !s.control) {
        setNotice(s.control ? "Device offline" : "Control disabled");
        return;
    }
    const char *action = command == Command::Start ? "start" : command == Command::Stop ? "stop"
        : command == Command::Pause ? "pause" : "resume";
    WiFiClient client;
    HTTPClient http;
    beginHttp(http, client, "/api/v1/action");
    http.addHeader("Content-Type", "application/json");
    StaticJsonDocument<64> doc;
    doc["action"] = action;
    String body;
    serializeJson(doc, body);
    int code = http.POST(body);
    http.end();
    // Never retry a control request: a lost reply does not mean the action was rejected.
    setNotice(code == 200 || code == 202 ? String(action) + " accepted"
        : code <= 0 ? "Result unknown; refreshing" : httpError(code));
}

void networkTask(void *)
{
    WiFi.mode(WIFI_STA);
    WiFi.setHostname("deltacrafter-s3");
    WiFi.setAutoReconnect(true);
    WiFi.persistent(false);
    if (!DeviceConfig::ssid().isEmpty()) WiFi.begin(DeviceConfig::ssid().c_str(), DeviceConfig::password().c_str());
    else WiFi.begin();
    uint32_t lastWifiAttempt = millis(), lastPoll = 0;
    bool wasConnected = false;
    for (;;) {
        Command command = Command::Refresh;
        bool hasCommand = xQueueReceive(commands, &command, pdMS_TO_TICKS(50)) == pdTRUE;
        bool connected = WiFi.status() == WL_CONNECTED;
        if (!DeviceConfig::valid()) {
            setError("Setup via USB serial");
        } else if (!connected) {
            setError("Wi-Fi disconnected");
            if (millis() - lastWifiAttempt >= 10000) {
                WiFi.reconnect();
                lastWifiAttempt = millis();
            }
        } else {
            if (hasCommand && command != Command::Refresh) sendAction(command);
            if (!wasConnected || hasCommand || millis() - lastPoll >= DELTA_POLL_MS) {
                pollStatus();
                lastPoll = millis();
            }
        }
        wasConnected = connected;
    }
}

String clipped(const String &value, int width)
{
    String result;
    int used = 0;
    for (unsigned i = 0; i < value.length();) {
        uint8_t lead = value[i];
        unsigned count = lead < 0x80 ? 1 : lead < 0xE0 ? 2 : lead < 0xF0 ? 3 : 4;
        int glyphWidth = count == 1 ? 8 : 16;
        if (used + glyphWidth > width || i + count > value.length()) break;
        if (lead >= 0x20) result += value.substring(i, i + count);
        used += glyphWidth;
        i += count;
    }
    return result;
}

void textAt(int x, int baseline, int width, const String &value, uint16_t color = COLOR_FG)
{
    canvas.setFont(u8g2_font_unifont_t_chinese4);
    canvas.setTextColor(color);
    canvas.setCursor(x, baseline);
    canvas.print(clipped(value, width));
}

String countdown(int32_t remaining, const Snapshot &s)
{
    if (remaining < 0 || !fresh(s)) return "--:--";
    int32_t elapsed = (millis() - s.fetchedAt) / 1000;
    uint32_t secondsLeft = remaining > elapsed ? remaining - elapsed : 0;
    if (secondsLeft > 359999) return ">99h";
    char result[10];
    snprintf(result, sizeof(result), "%02lu:%02lu:%02lu",
        static_cast<unsigned long>(secondsLeft / 3600),
        static_cast<unsigned long>(secondsLeft / 60 % 60),
        static_cast<unsigned long>(secondsLeft % 60));
    return result;
}

String phaseName(const String &phase)
{
    if (phase == "Crafting") return "制造中";
    if (phase == "ReadyToCollect") return "可领取";
    if (phase == "NeedsManual") return "需人工";
    if (phase == "Idle") return "空闲";
    return "未识别";
}

void draw()
{
    Snapshot s = readSnapshot();
    bool online = fresh(s);
    canvas.fillScreen(COLOR_BG);
    textAt(6, 17, 128, "DeltaCrafter", COLOR_CYAN);
    String state = !online ? (s.valid ? "STALE" : "OFFLINE") : s.running ? "RUNNING"
        : s.lastRunFailed ? "FAILED" : s.autoLoop ? "AUTO" : "IDLE";
    textAt(240, 17, 80, state, online ? (s.lastRunFailed ? COLOR_RED : COLOR_GREEN) : COLOR_AMBER);
    canvas.drawFastHLine(0, 24, 320, COLOR_DIM);
    if (!s.valid) {
        textAt(8, 65, 304, s.error, COLOR_AMBER);
        textAt(8, 97, 304, WiFi.status() == WL_CONNECTED ? WiFi.localIP().toString() : "Wi-Fi 2.4 GHz", COLOR_DIM);
    } else if (page == 0) {
        for (int i = 0; i < 4; ++i) {
            const auto &f = s.facilities[i];
            int y = 28 + i * 28;
            uint16_t color = f.phase == "NeedsManual" ? COLOR_RED : f.phase == "ReadyToCollect" ? COLOR_GREEN : COLOR_FG;
            textAt(4, y + 14, 64, NAMES[i], f.enabled ? COLOR_FG : COLOR_DIM);
            textAt(76, y + 14, 160, f.item.isEmpty() ? f.plannedItem : f.item, color);
            textAt(248, y + 14, 72, countdown(f.remaining, s), color);
            canvas.setFont(static_cast<const GFXfont *>(nullptr));
            canvas.setTextColor(COLOR_DIM);
            canvas.setCursor(76, y + 17);
            canvas.print(f.enabled ? f.phase : "Disabled");
        }
    } else {
        const auto &f = s.facilities[page - 1];
        textAt(6, 44, 120, NAMES[page - 1], COLOR_CYAN);
        textAt(140, 44, 170, phaseName(f.phase), f.phase == "NeedsManual" ? COLOR_RED : COLOR_FG);
        textAt(6, 68, 304, f.item.isEmpty() ? "尚无当前物品" : f.item);
        textAt(6, 92, 304, String("计划: ") + f.plannedItem, COLOR_DIM);
        textAt(6, 116, 304, f.reason.isEmpty() ? s.detail : f.reason, f.reason.isEmpty() ? COLOR_DIM : COLOR_RED);
        textAt(6, 139, 304, String("剩余: ") + countdown(f.remaining, s));
    }
    canvas.drawFastHLine(0, 145, 320, COLOR_DIM);
    String footer;
    if (!s.notice.isEmpty() && millis() - s.noticeAt < 5000) footer = s.notice;
    else if (!online) footer = s.error.isEmpty() ? "Status expired" : s.error;
    else footer = String(s.autoLoop ? "AUTO  Next " : "MANUAL  Next ") + countdown(s.nextRun, s);
    textAt(6, 163, 304, footer, online ? COLOR_DIM : COLOR_AMBER);
    display.pushImage(0, 0, 320, 170, canvas.getFramebuffer());
}

void handleButton(Button &button, bool isLeft)
{
    uint32_t now = millis();
    bool level = digitalRead(button.pin);
    if (level != button.raw) { button.raw = level; button.changed = now; }
    if (now - button.changed >= 35 && button.stable != level) {
        button.stable = level;
        if (!level) { button.pressed = now; button.longSent = false; }
        else if (!button.longSent) {
            if (isLeft) page = (page + 1) % 5;
            else {
                Command command = Command::Refresh;
                if (xQueueSend(commands, &command, 0) != pdTRUE) setNotice("Request pending");
            }
        }
    }
    if (!button.stable && !button.longSent && now - button.pressed >= 1500) {
        button.longSent = true;
        Snapshot s = readSnapshot();
        if (!fresh(s) || !s.control) {
            setNotice(s.control ? "Device offline" : "Control disabled");
            return;
        }
        Command command = isLeft ? (s.autoLoop ? Command::Pause : Command::Resume)
            : (s.running ? Command::Stop : Command::Start);
        if (xQueueSend(commands, &command, 0) != pdTRUE) setNotice("Request pending");
        else setNotice("Sending request");
    }
}
} // namespace

void setup()
{
    Serial.begin(115200);
    DeviceConfig::load();
    pinMode(15, OUTPUT);
    digitalWrite(15, HIGH);
    pinMode(0, INPUT_PULLUP);
    pinMode(14, INPUT_PULLUP);
    display.begin();
#if DELTA_LCD_NEW_PANEL
    for (const auto &entry : lcdInitCommands) {
        display.writecommand(entry.command);
        for (uint8_t i = 0; i < (entry.length & 0x7F); ++i) display.writedata(entry.data[i]);
        if (entry.length & 0x80) delay(120);
    }
#endif
    display.setRotation(1);
    display.invertDisplay(true);
    pinMode(38, OUTPUT);
    digitalWrite(38, HIGH);
    stateMutex = xSemaphoreCreateMutex();
    commands = xQueueCreate(1, sizeof(Command));
    if (!stateMutex || !commands || !canvas.begin(GFX_SKIP_OUTPUT_BEGIN)) {
        display.fillScreen(TFT_BLACK);
        display.setTextColor(TFT_RED);
        display.drawString("Display / memory init failed", 4, 60, 2);
        while (true) delay(1000);
    }
    canvas.setUTF8Print(true);
    canvas.setTextWrap(false);
    draw();
    if (xTaskCreate(networkTask, "delta-http", 8192, nullptr, 1, nullptr) != pdPASS)
        setError("Network task init failed");
}

void loop()
{
    if (Serial.available()) {
        Snapshot s = readSnapshot();
        DeviceConfig::setHealth(fresh(s), s.error, s.valid ? 4 : 0);
    }
    DeviceConfig::handleSerial();
    handleButton(left, true);
    handleButton(right, false);
    static uint32_t lastDraw = 0;
    if (millis() - lastDraw >= 250) { draw(); lastDraw = millis(); }
    delay(5);
}
