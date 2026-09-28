#include <Arduino.h>
#include <Axeuh_UI.h>
#include <ArduinoJson.h>
#include <HTTPClient.h>
#include <TFT_eSPI.h>
#include <WiFi.h>
#include "config.h"
#include "lcd_init.h"
#include "device_config.h"
#include "ui_state.h"
#include "ui_button.h"
#include "ui_indicators.h"
#include "mono_display.h"

#if TFT_WIDTH != 170 || TFT_HEIGHT != 320 || TFT_WR != 8 || TFT_RD != 9 || TFT_BL != 38
#error "Select TFT_eSPI Setup206_LilyGo_T_Display_S3.h for this firmware"
#endif

namespace {
constexpr uint32_t STALE_MS = DELTA_POLL_MS * 3 + 5000;
const char *const KEYS[] = {"workbench", "pharmacy-lab", "armor-station", "tech-center"};
const char *const NAMES[] = {"工作台", "制药台", "防具台", "技术中心"};

struct Facility {
    String item, plannedItem, phase, reason, craftMode;
    bool enabled = false;
    int32_t remaining = -1, total = -1;
};

struct Snapshot {
    Facility facilities[4];
    String mode, detail, lastRun, error = "Connecting Wi-Fi", notice;
    String gameState, gameDetail, afterRun;
    bool valid = false, running = false, autoLoop = false, control = false;
    bool lastRunFailed = false;
    bool steamDetection = false, settingsSupported = false, syncSupported = false;
    int32_t nextRun = -1;
    uint32_t fetchedAt = 0, noticeAt = 0;
};

enum class Command : uint8_t { Refresh, Start, Sync, Stop, Pause, Resume };
enum class Setting : uint8_t { None, FacilityEnabled, CraftMode, AutoLoop, SteamDetection, AfterRun };
struct Request {
    Command command = Command::Refresh;
    Setting setting = Setting::None;
    uint8_t facility = 0, value = 0;
};
TFT_eSPI display;
MonoDisplay canvas;
Axeuh_UI uiEngine(&canvas);
Axeuh_UI_Panel mainPanel;
Axeuh_UI_StatusBar statusBar;
MenuOption menuOptions[6];
Axeuh_UI_TextMenu settingsMenu(menuOptions, 6);
Axeuh_UI_Panel settingsPanel;
float focusX = 1, focusY = 1, focusW = 158, focusH = 70;
IN_PUT_Mode pendingInput = STOP;
SemaphoreHandle_t stateMutex;
QueueHandle_t commands;
Snapshot shared;
UiButton cycleButton, confirmButton;
UiState ui;
bool requestPending = false;
uint32_t batteryMv = 0;

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
    next.gameState = doc["game"]["state"] | "Unknown";
    next.gameDetail = doc["game"]["detail"] | "";
    next.afterRun = doc["afterRun"] | "CloseGame";
    next.steamDetection = doc["steamDetectionEnabled"] | false;
    next.settingsSupported = doc["settingsSupported"] | false;
    next.syncSupported = doc["syncSupported"] | false;
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
        if (!seconds(row["totalSeconds"], f.total)) return false;
        seen |= 1 << index;
        f.enabled = row["enabled"];
        f.item = row["itemName"].as<String>();
        f.plannedItem = row["plannedItemName"].as<String>();
        f.phase = row["phase"].as<String>();
        f.craftMode = row["craftMode"] | "Custom";
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
    if (!fresh(s) || !s.control || s.running) {
        setNotice(!fresh(s) ? "设备离线" : !s.control ? "电脑未允许设备控制" : "任务正在执行");
        return;
    }
    const char *action = command == Command::Start ? "start" : command == Command::Stop ? "stop"
        : command == Command::Sync ? "sync" : command == Command::Pause ? "pause" : "resume";
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
    setNotice(code == 200 || code == 202
        ? (command == Command::Sync ? "已提交识别当前任务" : "已提交开始制造")
        : code <= 0 ? "结果未知,正在刷新" : code == 409 ? "任务执行中或正在更新" : httpError(code));
}

const char *const CRAFT_MODES[] = {"Custom", "HourlyProfit", "TotalProfit"};
const char *const AFTER_RUN[] = {"CloseGame", "KeepRunning", "KeepAtLobby"};

void saveSetting(const Request &request)
{
    Snapshot s = readSnapshot();
    if (!fresh(s) || !s.control || !s.settingsSupported) {
        setNotice(!fresh(s) ? "设备离线" : !s.control ? "电脑未允许设备控制" : "请更新电脑客户端");
        return;
    }
    StaticJsonDocument<256> doc;
    if (request.setting == Setting::FacilityEnabled || request.setting == Setting::CraftMode)
        doc["facility"] = KEYS[request.facility];
    switch (request.setting) {
        case Setting::FacilityEnabled: doc["enabled"] = bool(request.value); break;
        case Setting::CraftMode: doc["craftMode"] = CRAFT_MODES[request.value]; break;
        case Setting::AutoLoop: doc["autoLoopEnabled"] = bool(request.value); break;
        case Setting::SteamDetection: doc["steamDetectionEnabled"] = bool(request.value); break;
        case Setting::AfterRun: doc["afterRun"] = AFTER_RUN[request.value]; break;
        default: return;
    }
    String body;
    serializeJson(doc, body);
    WiFiClient client;
    HTTPClient http;
    beginHttp(http, client, "/api/v1/settings");
    // The server may briefly wait for its task lock before saving a setting.
    http.setTimeout(6000);
    http.addHeader("Content-Type", "application/json");
    int code = http.POST(body);
    http.end();
    setNotice(code == 200 ? "设置已保存" : code <= 0 ? "保存结果未知,正在刷新"
        : code == 403 ? "电脑未允许设备控制" : code == 409 || code == 503 ? "电脑忙,请稍后重试" : httpError(code));
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
        Request request;
        bool hasCommand = xQueueReceive(commands, &request, pdMS_TO_TICKS(50)) == pdTRUE;
        bool connected = WiFi.status() == WL_CONNECTED;
        if (!DeviceConfig::valid()) {
            setError("Setup via USB serial");
        } else if (!connected) {
            setError("Wi-Fi disconnected");
            if (hasCommand) setNotice("设备离线,请求未发送");
            if (millis() - lastWifiAttempt >= 10000) {
                WiFi.reconnect();
                lastWifiAttempt = millis();
            }
        } else {
            if (hasCommand && request.setting != Setting::None) saveSetting(request);
            else if (hasCommand && request.command != Command::Refresh) sendAction(request.command);
            if (!wasConnected || hasCommand || millis() - lastPoll >= DELTA_POLL_MS) {
                pollStatus();
                lastPoll = millis();
            }
        }
        if (hasCommand) {
            xSemaphoreTake(stateMutex, portMAX_DELAY);
            requestPending = false;
            xSemaphoreGive(stateMutex);
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
        String glyph = value.substring(i, i + count);
        int glyphWidth = canvas.getUTF8Width(glyph.c_str());
        if (used + glyphWidth > width || i + count > value.length()) break;
        if (lead >= 0x20) result += value.substring(i, i + count);
        used += glyphWidth;
        i += count;
    }
    return result;
}

void textAt(int x, int baseline, int width, const String &value)
{
    canvas.setDrawColor(1);
    canvas.drawUTF8(x, baseline, clipped(value, width).c_str());
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

void border(int x, int y, int width, int height, bool selected)
{
    canvas.setDrawColor(1);
    for (int i = 0; i < (selected ? 3 : 1); ++i)
        canvas.drawFrame(x + i, y + i, width - 2 * i, height - 2 * i);
    if (!selected) return;
    if (height > 40) {
        canvas.drawFrame(x + 5, y + 5, width - 10, height - 10);
        for (int dx : {0, width - 18}) {
            canvas.drawBox(x + dx, y, 18, 6);
            canvas.drawBox(x + dx, y + height - 6, 18, 6);
        }
    } else {
        canvas.drawTriangle(x + 7, y + height / 2 - 4,
            x + 7, y + height / 2 + 4, x + 12, y + height / 2);
    }
}

String craftModeName(const String &mode)
{
    if (mode == "HourlyProfit") return "每小时利润";
    if (mode == "TotalProfit") return "总利润";
    return "自定义";
}

String afterRunName(const String &mode)
{
    if (mode == "KeepRunning") return "最小化后台";
    if (mode == "KeepAtLobby") return "停留大厅";
    return "关闭游戏";
}

String gameStatus(const Snapshot &s)
{
    if (!fresh(s)) return s.valid ? "连接中断 / 游戏状态未知" : "连接中 / 游戏状态未知";
    if (s.gameState == "Playing") {
        String detail = s.gameDetail;
        if (detail.startsWith("Steam ")) detail.remove(0, 6);
        return detail.isEmpty() ? "游戏中" : detail;
    }
    if (s.gameState == "NotPlaying") return "未在游戏中";
    if (s.gameState == "Unavailable") return "游戏状态暂不可用";
    return "游戏状态查询中";
}

void menuRow(uint8_t index, const String &label)
{
    String text = clipped(label, 276);
    if (text != menuOptions[index].c_str()) {
        menuOptions[index].set_str(text);
        settingsMenu.menu_str_len[index] = 0;
    }
}

void activateSelection();

void drawProgress(int x, int y, const Facility &facility, const Snapshot &s)
{
    canvas.setDrawColor(1);
    canvas.drawFrame(x, y, 140, 4);
    if (!s.valid) return;
    if (facility.phase == "ReadyToCollect") {
        canvas.drawBox(x + 1, y + 1, 138, 2);
    } else if (facility.phase == "Crafting") {
        int32_t remaining = facility.remaining;
        if (remaining >= 0 && fresh(s))
            remaining = max(int32_t(0), remaining - int32_t((millis() - s.fetchedAt) / 1000));
        int pixels = progressPixels(remaining, facility.total, 138);
        if (pixels > 0) canvas.drawBox(x + 1, y + 1, pixels, 2);
    }
}

void updateBattery()
{
    static uint32_t lastSample = 0;
    if (batteryMv != 0 && millis() - lastSample < 5000) return;
    lastSample = millis();
    uint32_t sum = 0;
    for (int i = 0; i < 8; ++i) sum += analogReadMilliVolts(4) * 2;
    batteryMv = sum / 8;
}

void drawBattery()
{
    constexpr int x = 283, y = 151;
    canvas.setDrawColor(1);
    canvas.drawFrame(x, y, 25, 12);
    canvas.drawBox(x + 25, y + 3, 3, 6);
    int bars = batteryBars(batteryMv);
    if (bars < 0) {
        canvas.drawLine(x + 7, y + 3, x + 17, y + 8);
        canvas.drawLine(x + 7, y + 8, x + 17, y + 3);
    } else for (int i = 0; i < bars; ++i)
        canvas.drawBox(x + 3 + i * 5, y + 3, 4, 6);
}

IN_PUT_Mode readInput()
{
    IN_PUT_Mode input = pendingInput;
    pendingInput = STOP;
    return input;
}

void drawPanel(U8G2 *, IN_PUT_Mode, Axeuh_UI_Panel *, Axeuh_UI *)
{
    Snapshot s = readSnapshot();
    if (ui.page == UiPage::Home) {
        constexpr uint8_t order[] = {3, 0, 1, 2};
        for (uint8_t cell = 0; cell < 4; ++cell) {
            const auto &f = s.facilities[order[cell]];
            int x = (cell % 2) * 160, y = (cell / 2) * 72;
            border(x + 1, y + 1, 158, 70, false);
            textAt(x + 10, y + 21, 76, NAMES[order[cell]]);
            textAt(x + 87, y + 21, 64, !s.valid ? "待连接" : !f.enabled ? "已停用" : phaseName(f.phase));
            textAt(x + 10, y + 39, 140, !s.valid ? "等待数据" : f.item.isEmpty() ? f.plannedItem : f.item);
            textAt(x + 10, y + 56, 140, String("剩余 ") + countdown(f.remaining, s));
            drawProgress(x + 10, y + 61, f, s);
        }
    } else {
        String title = ui.page == UiPage::Global ? "全局设置"
            : ui.page == UiPage::CraftMode ? String(NAMES[ui.facility()]) + " / 制造模式"
            : ui.page == UiPage::AfterRun ? "收取后行为" : String(NAMES[ui.facility()]) + " / 设置";
        textAt(8, 18, 304, title);
        canvas.drawHLine(0, 24, 320);
        const auto &f = s.facilities[ui.facility()];
        if (ui.page == UiPage::Facility) {
            menuRow(0, String("设施: ") + (!s.valid ? "未知" : f.enabled ? "启用" : "停用"));
            menuRow(1, String("制造模式: ") + craftModeName(f.craftMode));
            menuRow(2, "返回主界面");
        } else if (ui.page == UiPage::Global) {
            menuRow(0, String("自动循环: ") + (s.autoLoop ? "开启" : "关闭"));
            menuRow(1, String("Steam 检测: ") + (s.steamDetection ? "开启" : "关闭"));
            menuRow(2, String("收取后: ") + afterRunName(s.afterRun));
            menuRow(3, s.running ? "开始制造 (执行中)" : "开始制造");
            menuRow(4, s.running ? "识别当前任务 (执行中)" : "识别当前任务");
            menuRow(5, "返回主界面");
        } else if (ui.page == UiPage::CraftMode) {
            for (uint8_t i = 0; i < 3; ++i)
                menuRow(i, String(f.craftMode == CRAFT_MODES[i] ? "* " : "  ") + craftModeName(CRAFT_MODES[i]));
            menuRow(3, "返回设施设置");
        } else {
            for (uint8_t i = 0; i < 3; ++i)
                menuRow(i, String(s.afterRun == AFTER_RUN[i] ? "* " : "  ") + afterRunName(AFTER_RUN[i]));
            menuRow(3, "返回全局设置");
        }
        settingsMenu.menuOptions_index = ui.count();
        settingsMenu.set_munber(ui.row);
        // Keep Axeuh's native moving/resizing focus and scrolling. Repaint its
        // XOR highlight as an outline to retain white text on black throughout.
        int px = settingsPanel.x_now + settingsMenu.pointer_x_now + 1;
        int py = settingsPanel.y_now + settingsMenu.pointer_y_now + 1;
        int pw = settingsMenu.pointer_w_now - 1, ph = settingsMenu.pointer_h_now + 1;
        settingsPanel.drawPanel(&canvas, &uiEngine, STOP);
        canvas.setClipWindow(5, 28, 315, 148);
        canvas.setDrawColor(2);
        canvas.drawBox(px, py, pw, ph);
        canvas.setDrawColor(1);
        border(px, py, pw + 7, ph, true);
        canvas.setMaxClipWindow();
        String hint = !s.notice.isEmpty() && millis() - s.noticeAt < 5000 ? s.notice
            : !fresh(s) ? "离线 / 可浏览,暂不能保存"
            : !s.control ? "只读 / 电脑未允许设备控制"
            : s.running || s.mode == "WaitingSchedule" || s.mode == "Faulted" ? s.detail : gameStatus(s);
        textAt(8, 165, 304, hint);
    }
}

void drawStatusBar(U8G2 *, Axeuh_UI *)
{
    if (ui.page != UiPage::Home) return;
    Snapshot s = readSnapshot();
    border(1, 145, 318, 25, false);
    textAt(18, 162, 256, gameStatus(s));
    drawBattery();
    float x = ui.home == 4 ? 1 : 1 + (ui.home % 2) * 160;
    float y = ui.home == 4 ? 145 : 1 + (ui.home / 2) * 72;
    uiEngine.animation(&focusX, x, uiEngine.fps, 0.5f);
    uiEngine.animation(&focusY, y, uiEngine.fps, 0.5f);
    uiEngine.animation(&focusW, ui.home == 4 ? 318.0f : 158.0f, uiEngine.fps, 0.5f);
    uiEngine.animation(&focusH, ui.home == 4 ? 25.0f : 70.0f, uiEngine.fps, 0.5f);
    border(lroundf(focusX), lroundf(focusY), lroundf(focusW), lroundf(focusH), true);
}

void draw()
{
    // Use Axeuh's public panel API in our cooperative loop so serial provisioning
    // and the framebuffer capture stay on the same task as drawing.
    uiEngine.IN_now = canvas.offset == 0 ? uiEngine.handleInput() : STOP;
    UiPage before = ui.page;
    if (canvas.offset == 0) {
        if (uiEngine.IN_now == DOWN) ui.move(1);
        else if (uiEngine.IN_now == SELECT) activateSelection();
    }
    if (ui.page != before) {
        bool forward = ui.page != UiPage::Home
            && before != UiPage::CraftMode && before != UiPage::AfterRun;
        canvas.startTransition(forward);
        settingsMenu.interface_text_y = 0;
        settingsMenu.interface_text_y_now = 0;
        settingsMenu.pointer_y_now = ui.row * 29;
        settingsMenu.pointer_w_now = 24;
        settingsMenu.pointer_h_now = 29;
    }
    uiEngine.animation(&canvas.offset, 0.0f, uiEngine.fps * 0.8f, 0.5f);
    canvas.clearBuffer();
    uiEngine.Panel->drawPanel(&canvas, &uiEngine, uiEngine.IN_now);
    uiEngine.StatusBar->drawStatusBar(&canvas, &uiEngine);
    canvas.present(display);
}

bool enqueueRequest(const Request &request)
{
    xSemaphoreTake(stateMutex, portMAX_DELAY);
    bool pending = requestPending;
    if (!pending) requestPending = true;
    xSemaphoreGive(stateMutex);
    if (pending) { setNotice("请求处理中,请稍候"); return false; }
    if (xQueueSend(commands, &request, 0) != pdTRUE) {
        xSemaphoreTake(stateMutex, portMAX_DELAY);
        requestPending = false;
        xSemaphoreGive(stateMutex);
        setNotice("请求排队中");
        return false;
    }
    setNotice(request.setting != Setting::None ? "正在保存..." : "正在提交...");
    return true;
}

bool submit(Setting setting, uint8_t value)
{
    Snapshot s = readSnapshot();
    if (!fresh(s) || !s.control || !s.settingsSupported) {
        setNotice(!fresh(s) ? "设备离线,暂不能保存" : !s.control ? "电脑未允许设备控制" : "请更新电脑客户端");
        return false;
    }
    Request request;
    request.setting = setting;
    request.facility = ui.facility();
    request.value = value;
    return enqueueRequest(request);
}

void submitAction(Command command)
{
    Snapshot s = readSnapshot();
    if (!fresh(s) || !s.control || s.running || (command == Command::Sync && !s.syncSupported)) {
        setNotice(!fresh(s) ? "设备离线,请求未发送" : !s.control ? "电脑未允许设备控制"
            : s.running ? "任务正在执行" : "请更新电脑客户端");
        return;
    }
    Request request;
    request.command = command;
    enqueueRequest(request);
}

void activateSelection()
{
    Snapshot s = readSnapshot();
    if (ui.page == UiPage::Home) {
        ui.open(ui.home == 4 ? UiPage::Global : UiPage::Facility);
    } else if (ui.page == UiPage::Facility) {
        if (ui.row == 0) submit(Setting::FacilityEnabled, !s.facilities[ui.facility()].enabled);
        else if (ui.row == 1) {
            uint8_t selected = 0;
            for (uint8_t i = 0; i < 3; ++i)
                if (s.facilities[ui.facility()].craftMode == CRAFT_MODES[i]) selected = i;
            ui.open(UiPage::CraftMode, selected);
        } else ui.open(UiPage::Home);
    } else if (ui.page == UiPage::Global) {
        if (ui.row == 0) submit(Setting::AutoLoop, !s.autoLoop);
        else if (ui.row == 1) submit(Setting::SteamDetection, !s.steamDetection);
        else if (ui.row == 2) {
            uint8_t selected = 0;
            for (uint8_t i = 0; i < 3; ++i) if (s.afterRun == AFTER_RUN[i]) selected = i;
            ui.open(UiPage::AfterRun, selected);
        } else if (ui.row == 3) submitAction(Command::Start);
        else if (ui.row == 4) submitAction(Command::Sync);
        else ui.open(UiPage::Home);
    } else if (ui.page == UiPage::CraftMode) {
        if (ui.row == 3 || submit(Setting::CraftMode, ui.row)) ui.open(UiPage::Facility, 1);
    } else if (ui.row == 3 || submit(Setting::AfterRun, ui.row)) {
        ui.open(UiPage::Global, 2);
    }
}

void handleButtons()
{
    uint32_t now = millis();
    bool cycle = cycleButton.update(digitalRead(UI_CYCLE_PIN), now);
    bool confirm = confirmButton.update(digitalRead(UI_CONFIRM_PIN), now);
    if (cycle && confirmButton.stable) pendingInput = DOWN;
    else if (confirm && cycleButton.stable) pendingInput = SELECT;
}

void captureScreen()
{
    Serial.println("{\"width\":320,\"height\":170,\"format\":\"rgb565le\",\"bytes\":108800}");
    uint16_t row[MonoDisplay::Width];
    for (unsigned y = 0; y < MonoDisplay::Height; ++y) {
        canvas.readRow(y, row);
        Serial.write(reinterpret_cast<const uint8_t *>(row), sizeof(row));
    }
    Serial.flush();
}
} // namespace

void setup()
{
    Serial.begin(115200);
    DeviceConfig::load();
    pinMode(15, OUTPUT);
    digitalWrite(15, HIGH);
    pinMode(UI_CONFIRM_PIN, INPUT_PULLUP);
    pinMode(UI_CYCLE_PIN, INPUT_PULLUP);
    analogReadResolution(12);
    analogSetPinAttenuation(4, ADC_11db);
    updateBattery();
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
    commands = xQueueCreate(1, sizeof(Request));
    if (!stateMutex || !commands || !uiEngine.get_xMutex() || !mainPanel.xMutex
        || !statusBar.xMutex || !settingsPanel.xMutex || !settingsMenu.xMutex) {
        display.fillScreen(TFT_BLACK);
        display.setTextColor(TFT_WHITE);
        display.drawString("Display / memory init failed", 4, 60, 2);
        while (true) delay(1000);
    }
    uiEngine.begin();
    uiEngine.width = 320;
    uiEngine.height = 170;
    uiEngine.fps = 60;
    uiEngine.font_offset_y = 14;
    canvas.setFont(u8g2_font_wqy16_t_gb2312);
    mainPanel.x = mainPanel.y = mainPanel.interlude_x = mainPanel.interlude_y = 0;
    mainPanel.interlude_w = mainPanel.interlude_h = 0;
    mainPanel.w = mainPanel.w_now = 320;
    mainPanel.h = mainPanel.h_now = 170;
    mainPanel.x_now = mainPanel.y_now = 0;
    mainPanel.set_draw(drawPanel);
    for (auto &option : menuOptions) {
        option.height = 29;
        option.x = 12;
    }
    settingsPanel.x = settingsPanel.x_now = 4;
    settingsPanel.y = settingsPanel.y_now = 27;
    settingsPanel.w = settingsPanel.w_now = 312;
    settingsPanel.h = settingsPanel.h_now = 121;
    settingsPanel.interlude_w = 0;
    settingsMenu.font_height = 16;
    settingsMenu.pointer_w_now = 24;
    settingsMenu.pointer_h_now = 29;
    settingsPanel.set(&settingsMenu);
    settingsPanel.if_Input = false;
    statusBar.set_draw(drawStatusBar);
    uiEngine.set(&mainPanel);
    uiEngine.set(&statusBar);
    uiEngine.set(readInput);
    DeviceConfig::setScreenCapture(captureScreen);
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
    updateBattery();
    handleButtons();
    static uint32_t lastDraw = 0;
    uint32_t now = millis();
    if (now - lastDraw >= 16) {
        uiEngine.fps = 1000.0f / min(uint32_t(66), max(uint32_t(16), now - lastDraw));
        lastDraw = now;
        draw();
    }
    delay(1);
}
