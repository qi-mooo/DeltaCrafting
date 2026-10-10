#include "firmware_update.h"
#include <mbedtls/base64.h>
#include <esp_system.h>

void FirmwareUpdate::status(JsonObject d) {
    auto running=esp_ota_get_running_partition();
    auto next=esp_ota_get_next_update_partition(nullptr);
    d["version"]=HARP_FIRMWARE_VERSION; d["board"]=HARP_BOARD_ID; d["layout"]=HARP_LAYOUT_ID;
    d["imageMarker"]=HARP_IMAGE_MARKER;
    d["partition"]=running?running->label:"unknown";
    d["maxBytes"]=next?next->size:0; d["chunkBytes"]=sizeof(buffer);
    d["phase"]=phase; d["received"]=received; d["size"]=expected; d["error"]=failure;
    d["busy"]=busy(); d["restart"]=rebootAt!=0;
}
void FirmwareUpdate::send(int code, const char *error) {
    DynamicJsonDocument d(1536); d["ok"]=code<300;
    status(d.as<JsonObject>());
    if (error) d["error"]=error;
    if (active) d["updateId"]=id;
    String result; serializeJson(d,result);
    server.sendHeader("Cache-Control","no-store"); server.send(code,"application/json",result);
}
bool FirmwareUpdate::auth() {
    if (server.header("Authorization")=="Bearer "+apiKey) return true;
    send(401,"pairing_required"); return false;
}
bool FirmwareUpdate::body(JsonDocument &d,size_t limit) {
    if (!auth()) return false;
    String data=server.arg("plain");
    if (data.length()>limit || deserializeJson(d,data) || !d.is<JsonObject>()) {
        send(400,"invalid_json"); return false;
    }
    return true;
}
bool FirmwareUpdate::session(JsonDocument &d) {
    if (!active || String(d["updateId"] | "")!=id) { send(409,"invalid_update_session"); return false; }
    touched=millis(); return true;
}
void FirmwareUpdate::fail(const char *error) {
    if (active) esp_ota_abort(handle);
    if (hashing) { mbedtls_sha256_free(&hash); hashing=false; }
    active=false; phase="error"; failure=error; id="";
    player.endMaintenance();
}
void FirmwareUpdate::prepare() {
    DynamicJsonDocument d(1024); if (!body(d,768)) return;
    if (busy()) { send(409,"update_in_progress"); return; }
    if (String(d["board"] | "")!=HARP_BOARD_ID || String(d["layout"] | "")!=HARP_LAYOUT_ID) {
        send(400,"wrong_board_or_layout"); return;
    }
    target=esp_ota_get_next_update_partition(nullptr);
    String digest=d["sha256"] | ""; digest.toLowerCase();
    bool valid=digest.length()==64;
    for (unsigned i=0;i<digest.length();++i) valid=valid && isHexadecimalDigit(digest[i]);
    if (!target || !d["size"].is<uint32_t>() || d["size"].as<uint32_t>()<288
        || d["size"].as<uint32_t>()>target->size || !valid) { send(400,"invalid_size_or_sha256"); return; }
    if (storage.owner()==CardOwner::Host) { send(409,"eject_sd_on_computer_first"); return; }
    if (!player.beginMaintenance()) { send(409,"player_stop_timeout"); return; }
    // Re-check after draining commands: an earlier Export may have exposed USB.
    if (storage.owner()==CardOwner::Host) { player.endMaintenance(); send(409,"eject_sd_on_computer_first"); return; }
    expected=d["size"]; expectedHash=digest; received=0; failure=""; image=FirmwareImageCheck{};
    esp_err_t result=esp_ota_begin(target,expected,&handle);
    if (result!=ESP_OK) { fail("ota_begin_failed"); send(500); return; }
    active=true; phase="receiving"; touched=millis();
    char nonce[17]; snprintf(nonce,sizeof(nonce),"%08x%08x",esp_random(),esp_random()); id=nonce;
    mbedtls_sha256_init(&hash); hashing=true; mbedtls_sha256_starts_ret(&hash,0);
    send(200);
}
void FirmwareUpdate::chunk() {
    DynamicJsonDocument d(6656); if (!body(d,6144) || !session(d)) return;
    if (!d["offset"].is<uint32_t>() || d["offset"].as<uint32_t>()!=received) { send(409,"offset_mismatch"); return; }
    const char *encoded=d["data"] | "";
    size_t n=0;
    if (mbedtls_base64_decode(buffer,sizeof(buffer),&n,reinterpret_cast<const uint8_t *>(encoded),strlen(encoded))
        || !n || n>expected-received) { send(400,"invalid_chunk"); return; }
    if (esp_ota_write(handle,buffer,n)!=ESP_OK) { fail("ota_write_failed"); send(500); return; }
    image.add(buffer,n); mbedtls_sha256_update_ret(&hash,buffer,n); received+=n;
    send(200);
}
void FirmwareUpdate::commit() {
    DynamicJsonDocument d(256); if (!body(d,192) || !session(d)) return;
    if (received!=expected) { send(409,"incomplete_image"); return; }
    uint8_t digest[32]; char hex[65];
    mbedtls_sha256_finish_ret(&hash,digest); mbedtls_sha256_free(&hash); hashing=false;
    for (unsigned i=0;i<32;++i) snprintf(hex+i*2,3,"%02x",digest[i]);
    if (expectedHash!=hex || !image.valid()) { fail(expectedHash!=hex?"sha256_mismatch":"wrong_firmware_image"); send(400); return; }
    esp_err_t result=esp_ota_end(handle); active=false;
    if (result!=ESP_OK) { fail("image_validation_failed"); send(400); return; }
    if (esp_ota_set_boot_partition(target)!=ESP_OK) { fail("boot_selection_failed"); send(500); return; }
    phase="restarting"; id=""; rebootAt=millis()+700; send(200);
}
void FirmwareUpdate::abort() {
    DynamicJsonDocument d(256); if (!body(d,192) || !session(d)) return;
    fail("update_aborted"); send(200);
}
void FirmwareUpdate::begin() {
    server.on("/api/v1/firmware",HTTP_GET,[this]{ if(auth()) send(200); });
    server.on("/api/v1/firmware/begin",HTTP_POST,[this]{prepare();});
    server.on("/api/v1/firmware/chunk",HTTP_POST,[this]{chunk();});
    server.on("/api/v1/firmware/commit",HTTP_POST,[this]{commit();});
    server.on("/api/v1/firmware/abort",HTTP_POST,[this]{abort();});
}
void FirmwareUpdate::tick() {
    if (active && uint32_t(millis()-touched)>30000) fail("update_timeout");
    if (rebootAt && int32_t(millis()-rebootAt)>=0) ESP.restart();
}
