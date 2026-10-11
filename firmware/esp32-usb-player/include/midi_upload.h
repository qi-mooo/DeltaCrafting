#pragma once
#include "player.h"
#include <WebServer.h>
#include <ArduinoJson.h>
#include <functional>

class MidiUpload {
public:
    MidiUpload(WebServer &s,Player &p,UploadLibrary &l,std::function<bool()> auth)
        : server(s),player(p),library(l),authorized(auth) {}
    void begin();
    void tick();
    bool busy() const { return active; }
private:
    WebServer &server;
    Player &player;
    UploadLibrary &library;
    std::function<bool()> authorized;
    fs::File output;
    bool active=false;
    uint32_t expected=0,received=0,touched=0;
    String id,path;
    uint8_t buffer[3072];
    bool body(JsonDocument &doc,size_t limit=768);
    bool session(JsonDocument &doc);
    void reply(int code,const char *error=nullptr);
    void cleanup();
    void prepare();
    void chunk();
    void finish();
    void remove();
};
