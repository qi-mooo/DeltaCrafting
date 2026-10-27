#pragma once
#include <Arduino.h>
#include <ArduinoJson.h>
#include <WebServer.h>
#include <WiFiUdp.h>

class HarpDiscovery {
public:
    void begin(WebServer &server,const String &id,const String &key);
    void tick();
    void openPairing();
    void closePairing() { pairUntil=0; challenge=""; }
    uint32_t pairingSeconds() const;
private:
    WiFiUDP udp;
    String deviceId,apiKey,challenge;
    uint32_t pairUntil=0,pressedAt=0,lastReply=0;
    bool held=false,opened=false;
    bool pairable() const;
};
