#include "harp_discovery.h"
#include <WiFi.h>
#include <WiFiUdp.h>
#include <HTTPClient.h>
#include <ArduinoJson.h>
#include <esp_system.h>

namespace HarpNetwork {
namespace {
bool hex(const String &s,size_t n) {
    if(s.length()!=n) return false;
    for(unsigned i=0;i<n;++i) if(!isHexadecimalDigit(s[i])) return false;
    return true;
}
}
std::vector<Device> scan() {
    std::vector<Device> devices;
    WiFiUDP udp; if(!udp.begin(uint16_t(45000+esp_random()%15000))) return devices;
    IPAddress ip=WiFi.localIP(),mask=WiFi.subnetMask(),broadcast;
    for(int i=0;i<4;++i) broadcast[i]=ip[i]|~mask[i];
    char nonce[33]; snprintf(nonce,sizeof(nonce),"%08x%08x%08x%08x",esp_random(),esp_random(),esp_random(),esp_random());
    DynamicJsonDocument d(1024); d["type"]="delta-harp-discover"; d["protocol"]=1; d["nonce"]=nonce;
    String query; serializeJson(d,query);
    for(unsigned round=0;round<3;++round) {
        udp.beginPacket(broadcast,40110); udp.write(reinterpret_cast<const uint8_t *>(query.c_str()),query.length()); udp.endPacket();
        uint32_t started=millis();
        while(millis()-started<900) {
            int size=udp.parsePacket(); if(!size) { delay(10); continue; }
            IPAddress remote=udp.remoteIP(); bool local=true;
            for(int i=0;i<4;++i) local=local && (remote[i]&mask[i])==(ip[i]&mask[i]);
            if(size>768 || udp.remotePort()!=40110 || !local) { udp.flush(); continue; }
            char bytes[769]; int n=udp.read(bytes,sizeof(bytes)-1); if(n<=0) continue; bytes[n]=0;
            d.clear(); if(deserializeJson(d,bytes)) continue;
            if(String(d["type"] | "")!="delta-harp-device" || (d["protocol"] | 0)!=1
                || String(d["nonce"] | "")!=nonce || String(d["board"] | "")!="esp32-s3-dongle-fn8" || (d["port"] | 0)!=80) continue;
            Device device; device.id=d["deviceId"] | ""; device.name=d["name"] | "";
            device.url="http://"+remote.toString(); device.version=d["firmware"] | "";
            device.pairable=d["pairable"] | false; device.challenge=d["challenge"] | "";
            if(!hex(device.id,12) || device.name.isEmpty() || device.name.length()>48
                || (device.pairable && !hex(device.challenge,32))) continue;
            bool exists=false;
            for(auto &row:devices) if(row.id==device.id) { row=device; exists=true; break; }
            if(!exists && devices.size()<16) devices.push_back(device);
        }
    }
    udp.stop(); return devices;
}
bool pair(const Device &device,String &key,String &error) {
    if(!device.pairable) { error="按住播放器 BOOT 一秒后重新扫描"; return false; }
    WiFiClient client; HTTPClient h; h.useHTTP10(true); h.setConnectTimeout(1200); h.setTimeout(2500);
    h.setFollowRedirects(HTTPC_DISABLE_FOLLOW_REDIRECTS);
    if(!h.begin(client,device.url+"/api/v1/pair")) { error="播放器连接失败"; return false; }
    h.addHeader("Content-Type","application/json");
    DynamicJsonDocument d(768); d["deviceId"]=device.id; d["challenge"]=device.challenge;
    String text; serializeJson(d,text); int code=h.POST(text); d.clear();
    bool parsed=code==200 && !deserializeJson(d,h.getStream()); h.end();
    key=d["apiKey"] | "";
    bool valid=parsed && (d["ok"] | false) && String(d["deviceId"] | "")==device.id && key.length()>=32 && key.length()<=64;
    for(unsigned i=0;i<key.length();++i) valid=valid && isAlphaNumeric(key[i]);
    if(!valid) { key=""; error=code==403?"按住 BOOT 后重新扫描配对":"播放器配对失败"; }
    return valid;
}
}
