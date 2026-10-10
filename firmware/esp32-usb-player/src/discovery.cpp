#include "discovery.h"
#include "firmware_identity.h"
#include <ArduinoJson.h>
#include <WiFi.h>
#include <Preferences.h>
#include <esp_system.h>

namespace {
constexpr uint16_t Port=40110;
bool local(IPAddress remote) {
    auto same=[remote](IPAddress address,IPAddress mask) {
        if(uint32_t(address)==0) return false;
        for(int i=0;i<4;++i) if((remote[i]&mask[i])!=(address[i]&mask[i])) return false;
        return true;
    };
    return same(WiFi.localIP(),WiFi.subnetMask()) || same(WiFi.softAPIP(),IPAddress(255,255,255,0));
}
bool nonceValid(const String &s) {
    if(s.length()!=32) return false;
    for(unsigned i=0;i<s.length();++i) if(!isHexadecimalDigit(s[i])) return false;
    return true;
}
}
bool HarpDiscovery::pairable() const { return pairUntil && int32_t(pairUntil-millis())>0; }
uint32_t HarpDiscovery::pairingSeconds() const {
    int32_t remaining=int32_t(pairUntil-millis());
    return pairUntil && remaining>0 ? (uint32_t(remaining)+999)/1000 : 0;
}
void HarpDiscovery::openPairing() {
    char value[33]; snprintf(value,sizeof(value),"%08x%08x%08x%08x",esp_random(),esp_random(),esp_random(),esp_random());
    challenge=value; pairUntil=millis()+60000;
}
void HarpDiscovery::begin(WebServer &server,const String &id,const String &key) {
    deviceId=id; apiKey=key; pinMode(0,INPUT_PULLUP); udp.begin(Port);
    Preferences p;
    if(p.begin("delta-harp",true)) { bool paired=p.getBool("net-paired",false); p.end(); if(!paired) openPairing(); }
    server.on("/api/v1/pair",HTTP_POST,[this,&server] {
        DynamicJsonDocument d(512);
        bool valid=local(server.client().remoteIP()) && pairable() && server.arg("plain").length()<=256
            && !deserializeJson(d,server.arg("plain")) && String(d["challenge"] | "")==challenge
            && String(d["deviceId"] | "")==deviceId;
        server.sendHeader("Cache-Control","no-store");
        if(!valid) { server.send(403,"application/json","{\"ok\":false,\"error\":\"press_boot_to_pair\"}"); return; }
        Preferences p; bool saved=p.begin("delta-harp",false);
        if(saved) saved=p.putBool("net-paired",true)>0; p.end();
        if(!saved) { server.send(500,"application/json","{\"ok\":false,\"error\":\"save_failed\"}"); return; }
        d.clear(); d["ok"]=true; d["deviceId"]=deviceId; d["apiKey"]=apiKey;
        String reply; serializeJson(d,reply); server.send(200,"application/json",reply);
    });
}
void HarpDiscovery::tick() {
    uint32_t now=millis();
    if(digitalRead(0)==LOW) {
        if(!held) { held=true; opened=false; pressedAt=now; }
        if(!opened && now-pressedAt>=1000) { openPairing(); opened=true; }
    } else held=false;
    int size=udp.parsePacket(); if(!size) return;
    if(size>384 || !local(udp.remoteIP()) || now-lastReply<100) { udp.flush(); return; }
    char bytes[385]; int n=udp.read(bytes,sizeof(bytes)-1); if(n<=0) return; bytes[n]=0;
    DynamicJsonDocument d(768);
    if(deserializeJson(d,bytes) || String(d["type"] | "")!="delta-harp-discover" || (d["protocol"] | 0)!=1) return;
    String nonce=d["nonce"] | ""; if(!nonceValid(nonce)) return;
    d.clear(); d["type"]="delta-harp-device"; d["protocol"]=1; d["nonce"]=nonce;
    d["deviceId"]=deviceId; d["name"]="DeltaHarp-"+deviceId.substring(deviceId.length()-6);
    d["firmware"]=HARP_FIRMWARE_VERSION; d["board"]=HARP_BOARD_ID; d["port"]=80;
    d["pairable"]=pairable(); if(pairable()) d["challenge"]=challenge;
    String reply; serializeJson(d,reply); lastReply=now;
    udp.beginPacket(udp.remoteIP(),udp.remotePort()); udp.write(reinterpret_cast<const uint8_t *>(reply.c_str()),reply.length()); udp.endPacket();
}
