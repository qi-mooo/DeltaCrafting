#include "harp_update.h"
#include "harp_update_protocol.h"
#include "device_config.h"
#include <HTTPClient.h>
#include <ArduinoJson.h>
#include <mbedtls/base64.h>
#include <mbedtls/sha256.h>
#include <memory>

namespace HarpUpdate {
namespace {
constexpr const char *Board="esp32-s3-dongle-fn8", *Layout="dual-8mb-v1";
struct Manifest { String id,version,sha; uint32_t size=0,rev=0; } checked;
View view;
bool hashValid(const String &s) {
    if(s.length()!=64) return false;
    for(unsigned i=0;i<s.length();++i) if(!((s[i]>='0' && s[i]<='9') || (s[i]>='a' && s[i]<='f'))) return false;
    return true;
}
void open(HTTPClient &h,WiFiClient &c,const String &url,const String &key,const String &path) {
    h.setConnectTimeout(2000); h.setTimeout(8000); h.useHTTP10(true); h.setReuse(false);
    h.setFollowRedirects(HTTPC_DISABLE_FOLLOW_REDIRECTS);
    String base=url; while(base.endsWith("/")) base.remove(base.length()-1);
    h.begin(c,base+path); h.addHeader("Authorization","Bearer "+key);
}
int request(const String &url,const String &key,const char *path,JsonDocument &reply,const String *body=nullptr) {
    WiFiClient c; HTTPClient h; open(h,c,url,key,path);
    int code;
    if(body) { h.addHeader("Content-Type","application/json"); code=h.POST(*body); }
    else code=h.GET();
    if(code>0 && deserializeJson(reply,h.getStream())) code=-1;
    h.end(); return code;
}
String httpError(int code,bool desktop) {
    if(code==401) return desktop?"Windows App 配对密钥错误":"播放器配对密钥错误";
    if(code==403) return "Windows App 未允许设备控制";
    if(code==404) return desktop?"请更新 Windows App 或导入 Harp 固件":"播放器需先用 USB 安装 v2";
    return desktop?"无法连接 Windows App":"无法连接播放器";
}
bool compatible(JsonDocument &d,const Manifest &m) {
    uint32_t current=revision(d["version"] | "");
    return current>=2 && m.rev>=current && String(d["board"] | "")==Board
        && String(d["layout"] | "")==Layout && (d["maxBytes"] | 0u)>=m.size && (d["chunkBytes"] | 0u)>=4096;
}
bool manifest(JsonDocument &d,Manifest &m) {
    if((d["schemaVersion"] | 0)!=1 || String(d["board"] | "")!=Board || String(d["layout"] | "")!=Layout
        || (d["minimumUpdater"] | 0)!=1 || !d["images"].is<JsonArray>() || d["images"].size()!=1) return false;
    m.id=d["bundleId"] | ""; m.version=d["firmwareVersion"] | ""; m.rev=revision(m.version.c_str());
    auto im=d["images"][0]; m.size=im["size"] | 0u; m.sha=im["sha256"] | "";
    return m.rev>=2 && m.rev==(d["revision"] | 0u) && hashValid(m.id) && hashValid(m.sha)
        && m.size>=288 && m.size<=0x330000 && String(im["role"] | "")=="player"
        && String(im["file"] | "")=="firmware.bin" && (im["address"] | 0u)==0x10000;
}
struct Relay {
    const String &url,&key;
    Progress notify;
    String session,partition;
    WiFiClient client;
    HTTPClient download;
    std::unique_ptr<uint8_t[]> bytes{new uint8_t[4096]};
    std::unique_ptr<unsigned char[]> encoded{new unsigned char[5465]};
    mbedtls_sha256_context digest;
    Relay(const String &u,const String &k,Progress p):url(u),key(k),notify(p) {
        mbedtls_sha256_init(&digest); mbedtls_sha256_starts_ret(&digest,0);
    }
    ~Relay() { download.end(); mbedtls_sha256_free(&digest); }
    int post(const char *path,JsonDocument &body,JsonDocument &reply) {
        String text; serializeJson(body,text); return request(url,key,path,reply,&text);
    }
    bool start() {
        DynamicJsonDocument reply(2048),body(768);
        int code=request(url,key,"/api/v1/firmware",reply);
        if(code!=200 || !compatible(reply,checked)) { view.detail="播放器版本或连接已变化,请重新检查"; return false; }
        partition=reply["partition"] | "";
        if(partition!="app0" && partition!="app1") { view.detail="播放器分区不兼容"; return false; }
        open(download,client,DeviceConfig::baseUrl(),DeviceConfig::apiKey(),
            "/api/v1/harp/firmware-image?bundle="+checked.id+"&role=player");
        code=download.GET();
        if(code!=200 || download.getSize()!=int(checked.size)) { view.detail="Windows App 固件下载失败"; return false; }
        body["board"]=Board; body["layout"]=Layout; body["size"]=checked.size; body["sha256"]=checked.sha;
        reply.clear(); code=post("/api/v1/firmware/begin",body,reply);
        if(code!=200 || !(reply["ok"] | false)) {
            String error=reply["error"] | "";
            view.detail=error=="eject_sd_on_computer_first"?"请先在电脑弹出 SD 卡":code<=0?"更新启动结果未知,30秒后重试":"播放器拒绝更新: "+error;
            return false;
        }
        session=reply["updateId"] | ""; return !session.isEmpty();
    }
    bool read(size_t n) {
        size_t done=0; uint32_t last=millis(); auto stream=download.getStreamPtr();
        while(done<n) {
            int available=stream->available();
            if(available>0) {
                int got=stream->read(bytes.get()+done,min(n-done,size_t(available)));
                if(got>0) { done+=got; last=millis(); }
            } else if(!download.connected() || millis()-last>10000) return false;
            delay(1);
        }
        mbedtls_sha256_update_ret(&digest,bytes.get(),n); return true;
    }
    bool send(uint32_t offset,size_t n) {
        size_t length=0; if(mbedtls_base64_encode(encoded.get(),5465,&length,bytes.get(),n)) return false;
        encoded[length]=0;
        DynamicJsonDocument body(6656),reply(2048);
        body["updateId"]=session; body["offset"]=offset; body["data"]=reinterpret_cast<char *>(encoded.get());
        int code=post("/api/v1/firmware/chunk",body,reply);
        return code==200 && (reply["ok"] | false) && String(reply["updateId"] | "")==session
            && (reply["received"] | UINT32_MAX)==offset+n;
    }
    int64_t received() {
        DynamicJsonDocument reply(2048);
        if(request(url,key,"/api/v1/firmware",reply)!=200 || String(reply["updateId"] | "")!=session) return -1;
        return reply["received"] | int64_t(-1);
    }
    void progress(uint32_t offset,uint32_t size) {
        int percent=uint64_t(offset)*100/size;
        if(view.percent!=percent) { view.percent=percent; view.detail="正在更新 USB 播放器"; notify(view); }
    }
    bool verifyHash() {
        uint8_t raw[32]; char hex[65]; mbedtls_sha256_finish_ret(&digest,raw);
        for(unsigned i=0;i<32;++i) snprintf(hex+i*2,3,"%02x",raw[i]);
        download.end(); return checked.sha==hex;
    }
    void abort() {
        if(session.isEmpty()) return;
        DynamicJsonDocument body(256),reply(2048); body["updateId"]=session;
        post("/api/v1/firmware/abort",body,reply);
    }
    int commit() {
        view.detail="校验完成,等待播放器重启"; notify(view);
        DynamicJsonDocument body(256),reply(2048); body["updateId"]=session;
        int code=post("/api/v1/firmware/commit",body,reply);
        return code<=0?-1:(code==200 && (reply["ok"] | false)?1:0);
    }
    bool verifyRestart() {
        uint32_t started=millis(); delay(1000);
        while(millis()-started<35000) {
            DynamicJsonDocument reply(2048);
            if(request(url,key,"/api/v1/firmware",reply)==200 && String(reply["phase"] | "")=="idle"
                && String(reply["version"] | "")==checked.version && String(reply["board"] | "")==Board
                && String(reply["layout"] | "")==Layout
                && String(reply["partition"] | "")==(partition=="app0"?"app1":"app0")) return true;
            delay(500);
        }
        return false;
    }
};
}
void check(const String &url,const String &key,Progress progress) {
    view={}; checked={}; view.busy=true; view.detail="正在检查 Harp 更新"; progress(view);
    do {
        if(!DeviceConfig::valid()) { view.detail="请先配对 Windows App"; break; }
        DynamicJsonDocument d(4096);
        int code=request(url,key,"/api/v1/firmware",d);
        if(code!=200) { view.detail=httpError(code,false); break; }
        view.current=d["version"] | "";
        DynamicJsonDocument source(4096);
        code=request(DeviceConfig::baseUrl(),DeviceConfig::apiKey(),"/api/v1/harp/firmware",source);
        if(code!=200) { view.detail=httpError(code,true); break; }
        Manifest next;
        if(!manifest(source,next) || !compatible(d,next)) { view.detail="固件不兼容或版本低于播放器"; break; }
        checked=next; view.version=next.version; view.ready=true;
        view.detail=view.current==next.version?"已是当前版本,可重新安装":"发现新版本,可安装";
    } while(false);
    view.busy=false; progress(view);
}
void install(const String &url,const String &key,Progress progress) {
    if(!view.ready || checked.id.isEmpty()) return;
    view.ready=false; view.busy=true; view.percent=0; view.detail="准备更新 USB 播放器"; progress(view);
    Relay transport(url,key,progress); Result result=relay(transport,checked.size);
    switch(result) {
    case Result::Complete: view.current=checked.version; view.detail="更新完成,播放器已重启"; break;
    case Result::BeginFailed: break;
    case Result::DownloadFailed: view.detail="下载中断,已取消更新"; break;
    case Result::UploadFailed: view.detail="上传中断,请重新检查后重试"; break;
    case Result::HashFailed: view.detail="固件校验失败,已取消更新"; break;
    case Result::CommitFailed: view.detail="播放器拒绝固件,请重新检查"; break;
    case Result::RebootUnknown: view.detail="重启结果未确认,请重新检查"; break;
    }
    view.busy=false; progress(view);
}
}
