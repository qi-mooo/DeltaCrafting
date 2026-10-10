#include "harp_update.h"
#include "device_config.h"
#include <HTTPClient.h>
#include <ArduinoJson.h>
#include <esp_system.h>

namespace HarpUpdate {
namespace {
View view;
String checkId;
uint32_t lastPoll=0;
String normalized(String s) { while(s.endsWith("/")) s.remove(s.length()-1); return s; }
int request(JsonDocument &reply,const String *body=nullptr) {
    WiFiClient client; HTTPClient http;
    http.setConnectTimeout(1200); http.setTimeout(2500); http.useHTTP10(true); http.setReuse(false);
    http.setFollowRedirects(HTTPC_DISABLE_FOLLOW_REDIRECTS);
    String path=body?"/api/v1/harp/update/action":"/api/v1/harp/update";
    if(!http.begin(client,normalized(DeviceConfig::baseUrl())+path)) return -1;
    http.addHeader("Authorization","Bearer "+DeviceConfig::apiKey());
    int code;
    if(body) { http.addHeader("Content-Type","application/json"); code=http.POST(*body); }
    else code=http.GET();
    if(code>0 && deserializeJson(reply,http.getStream())) code=-1;
    http.end(); return code;
}
String error(int code) {
    if(code==401) return "Windows App 配对密钥错误";
    if(code==403) return "Windows App 未允许设备控制";
    if(code==404) return "请更新 Windows App";
    return "客户端未连接,请重新检查进度";
}
bool accept(JsonDocument &d,const String &url,const String &id) {
    if(!d["busy"].is<bool>() || !d["ready"].is<bool>()) return false;
    String target=d["playerUrl"] | "";
    view.current=d["current"] | ""; view.version=d["version"] | "";
    view.detail=d["detail"] | ""; view.busy=d["busy"] | false;
    view.ready=d["ready"] | false; view.percent=constrain(d["percent"] | 0,0,100);
    checkId=d["checkId"] | "";
    if(id.isEmpty()?normalized(target)!=normalized(url):String(d["playerId"] | "")!=id) {
        view.ready=false; view.busy=false;
        view.detail="请在 App 扫描连接同一 Harp";
    }
    return true;
}
void action(const String &url,const String &deviceId,bool install,Progress progress) {
    if(!DeviceConfig::valid()) { view={}; view.detail="请先配对 Windows App"; progress(view); return; }
    if(install && (!view.ready || checkId.isEmpty())) return;
    char id[33]; snprintf(id,sizeof(id),"%08x%08x%08x%08x",esp_random(),esp_random(),esp_random(),esp_random());
    DynamicJsonDocument body(768),reply(2048);
    body["action"]=install?"install":"check"; body["requestId"]=id;
    body["checkId"]=checkId; body["playerUrl"]=url;
    body["playerId"]=deviceId;
    String text; serializeJson(body,text);
    view.busy=true; view.ready=false; view.detail="正在请求 Windows App"; progress(view);
    int code=request(reply,&text);
    // An ambiguous POST is resolved by reading the desktop job, never by repeating install.
    if(code<=0) { reply.clear(); code=request(reply); }
    if((code==200 || code==202 || code==409) && accept(reply,url,deviceId)) { }
    else { view.busy=false; view.ready=false; view.detail=error(code); }
    lastPoll=millis(); progress(view);
}
}
void check(const String &url,const String &id,Progress progress) {
    if(DeviceConfig::valid()) {
        DynamicJsonDocument reply(2048);
        if(request(reply)==200 && (reply["busy"] | false) && accept(reply,url,id)) { progress(view); return; }
    }
    action(url,id,false,progress);
}
void install(const String &url,const String &id,Progress progress) { action(url,id,true,progress); }
void poll(const String &url,const String &id,Progress progress) {
    if(!view.busy || millis()-lastPoll<1000) return;
    lastPoll=millis(); DynamicJsonDocument reply(2048);
    int code=request(reply);
    if(code!=200 || !accept(reply,url,id)) {
        view.busy=false; view.ready=false;
        view.detail="进度暂不可用,App 可能仍在更新";
    }
    progress(view);
}
}
