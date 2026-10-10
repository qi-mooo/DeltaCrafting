#pragma once
#include <WebServer.h>
#include <ArduinoJson.h>
#include <esp_ota_ops.h>
#include <mbedtls/sha256.h>
#include "player.h"
#include "firmware_image.h"

class FirmwareUpdate {
public:
    FirmwareUpdate(WebServer &web, Player &p, Storage &s, const String &key)
        : server(web), player(p), storage(s), apiKey(key) {}
    void begin();
    void tick();
    void status(JsonObject doc);
    bool busy() const { return active || rebootAt; }
private:
    WebServer &server;
    Player &player;
    Storage &storage;
    const String &apiKey;
    esp_ota_handle_t handle=0;
    const esp_partition_t *target=nullptr;
    mbedtls_sha256_context hash{};
    FirmwareImageCheck image;
    bool active=false, hashing=false;
    uint32_t expected=0, received=0, touched=0, rebootAt=0;
    String expectedHash, id, phase="idle", failure;
    uint8_t buffer[4096];
    bool auth();
    bool body(JsonDocument &doc, size_t limit);
    bool session(JsonDocument &doc);
    void send(int code, const char *error=nullptr);
    void fail(const char *error);
    void prepare();
    void chunk();
    void commit();
    void abort();
};
