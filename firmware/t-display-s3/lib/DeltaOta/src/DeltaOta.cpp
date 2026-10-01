#include "DeltaOta.h"
#include "../../../include/firmware_version.h"
#include <ArduinoJson.h>
#include <HTTPClient.h>
#include <Preferences.h>
#include <WiFi.h>
#include <esp_ota_ops.h>
#include <mbedtls/sha256.h>
#include <mbedtls/version.h>
#include <memory>

namespace DeltaOta {
namespace {
constexpr uint32_t MonitorAddress = 0x10000, AudioAddress = 0x650000, PartitionSize = 0x640000;
constexpr int BufferSize = 4096;

class Digest {
    mbedtls_sha256_context ctx;
public:
    Digest() {
        mbedtls_sha256_init(&ctx);
#if MBEDTLS_VERSION_MAJOR >= 3
        mbedtls_sha256_starts(&ctx, 0);
#else
        mbedtls_sha256_starts_ret(&ctx, 0);
#endif
    }
    ~Digest() { mbedtls_sha256_free(&ctx); }
    void add(const uint8_t *bytes, size_t size) {
#if MBEDTLS_VERSION_MAJOR >= 3
        mbedtls_sha256_update(&ctx, bytes, size);
#else
        mbedtls_sha256_update_ret(&ctx, bytes, size);
#endif
    }
    String finish() {
        uint8_t bytes[32]; char hex[65];
#if MBEDTLS_VERSION_MAJOR >= 3
        mbedtls_sha256_finish(&ctx, bytes);
#else
        mbedtls_sha256_finish_ret(&ctx, bytes);
#endif
        for (int i = 0; i < 32; ++i) snprintf(hex + i * 2, 3, "%02x", bytes[i]);
        return String(hex);
    }
};

bool hashValid(const String &hash)
{
    if (hash.length() != 64) return false;
    for (unsigned i = 0; i < hash.length(); ++i)
        if (!((hash[i] >= '0' && hash[i] <= '9') || (hash[i] >= 'a' && hash[i] <= 'f'))) return false;
    return true;
}

const esp_partition_t *partition(bool audio)
{
    auto p = esp_partition_find_first(ESP_PARTITION_TYPE_APP,
        audio ? ESP_PARTITION_SUBTYPE_APP_OTA_1 : ESP_PARTITION_SUBTYPE_APP_OTA_0, nullptr);
    return p && p->address == (audio ? AudioAddress : MonitorAddress) && p->size == PartitionSize ? p : nullptr;
}

bool parse(JsonObjectConst root, Manifest &m)
{
    if ((root["schemaVersion"] | 0) != 1 || strcmp(root["board"] | "", "lilygo-t-display-s3")
        || strcmp(root["layout"] | "", "dual-16mb-v1") || (root["minimumUpdater"] | 0) != DELTA_UPDATER_ABI
        || !root["revision"].is<uint32_t>() || !root["images"].is<JsonArrayConst>()) return false;
    m.id = root["bundleId"] | ""; m.version = root["firmwareVersion"] | "";
    m.revision = root["revision"].as<uint32_t>();
    if (!hashValid(m.id) || m.revision < DELTA_FIRMWARE_REVISION
        || m.version != String("axeuh-tools-v") + m.revision) return false;
    JsonArrayConst images = root["images"].as<JsonArrayConst>();
    if (images.size() != 2) return false;
    bool foundMonitor = false, foundAudio = false;
    for (JsonObjectConst image : images) {
        String role = image["role"] | "";
        bool audio = role == "audio";
        if ((role != "monitor" && !audio) || (audio ? foundAudio : foundMonitor)
            || !image["size"].is<uint32_t>() || !image["address"].is<uint32_t>()
            || image["address"].as<uint32_t>() != (audio ? AudioAddress : MonitorAddress)
            || strcmp(image["file"] | "", audio ? "audio-firmware.bin" : "firmware.bin")) return false;
        Image &out = audio ? m.audio : m.monitor;
        out.size = image["size"].as<uint32_t>(); out.sha256 = image["sha256"] | "";
        if (out.size < 32 || out.size > PartitionSize || !hashValid(out.sha256)) return false;
        (audio ? foundAudio : foundMonitor) = true;
    }
    return foundMonitor && foundAudio;
}

bool saveJob(const Manifest &m, Stage stage)
{
    DynamicJsonDocument doc(4096);
    doc["schemaVersion"] = 1; doc["board"] = "lilygo-t-display-s3"; doc["layout"] = "dual-16mb-v1";
    doc["minimumUpdater"] = DELTA_UPDATER_ABI; doc["revision"] = m.revision;
    doc["bundleId"] = m.id; doc["firmwareVersion"] = m.version;
    doc["stage"] = stage == Stage::Audio ? "audio" : stage == Stage::Monitor ? "monitor" : "done";
    auto images = doc.createNestedArray("images");
    for (bool audio : {false, true}) {
        auto image = images.createNestedObject();
        const Image &info = audio ? m.audio : m.monitor;
        image["role"] = audio ? "audio" : "monitor";
        image["file"] = audio ? "audio-firmware.bin" : "firmware.bin";
        image["address"] = audio ? AudioAddress : MonitorAddress;
        image["size"] = info.size; image["sha256"] = info.sha256;
    }
    if (doc.overflowed()) return false;
    String json; serializeJson(doc, json);
    Preferences prefs;
    if (!prefs.begin("delta-ota", false)) return false;
    bool ok = prefs.putString("job", json) == json.length();
    prefs.end();
    return ok;
}

bool matches(const esp_partition_t *p, const Image &image)
{
    if (!p || image.size > p->size) return false;
    std::unique_ptr<uint8_t[]> buffer(new uint8_t[BufferSize]);
    Digest digest;
    for (uint32_t offset = 0; offset < image.size;) {
        size_t size = min(uint32_t(BufferSize), image.size - offset);
        if (esp_partition_read(p, offset, buffer.get(), size) != ESP_OK) return false;
        digest.add(buffer.get(), size); offset += size; delay(1);
    }
    return digest.finish() == image.sha256;
}

void begin(HTTPClient &http, WiFiClient &client, const Config &config, const String &path)
{
    String base = config.url; while (base.endsWith("/")) base.remove(base.length() - 1);
    http.begin(client, base + path);
    http.useHTTP10(true); http.setReuse(false);
    http.setConnectTimeout(3000); http.setTimeout(10000);
    http.setFollowRedirects(HTTPC_DISABLE_FOLLOW_REDIRECTS);
    http.addHeader("Authorization", String("Bearer ") + config.key);
}

bool download(const Config &config, const Manifest &m, bool audio, Progress progress, String &error)
{
    auto target = partition(audio);
    auto running = esp_ota_get_running_partition();
    if (!target || !running || target->address == running->address) { error = "Unsafe partition layout"; return false; }
    const Image &image = audio ? m.audio : m.monitor;
    const char *role = audio ? "audio" : "monitor";
    WiFiClient client; HTTPClient http;
    begin(http, client, config, String("/api/v1/firmware-image?bundle=") + m.id + "&role=" + role);
    int code = http.GET();
    if (code != 200 || http.getSize() != int(image.size)) {
        error = String("Firmware download HTTP ") + code;
        http.end(); return false;
    }
    esp_ota_handle_t handle = 0;
    bool opened = false, ok = true;
    std::unique_ptr<uint8_t[]> buffer(new uint8_t[BufferSize]);
    Digest digest;
    uint32_t offset = 0, lastData = millis();
    int lastPercent = -1;
    auto stream = http.getStreamPtr();
    while (offset < image.size) {
        int available = stream->available();
        if (available <= 0) {
            if (!http.connected() || millis() - lastData > 15000) { error = "Download interrupted; retry"; ok = false; break; }
            delay(2); continue;
        }
        size_t count = min(uint32_t(BufferSize), min(uint32_t(available), image.size - offset));
        int read = stream->read(buffer.get(), count);
        if (read <= 0) { delay(2); continue; }
        if (!opened) {
            // IDF verifies the complete ESP image at esp_ota_end; the hash pins the exact bundle.
            if (esp_ota_begin(target, image.size, &handle) != ESP_OK) { error = "Cannot begin flash write"; ok = false; break; }
            opened = true;
        }
        if (esp_ota_write(handle, buffer.get(), read) != ESP_OK) { error = "Flash write failed"; ok = false; break; }
        digest.add(buffer.get(), read); offset += read; lastData = millis();
        int percent = offset * 100ULL / image.size;
        if (percent != lastPercent) { progress(role, percent, ""); lastPercent = percent; }
        delay(1);
    }
    http.end();
    if (ok && digest.finish() != image.sha256) { error = "Firmware SHA-256 mismatch"; ok = false; }
    if (!ok) { if (opened) esp_ota_abort(handle); return false; }
    if (!opened || esp_ota_end(handle) != ESP_OK) { error = "ESP image validation failed"; return false; }
    return true;
}

struct Transaction {
    const Config &config;
    const Job &job;
    Progress progress;
    String &error;
    bool monitorRunning() {
        auto running = esp_ota_get_running_partition();
        if (partition(false) && partition(true) && running && running->address == MonitorAddress
            && esp_ota_set_boot_partition(partition(false)) == ESP_OK) return true;
        error = "Unsafe partition layout"; return false;
    }
    bool audioRunningAndVerified() {
        auto running = esp_ota_get_running_partition();
        if (job.stage != Stage::None && job.stage != Stage::Invalid && running
            && running->address == AudioAddress && matches(partition(true), job.manifest.audio)) return true;
        error = "Update state or audio image invalid"; return false;
    }
    bool save(Stage stage) {
        if (saveJob(job.manifest, stage)) return true;
        error = "Cannot save update state"; return false;
    }
    bool writeAudio() { return download(config, job.manifest, true, progress, error); }
    bool writeMonitor() { return download(config, job.manifest, false, progress, error); }
    bool monitorMatches() { return matches(partition(false), job.manifest.monitor); }
    bool boot(bool audio) {
        auto target = partition(audio);
        if (target && esp_ota_set_boot_partition(target) == ESP_OK) return true;
        error = "Cannot select update partition"; return false;
    }
    bool bootAudio() { return boot(true); }
    bool bootMonitor() { return boot(false); }
};
}

bool loadConfig(Config &config, String &error)
{
    Preferences prefs;
    if (!prefs.begin("delta-monitor", true)) { error = "Pairing configuration missing"; return false; }
    String json = prefs.getString("config", ""); prefs.end();
    DynamicJsonDocument doc(3072);
    if (json.length() > 2048 || deserializeJson(doc, json)) { error = "Pairing configuration invalid"; return false; }
    config.ssid = doc["wifiSsid"] | ""; config.password = doc["wifiPassword"] | "";
    config.url = doc["baseUrl"] | ""; config.key = doc["apiKey"] | "";
    if (!config.url.startsWith("http://") || config.url.length() > 192 || config.url.indexOf('@') >= 0
        || config.key.length() < 32 || config.key.length() > 128 || config.ssid.isEmpty()
        || config.ssid.length() > 32 || config.password.length() > 64) {
        error = "Pairing configuration invalid"; return false;
    }
    return true;
}

Job loadJob()
{
    Job job;
    Preferences prefs;
    if (!prefs.begin("delta-ota", true)) return job;
    String json = prefs.getString("job", ""); prefs.end();
    if (json.isEmpty()) return job;
    job.stage = Stage::Invalid;
    DynamicJsonDocument doc(4096);
    if (json.length() > 4096 || deserializeJson(doc, json) || !parse(doc.as<JsonObjectConst>(), job.manifest)) return job;
    String stage = doc["stage"] | "";
    job.stage = stage == "audio" ? Stage::Audio : stage == "monitor" ? Stage::Monitor : stage == "done" ? Stage::Done : Stage::Invalid;
    return job;
}

bool check(const Config &config, Manifest &manifest, String &error)
{
    WiFiClient client; HTTPClient http;
    begin(http, client, config, "/api/v1/firmware");
    int code = http.GET(), size = http.getSize();
    if (code != 200 || size <= 0 || size > 4096) {
        error = code == 404 ? "电脑尚未准备固件包" : code == 403 ? "电脑未允许设备控制"
            : code == 401 ? "配对密钥错误" : "固件检查失败";
        http.end(); return false;
    }
    String body = http.getString(); http.end();
    DynamicJsonDocument doc(4096);
    if (body.length() != unsigned(size) || deserializeJson(doc, body) || !parse(doc.as<JsonObjectConst>(), manifest)) {
        error = "固件不兼容或版本过旧"; return false;
    }
    return true;
}

bool installAudio(const Config &config, const Manifest &m, Progress progress, String &error)
{
    Job job;
    job.stage = Stage::Audio;
    job.manifest = m;
    Transaction ops{config, job, progress, error};
    return audioTransaction(ops);
}

bool installMonitor(const Config &config, const Job &job, Progress progress, String &error)
{
    Transaction ops{config, job, progress, error};
    return monitorTransaction(ops);
}

bool monitorBoot(String &error)
{
    Job job = loadJob();
    if (job.stage == Stage::None || job.stage == Stage::Audio) return true;
    if (job.stage == Stage::Done && matches(partition(false), job.manifest.monitor)
        && matches(partition(true), job.manifest.audio)) {
        Preferences prefs;
        if (!prefs.begin("delta-ota", false)) { error = "Cannot finish update state"; return false; }
        bool ok = prefs.remove("job"); prefs.end();
        if (!ok) error = "Cannot finish update state";
        return ok;
    }
    if (job.stage != Stage::Invalid && matches(partition(true), job.manifest.audio)
        && esp_ota_set_boot_partition(partition(true)) == ESP_OK) {
        delay(100); ESP.restart();
    }
    error = "Update incomplete; retry from settings";
    return false;
}
}
