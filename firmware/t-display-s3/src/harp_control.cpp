#include "harp_control.h"
#include <HTTPClient.h>
#include <WiFi.h>
#include <Preferences.h>
#include <atomic>

namespace HarpControl {
namespace {
struct Request { Action action; char file[192] = {}; int value = 0; };
SemaphoreHandle_t mutex = nullptr;
QueueHandle_t queue = nullptr;
std::atomic_bool active{false};
State shared;
String url, key;
String commandError;
uint32_t errorUntil = 0;
void updateProgress(const HarpUpdate::View &view) {
    xSemaphoreTake(mutex,portMAX_DELAY); shared.firmware=view; xSemaphoreGive(mutex);
}
bool valid(const String &u, const String &k) {
    if (!u.startsWith("http://") || u.length() < 8 || u.length() > 192 || k.length() < 32 || k.length() > 64) return false;
    for (unsigned i=7;i<u.length();++i) if (u[i]<=' ' || u[i]=='@' || u[i]=='?' || u[i]=='#') return false;
    for (unsigned i=0;i<k.length();++i) if (!isAlphaNumeric(k[i])) return false;
    return true;
}
bool http(const char *path, JsonDocument &doc, const String *body = nullptr) {
    HTTPClient h; WiFiClient client;
    h.setConnectTimeout(800); h.setTimeout(1200); h.useHTTP10(true);
    h.setFollowRedirects(HTTPC_DISABLE_FOLLOW_REDIRECTS);
    if (!h.begin(client,url+path)) return false;
    h.addHeader("Authorization","Bearer "+key);
    int code;
    if (body) { h.addHeader("Content-Type","application/json"); code=h.POST(*body); }
    else code=h.GET();
    bool parsed = code > 0 && !deserializeJson(doc,h.getStream());
    bool ok = code>=200 && code<300 && parsed && (doc["ok"] | false);
    if (!ok) {
        xSemaphoreTake(mutex,portMAX_DELAY);
        shared.online=code>0 && code!=401;
        shared.error=code==401?"播放器配对密钥错误":code==409?"请先在电脑弹出 SD 卡":code<=0?"播放器未连接":String("播放器 HTTP ")+code;
        if (body) { commandError=shared.error; errorUntil=millis()+5000; }
        xSemaphoreGive(mutex);
    }
    h.end(); return ok;
}
void poll(bool songs) {
    DynamicJsonDocument d(4096);
    if (!http("/api/v1/status",d)) return;
    if (!d["state"].is<const char *>() || !d["storage"].is<const char *>()) return;
    xSemaphoreTake(mutex,portMAX_DELAY);
    shared.online=true; shared.state=d["state"].as<String>(); shared.storage=d["storage"].as<String>();
    shared.file=d["file"].as<String>();
    shared.error=int32_t(errorUntil-millis())>0 ? commandError : d["error"].as<String>();
    shared.busy=d["busy"] | false; shared.loop=d["loop"] | false;
    shared.speed=d["speed"] | 100; shared.elapsedMs=d["elapsedMs"] | 0u;
    shared.durationMs=d["durationMs"] | 0u; shared.countdownMs=d["countdownMs"] | 0u;
    int offset=shared.offset;
    xSemaphoreGive(mutex);
    if (!songs) return;
    DynamicJsonDocument list(20000);
    String path="/api/v1/songs?offset="+String(offset);
    if (!http(path.c_str(),list) || !list["songs"].is<JsonArray>() || list["songs"].size()>32) return;
    std::vector<Song> rows;
    for (auto item : list["songs"].as<JsonArray>()) {
        if (!item["path"].is<const char *>() || !item["name"].is<const char *>()) return;
        rows.push_back({item["path"].as<String>(),item["name"].as<String>()});
    }
    xSemaphoreTake(mutex,portMAX_DELAY);
    shared.songs=std::move(rows); shared.total=list["total"] | 0;
    xSemaphoreGive(mutex);
}
void worker(void *) {
    uint32_t last=0, libraryAt=0;
    for (;;) {
        Request r; bool received=xQueueReceive(queue,&r,pdMS_TO_TICKS(50))==pdTRUE;
        if ((active.load() || received) && !url.isEmpty()) {
            if (WiFi.status()!=WL_CONNECTED) {
                xSemaphoreTake(mutex,portMAX_DELAY); shared.online=false; shared.error="Wi-Fi 未连接";
                if(received && (r.action==Action::CheckFirmware || r.action==Action::InstallFirmware)) {
                    shared.firmware.busy=false; shared.firmware.ready=false; shared.firmware.detail="Wi-Fi 未连接,请重新检查";
                }
                xSemaphoreGive(mutex);
            } else {
                if (received) {
                    DynamicJsonDocument body(512), reply(2048);
                    const char *path=nullptr;
                    switch (r.action) {
                    case Action::CheckFirmware: HarpUpdate::check(url,key,updateProgress); break;
                    case Action::InstallFirmware: HarpUpdate::install(url,key,updateProgress); break;
                    case Action::Play: path="/api/v1/play"; body["file"]=r.file; body["countdown"]=3; break;
                    case Action::Pause: path="/api/v1/pause"; break;
                    case Action::Resume: path="/api/v1/resume"; break;
                    case Action::Stop: path="/api/v1/stop"; break;
                    case Action::Usb: path="/api/v1/storage/usb"; break;
                    case Action::Refresh: path="/api/v1/library/refresh"; break;
                    case Action::Speed: path="/api/v1/settings"; body["speed"]=r.value; break;
                    case Action::Loop: path="/api/v1/settings"; body["loop"]=r.value!=0; break;
                    case Action::Songs:
                        xSemaphoreTake(mutex,portMAX_DELAY); shared.offset=r.value; xSemaphoreGive(mutex); break;
                    }
                    if (path) {
                        errorUntil=0; commandError="";
                        String text; serializeJson(body,text); http(path,reply,&text);
                    }
                }
                uint32_t now=millis();
                if (received || now-last>=700) {
                    bool fetch=received || now-libraryAt>=2500;
                    poll(fetch); last=now; if (fetch) libraryAt=now;
                }
            }
        }
        if (received) { xSemaphoreTake(mutex,portMAX_DELAY); shared.pending=false; xSemaphoreGive(mutex); }
    }
}
}
void begin() {
    mutex=xSemaphoreCreateMutex(); queue=xQueueCreate(1,sizeof(Request));
    Preferences p;
    if (p.begin("delta-harp-ui",true)) {
        DynamicJsonDocument d(1024); deserializeJson(d,p.getString("config","{}")); p.end();
        url=d["url"].as<String>(); key=d["key"].as<String>();
    }
    shared.configured=valid(url,key);
    if (!shared.configured) { url=""; shared.error="通过 USB 配对播放器"; }
    if (!mutex || !queue || xTaskCreate(worker,"harp-http",12288,nullptr,1,nullptr)!=pdPASS)
        shared.error="播放器网络任务启动失败";
}
void visible(bool value) { active.store(value); }
State snapshot() {
    if (!mutex) return shared;
    xSemaphoreTake(mutex,portMAX_DELAY); auto copy=shared; xSemaphoreGive(mutex); return copy;
}
bool configured() { return shared.configured; }
bool submit(Action action,const String &file,int value) {
    if (!mutex || !queue || file.length()>191) return false;
    xSemaphoreTake(mutex,portMAX_DELAY);
    bool ok=shared.configured && !shared.pending;
    if (action==Action::InstallFirmware && !shared.firmware.ready) ok=false;
    if (ok) {
        shared.pending=true;
        if(action==Action::CheckFirmware || action==Action::InstallFirmware) {
            shared.firmware.busy=true; shared.firmware.percent=0;
            shared.firmware.detail="正在处理...";
        }
    }
    xSemaphoreGive(mutex);
    if (!ok) return false;
    Request r; r.action=action; r.value=value; strlcpy(r.file,file.c_str(),sizeof(r.file));
    if (xQueueSend(queue,&r,0)==pdTRUE) return true;
    xSemaphoreTake(mutex,portMAX_DELAY); shared.pending=false; shared.firmware.busy=false; xSemaphoreGive(mutex); return false;
}
bool configure(JsonDocument &doc,String &error) {
    String u=doc["playerUrl"] | "", k=doc["playerKey"] | "";
    while (u.endsWith("/")) u.remove(u.length()-1);
    if (!valid(u,k)) { error="invalid_player_url_or_key"; return false; }
    DynamicJsonDocument saved(1024); saved["url"]=u; saved["key"]=k;
    String value; serializeJson(saved,value); Preferences p;
    bool ok=p.begin("delta-harp-ui",false);
    if (ok) ok=p.putString("config",value)==value.length(); p.end();
    if (!ok) error="player_config_save_failed";
    return ok;
}
}
