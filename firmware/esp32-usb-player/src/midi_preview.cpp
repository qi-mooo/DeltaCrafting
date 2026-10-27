#include "midi_preview.h"
#include <ArduinoJson.h>

void MidiPreview::error(int code,const char *message) {
    StaticJsonDocument<192> d; d["ok"]=false; d["error"]=message;
    String text; serializeJson(d,text);
    server.sendHeader("Cache-Control","no-store");
    server.send(code,"application/json; charset=utf-8",text);
}
void MidiPreview::begin() {
    server.on("/api/v1/preview/midi",HTTP_GET,[this]{download();});
}
void MidiPreview::download() {
    if(!authorized()) return;
    String path=server.arg("file");
    if(!path.startsWith("/") || path.length()>=192 || path.indexOf("..")>=0 || path.indexOf('\\')>=0) {
        error(400,"invalid_preview_file"); return;
    }
    // Serve only a known library entry, never arbitrary files on the card.
    bool found=false; size_t offset=0,total=0;
    do {
        auto rows=player.library(offset,total); offset+=rows.size();
        for(const auto &song:rows) if(song.path==path) found=true;
        if(rows.empty()) break;
    } while(!found && offset<total);
    if(!found) { error(404,"preview_file_not_found"); return; }
    if(!player.beginInspection()) { error(409,"stop_before_previewing"); return; }
    struct Release { Player &p; ~Release(){p.endMaintenance();} } release{player};
    if(!storage.beginRead()) { error(409,storage.error().c_str()); return; }
    FsFile file;
    bool opened=file.open(&storage.volume,path.c_str(),O_RDONLY) && !file.isDirectory();
    uint64_t size=opened?file.fileSize():0;
    if(!opened || size<14 || size>512*1024) {
        file.close(); storage.endRead(); error(400,"midi_open_failed_or_larger_than_512KiB"); return;
    }
    auto client=server.client();
    server.sendHeader("Cache-Control","no-store");
    server.sendHeader("X-Content-Type-Options","nosniff");
    server.setContentLength(size); server.send(200,"audio/midi","");
    uint32_t remaining=size;
    bool valid=true,closed=false;
    while(remaining && valid && client.connected()) {
        size_t n=std::min(size_t(remaining),sizeof(buffer));
        valid=storage.readValid() && file.read(buffer,n)==int(n) && storage.readValid();
        if(!valid) break;
        // Withhold the final block until the whole read window is verified.
        // A concurrent host write or disconnect leaves a truncated HTTP body,
        // so fetch fails instead of playing a partially changed MIDI file.
        if(n==remaining) { file.close(); valid=storage.endRead(); closed=true; }
        if(!valid) break;
        valid=client.write(buffer,n)==n;
        remaining-=n;
        delay(1);
    }
    if(!closed) { file.close(); storage.endRead(); }
    if(!valid || remaining) client.stop();
}
