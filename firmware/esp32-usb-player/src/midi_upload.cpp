#include "midi_upload.h"
#include <esp_system.h>
#include <mbedtls/base64.h>

namespace {
constexpr const char *Temp=UploadLibrary::Temp;
struct Input : Harp::Reader {
    FsFile file;
    uint32_t size() const override { return file.fileSize(); }
    bool read(uint32_t off,void *buffer,size_t n) override {
        return file.seekSet(off) && file.read(buffer,n)==int(n);
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
    output.close(); if(writeLease) { library.remove(Temp); library.endWrite(); writeLease=false; } active=false; id=""; player.endMaintenance();
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
    if(!player.beginMaintenance("uploading")) { reply(409,"player_busy"); return; }
    if(!library.beginWrite()) { player.endMaintenance(); reply(409,library.error().isEmpty()?"upload_storage_unavailable":library.error().c_str()); return; }
    writeLease=true; library.remove(Temp);
    std::vector<Song> songs;
    if(!library.list(songs) || songs.size()>=Harp::UploadMaxFiles) { cleanup(); reply(409,"upload_file_limit"); return; }
    String destination="/@web/"+name;
    if(library.exists(destination)) { cleanup(); reply(409,"upload_name_exists"); return; }
    uint32_t size=d["size"];
    library.refreshSpace();
    uint64_t total=library.totalBytes(),used=library.usedBytes();
    if(!library.spaceKnown() || used>total || size+16384>total-used) { cleanup(); reply(507,"upload_storage_full"); return; }
    output=library.open(Temp,O_WRONLY|O_CREAT|O_EXCL);
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
    if(output.write(buffer,count)!=count || !output.sync()) { cleanup(); reply(500,"upload_write_failed"); return; }
    received+=count; reply(200);
}
void MidiUpload::finish() {
    DynamicJsonDocument d(256); if(!body(d,192) || !session(d)) return;
    if(received!=expected) { reply(409,"upload_incomplete"); return; }
    if(!output.sync()) { cleanup(); reply(500,"upload_write_failed"); return; }
    output.close();
    String failure; bool valid=false;
    {
        Input input; input.file=library.open(Temp); Harp::MidiFile midi;
        valid=input.file && input.file.fileSize()==expected && midi.open(input);
        if(valid) {
            bool melody=false; Harp::Event event;
            while(midi.next(event)) if(event.type==0x90 && event.b && event.channel!=9) melody=true;
            if(!melody) { valid=false; failure="no_melodic_track_channel"; }
        } else failure=midi.error()?midi.error():"invalid_midi";
    }
    if(!valid) { cleanup(); reply(400,failure.c_str()); return; }
    if(library.exists(path) || !library.rename(Temp,path)) {
        cleanup(); reply(500,"upload_save_failed"); return;
    }
    library.refreshSpace();
    bool saved=library.endWrite(); writeLease=false;
    if(saved) player.uploadedSong({path,path.substring(6),expected});
    active=false; id=""; player.endMaintenance();
    reply(saved?201:500,saved?nullptr:"sd_sync_failed");
}
void MidiUpload::remove() {
    DynamicJsonDocument d(512); if(!body(d)) return;
    String file=d["file"] | "";
    if(!library.ready() || !UploadLibrary::isUpload(file) || !Harp::uploadNameValid(file.substring(6).c_str())) { reply(400,"invalid_uploaded_file"); return; }
    auto state=player.status();
    if(active || state.busy || (state.state!="stopped" && state.state!="error")) { reply(409,"stop_before_uploading"); return; }
    if(!player.beginMaintenance("uploading")) { reply(409,"player_busy"); return; }
    if(!library.beginWrite()) { player.endMaintenance(); reply(409,library.error().isEmpty()?"upload_storage_unavailable":library.error().c_str()); return; }
    bool exists=library.exists(file);
    bool ok=exists && library.remove(file); library.refreshSpace();
    ok=library.endWrite() && ok;
    if(ok) player.uploadedSong({file,"",0},true);
    player.endMaintenance();
    reply(ok?200:exists?500:404,ok?nullptr:exists?"upload_delete_failed":"uploaded_midi_not_found");
}
void MidiUpload::begin() {
    server.on("/api/v1/uploads",HTTP_GET,[this] {
        if(!authorized()) return;
        DynamicJsonDocument d(20000); d["ok"]=true; d["available"]=library.ready(); d["busy"]=active;
        d["destination"]="sd"; d["directory"]="/@web"; d["spaceKnown"]=library.spaceKnown();
        d["migrationError"]=library.migrationError();
        d["migratedSongs"]=library.migratedSongs(); d["legacySongs"]=library.legacySongs();
        d["totalBytes"]=library.totalBytes(); d["usedBytes"]=library.usedBytes();
        d["maxFileBytes"]=Harp::UploadMaxBytes; d["maxFiles"]=Harp::UploadMaxFiles;
        auto rows=d.createNestedArray("songs");
        size_t total=0,offset=0;
        do {
            auto songs=player.library(offset,total); offset+=songs.size();
            for(const auto &song:songs) if(UploadLibrary::isUpload(song.path)) { auto row=rows.createNestedObject(); row["path"]=song.path; row["name"]=song.name; row["bytes"]=song.bytes; }
            if(songs.empty()) break;
        } while(offset<total);
        String value; serializeJson(d,value); server.sendHeader("Cache-Control","no-store"); server.send(200,"application/json; charset=utf-8",value);
    });
    server.on("/api/v1/uploads/begin",HTTP_POST,[this]{prepare();});
    server.on("/api/v1/uploads/chunk",HTTP_POST,[this]{chunk();});
    server.on("/api/v1/uploads/finish",HTTP_POST,[this]{finish();});
    server.on("/api/v1/uploads/abort",HTTP_POST,[this]{DynamicJsonDocument d(256);if(body(d,192) && session(d)){cleanup();reply(200);}});
    server.on("/api/v1/uploads/remove",HTTP_POST,[this]{remove();});
}
void MidiUpload::tick() { if(active && uint32_t(millis()-touched)>30000) cleanup(); }
