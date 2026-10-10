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
String url, key, deviceId;
String commandError;
uint32_t errorUntil = 0;
String readableError(const String &error) {
    if(error=="sd_busy_retry" || error=="sd_changed_retry") return "SD 正在写入,稍后重试";
    if(error=="eject_sd_on_computer_first") return "旧版播放器需先弹出 SD 卡";
    if(error=="sd_not_available") return "未检测到 SD 卡";
    if(error=="update_in_progress") return "播放器正在更新";
    if(error=="usb_host_not_connected") return "播放器 USB 未连接电脑";
    return error;
}
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
        String reason=readableError(doc["error"] | "");
        shared.error=code==401?"播放器配对密钥错误":code<=0?"播放器未连接":!reason.isEmpty()?reason:String("播放器 HTTP ")+code;
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
    shared.error=int32_t(errorUntil-millis())>0 ? commandError : readableError(d["error"] | "");
    shared.sharedLibrary=d["sharedLibrary"] | false;
    shared.busy=d["busy"] | false; shared.loop=d["loop"] | false;
    shared.speed=d["speed"] | 100; shared.elapsedMs=d["elapsedMs"] | 0u;
    shared.durationMs=d["durationMs"] | 0u; shared.countdownMs=d["countdownMs"] | 0u;
    int offset=shared.offset;
    xSemaphoreGive(mutex);
    if (!songs) return;
    DynamicJsonDocument list(20000);
    String path="/api/v1/favorites?offset="+String(offset);
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
bool savePair(const String &u,const String &k,const String &id) {
    DynamicJsonDocument saved(1024); saved["url"]=u; saved["key"]=k; saved["deviceId"]=id;
    String value; serializeJson(saved,value); Preferences p;
    bool ok=p.begin("delta-harp-ui",false);
    if(ok) ok=p.putString("config",value)==value.length(); p.end(); return ok;
}
void scanPlayers() {
    auto devices=HarpNetwork::scan();
    for(const auto &d:devices) if(!deviceId.isEmpty() && d.id==deviceId && d.url!=url) {
        if(savePair(d.url,key,deviceId)) url=d.url;
    }
    xSemaphoreTake(mutex,portMAX_DELAY); shared.devices=std::move(devices);
    shared.discoveryDetail=shared.devices.empty()?"未发现 Harp,请检查局域网":"选择设备连接;首次配对按住 BOOT";
    xSemaphoreGive(mutex);
}
void connectPlayer(int index) {
    xSemaphoreTake(mutex,portMAX_DELAY);
    bool found=index>=0 && index<int(shared.devices.size());
    HarpNetwork::Device d; if(found) d=shared.devices[index];
    xSemaphoreGive(mutex); if(!found) return;
    String nextKey,message;
    bool ok=!d.pairable && d.id==deviceId && valid(url,key);
    if(ok) nextKey=key; else ok=HarpNetwork::pair(d,nextKey,message);
    if(ok) { ok=savePair(d.url,nextKey,d.id); if(!ok) message="配对保存失败"; }
    if(ok) { url=d.url; key=nextKey; deviceId=d.id; }
    xSemaphoreTake(mutex,portMAX_DELAY);
    if(ok) { shared.configured=true; shared.songs.clear(); shared.offset=0; shared.total=0; shared.error=""; }
    shared.discoveryDetail=ok?"已连接 "+d.name:message;
    xSemaphoreGive(mutex);
}
void worker(void *) {
    uint32_t last=0, libraryAt=0,discoveryAt=0;
    for (;;) {
        Request r; bool received=xQueueReceive(queue,&r,pdMS_TO_TICKS(50))==pdTRUE;
        if (active.load() || received) {
            if (WiFi.status()!=WL_CONNECTED) {
                xSemaphoreTake(mutex,portMAX_DELAY); shared.online=false; shared.error="Wi-Fi 未连接";
                if(received && (r.action==Action::Scan || r.action==Action::Connect)) shared.discoveryDetail="Wi-Fi 未连接";
                if(received && (r.action==Action::CheckFirmware || r.action==Action::InstallFirmware)) {
                    shared.firmware.busy=false; shared.firmware.ready=false; shared.firmware.detail="Wi-Fi 未连接,请重新检查";
                }
                xSemaphoreGive(mutex);
            } else {
                bool networkAction=received && (r.action==Action::Scan || r.action==Action::Connect);
                if(received && r.action==Action::Scan) { scanPlayers(); discoveryAt=millis(); }
                if(received && r.action==Action::Connect) connectPlayer(r.value);
                if(!received && !deviceId.isEmpty() && millis()-discoveryAt>15000) {
                    bool online; xSemaphoreTake(mutex,portMAX_DELAY); online=shared.online; xSemaphoreGive(mutex);
                    if(!online) scanPlayers(); discoveryAt=millis();
                }
                if(url.isEmpty() || networkAction) {
                    if(received) { xSemaphoreTake(mutex,portMAX_DELAY); shared.pending=false; xSemaphoreGive(mutex); }
                    continue;
                }
                if (received) {
                    DynamicJsonDocument body(512), reply(2048);
                    const char *path=nullptr;
                    switch (r.action) {
                    case Action::CheckFirmware: HarpUpdate::check(url,deviceId,updateProgress); break;
                    case Action::InstallFirmware: HarpUpdate::install(url,deviceId,updateProgress); break;
                    case Action::Scan: case Action::Connect: break;
                    case Action::Play: path="/api/v1/play"; body["file"]=r.file; break;
                    case Action::Pause: path="/api/v1/pause"; break;
                    case Action::Resume: path="/api/v1/resume"; break;
                    case Action::Stop: path="/api/v1/stop"; break;
                    case Action::Usb: path="/api/v1/storage/usb"; break;
                    case Action::Refresh: path="/api/v1/library/refresh"; break;
                    case Action::Speed: path="/api/v1/settings"; body["speed"]=r.value; break;
                    case Action::Loop: path="/api/v1/settings"; body["loop"]=r.value!=0; break;
                    case Action::Songs:
                        xSemaphoreTake(mutex,portMAX_DELAY);
                        shared.offset=r.value;
                        xSemaphoreGive(mutex); break;
                    }
                    if (path) {
                        errorUntil=0; commandError="";
                        String text; serializeJson(body,text); http(path,reply,&text);
                    }
                }
                uint32_t now=millis();
                HarpUpdate::poll(url,deviceId,updateProgress);
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
        deviceId=d["deviceId"] | "";
    }
    shared.configured=valid(url,key);
    if (!shared.configured) { url=""; shared.error="请扫描并连接播放器"; }
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
    bool ok=(shared.configured || action==Action::Scan || action==Action::Connect) && !shared.pending;
    if (action==Action::InstallFirmware && !shared.firmware.ready) ok=false;
    if (ok) {
        shared.pending=true;
        if(action==Action::Scan) shared.discoveryDetail="正在广播扫描...";
        if(action==Action::Connect) shared.discoveryDetail="正在连接播放器...";
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
