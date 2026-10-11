#include "midi_upload.h"
#include <esp_system.h>
#include <mbedtls/base64.h>

namespace {
constexpr const char *Temp="/.upload.part";
struct Input : Harp::Reader {
    fs::File file;
    uint32_t size() const override { return file.size(); }
    bool read(uint32_t off,void *buffer,size_t n) override {
        return file.seek(off) && file.read(static_cast<uint8_t *>(buffer),n)==n;
    }
};
}
bool MidiUpload::body(JsonDocument &doc,size_t limit) {
    if(!authorized()) return false;
    String data=server.arg("plain");
    if(data.length()>limit || deserializeJson(doc,data) || !doc.is<JsonObject>()) {
        reply(400,"invalid_json"); return false;
    }
    return true;
}
bool MidiUpload::session(JsonDocument &doc) {
    if(!active || String(doc["uploadId"] | "")!=id) { reply(409,"invalid_upload_session"); return false; }
    touched=millis(); return true;
}
void MidiUpload::reply(int code,const char *error) {
    DynamicJsonDocument doc(768); doc["ok"]=code<300;
    if(error) doc["error"]=error;
    doc["received"]=received; doc["size"]=expected; doc["file"]=path;
    if(active) { doc["uploadId"]=id; doc["chunkBytes"]=3072; }
    String value; serializeJson(doc,value); server.sendHeader("Cache-Control","no-store");
    server.send(code,"application/json; charset=utf-8",value);
}
void MidiUpload::cleanup() {
    output.close(); LittleFS.remove(Temp); active=false; id=""; player.endMaintenance();
}
void MidiUpload::prepare() {
    DynamicJsonDocument d(1024); if(!body(d)) return;
    if(active) { reply(409,"upload_in_progress"); return; }
    if(!library.ready()) { reply(503,"upload_storage_unavailable"); return; }
    String name=d["name"] | "";
    if(!Harp::uploadNameValid(name.c_str()) || !d["size"].is<uint32_t>()
        || d["size"].as<uint32_t>()<26 || d["size"].as<uint32_t>()>Harp::UploadMaxBytes) { reply(400,"invalid_midi_name_or_size"); return; }
    auto state=player.status();
    if(state.busy || (state.state!="stopped" && state.state!="error")) { reply(409,"stop_before_uploading"); return; }
    std::vector<Song> songs;
    if(!library.list(songs) || songs.size()>=Harp::UploadMaxFiles) { reply(409,"upload_file_limit"); return; }
    String destination="/@web/"+name;
    if(LittleFS.exists(UploadLibrary::diskPath(destination))) { reply(409,"upload_name_exists"); return; }
    uint32_t size=d["size"];
    size_t total=library.totalBytes(),used=library.usedBytes();
    if(used>total || size+16384>total-used) { reply(507,"upload_storage_full"); return; }
    if(!player.beginMaintenance("uploading")) { reply(409,"player_busy"); return; }
    output=LittleFS.open(Temp,FILE_WRITE);
    if(!output) { cleanup(); reply(500,"upload_write_failed"); return; }
    active=true; expected=size; received=0; path=destination; touched=millis();
    char nonce[33]; for(int i=0;i<4;++i) snprintf(nonce+i*8,9,"%08x",esp_random()); id=nonce;
    reply(200);
}
void MidiUpload::chunk() {
    DynamicJsonDocument d(5120); if(!body(d,4608) || !session(d)) return;
    if(!d["offset"].is<uint32_t>() || d["offset"].as<uint32_t>()!=received) { reply(409,"upload_offset_mismatch"); return; }
    const char *data=d["data"] | ""; size_t count=0;
    if(mbedtls_base64_decode(buffer,sizeof(buffer),&count,reinterpret_cast<const uint8_t *>(data),strlen(data))
        || !count || count>expected-received) { reply(400,"invalid_upload_chunk"); return; }
    if(output.write(buffer,count)!=count) { cleanup(); reply(500,"upload_write_failed"); return; }
    received+=count; reply(200);
}
void MidiUpload::finish() {
    DynamicJsonDocument d(256); if(!body(d,192) || !session(d)) return;
    if(received!=expected) { reply(409,"upload_incomplete"); return; }
    output.flush(); output.close();
    String failure; bool valid=false;
    {
        Input input; input.file=LittleFS.open(Temp,FILE_READ); Harp::MidiFile midi;
        valid=input.file && input.file.size()==expected && midi.open(input);
        if(valid) {
            bool melody=false; Harp::Event event;
            while(midi.next(event)) if(event.type==0x90 && event.b && event.channel!=9) melody=true;
            if(!melody) { valid=false; failure="no_melodic_track_channel"; }
        } else failure=midi.error()?midi.error():"invalid_midi";
    }
    if(!valid) { cleanup(); reply(400,failure.c_str()); return; }
    if(LittleFS.exists(UploadLibrary::diskPath(path)) || !LittleFS.rename(Temp,UploadLibrary::diskPath(path))) {
        cleanup(); reply(500,"upload_save_failed"); return;
    }
    active=false; id=""; player.endMaintenance();
    reply(201);
}
void MidiUpload::remove() {
    DynamicJsonDocument d(512); if(!body(d)) return;
    String file=d["file"] | "";
    if(!library.ready() || !UploadLibrary::isUpload(file) || !Harp::uploadNameValid(file.substring(6).c_str())) { reply(400,"invalid_uploaded_file"); return; }
    auto state=player.status();
    if(active || state.busy || (state.state!="stopped" && state.state!="error")) { reply(409,"stop_before_uploading"); return; }
    if(!player.beginMaintenance("uploading")) { reply(409,"player_busy"); return; }
    bool exists=LittleFS.exists(UploadLibrary::diskPath(file));
    bool ok=exists && LittleFS.remove(UploadLibrary::diskPath(file)); player.endMaintenance();
    reply(ok?200:exists?500:404,ok?nullptr:exists?"upload_delete_failed":"uploaded_midi_not_found");
}
void MidiUpload::begin() {
    server.on("/api/v1/uploads",HTTP_GET,[this] {
        if(!authorized()) return;
        DynamicJsonDocument d(20000); d["ok"]=true; d["available"]=library.ready(); d["busy"]=active;
        d["totalBytes"]=library.totalBytes(); d["usedBytes"]=library.usedBytes();
        d["maxFileBytes"]=Harp::UploadMaxBytes; d["maxFiles"]=Harp::UploadMaxFiles;
        std::vector<Song> songs; library.list(songs); auto rows=d.createNestedArray("songs");
        for(const auto &song:songs) { auto row=rows.createNestedObject(); row["path"]=song.path; row["name"]=song.name; row["bytes"]=song.bytes; }
        String value; serializeJson(d,value); server.sendHeader("Cache-Control","no-store"); server.send(200,"application/json; charset=utf-8",value);
    });
    server.on("/api/v1/uploads/begin",HTTP_POST,[this]{prepare();});
    server.on("/api/v1/uploads/chunk",HTTP_POST,[this]{chunk();});
    server.on("/api/v1/uploads/finish",HTTP_POST,[this]{finish();});
    server.on("/api/v1/uploads/abort",HTTP_POST,[this]{DynamicJsonDocument d(256);if(body(d,192) && session(d)){cleanup();reply(200);}});
    server.on("/api/v1/uploads/remove",HTTP_POST,[this]{remove();});
}
void MidiUpload::tick() { if(active && uint32_t(millis()-touched)>30000) cleanup(); }
