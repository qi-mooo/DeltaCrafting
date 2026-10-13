#pragma once
#include <WebServer.h>
#include "player.h"

// Bounded read-only download for browser synthesis. No HID play command.
class MidiPreview {
public:
    MidiPreview(WebServer &w,Player &p,Storage &s,bool (*auth)())
        : server(w),player(p),storage(s),authorized(auth) {}
    void begin();
private:
    WebServer &server;
    Player &player;
    Storage &storage;
    bool (*authorized)();
    uint8_t buffer[4096];
    void download();
    void error(int code,const char *message);
};
