#include "upload_library.h"
#include <LittleFS.h>

bool UploadLibrary::beginWrite() {
    if(!storage.beginWrite()) return false;
    if(!storage.volume.exists("/@web") && !storage.volume.mkdir("/@web")) {
        storage.endWrite(); return false;
    }
    return true;
}
FsFile UploadLibrary::open(const String &path,oflag_t flags) {
    FsFile file; file.open(&storage.volume,path.c_str(),flags); return file;
}
bool UploadLibrary::list(std::vector<Song> &songs) {
    auto dir=open("/@web");
    if(!dir || !dir.isDirectory()) return false;
    FsFile file; char name[192];
    while(file.openNext(&dir,O_RDONLY)) {
        if(!file.getName(name,sizeof(name))) return false;
        if(!file.isDirectory() && Harp::uploadNameValid(name)) songs.push_back({"/@web/"+String(name),String(name),uint32_t(file.fileSize())});
        file.close(); delay(1);
    }
    return true;
}
void UploadLibrary::begin() {
    // Only the boot-time v10 migration uses flash. Never format or create a new
    // internal library. SD filenames keep the old paths so favorites stay valid.
    if(!ready() || !beginWrite()) { migrationError_=error(); return; }
    remove(Temp); // Interrupted SD uploads are never part of the visible library.
    if(!LittleFS.begin(false)) { endWrite(); return; }
    auto dir=LittleFS.open("/midi");
    std::vector<String> moved;
    for(auto old=dir.openNextFile();old;old=dir.openNextFile()) {
        String name=old.name();
        name=name.substring(name.lastIndexOf('/')+1);
        if(old.isDirectory() || !Harp::uploadNameValid(name.c_str())) continue;
        ++legacy_;
        String path="/@web/"+name;
        uint8_t bytes[1024],check[1024]; bool ok=true;
        if(!exists(path)) {
            auto out=open(Temp,O_WRONLY|O_CREAT|O_EXCL);
            ok=bool(out);
            while(ok && old.available()) {
                size_t n=old.read(bytes,sizeof(bytes));
                ok=n && out.write(bytes,n)==n; delay(1);
            }
            ok=out.sync() && ok; out.close();
            if(ok) ok=rename(Temp,path);
            if(!ok) remove(Temp);
        }
        auto copy=open(path); old.seek(0);
        ok=ok && copy && copy.fileSize()==old.size();
        while(ok && old.available()) {
            size_t n=old.read(bytes,sizeof(bytes));
            ok=n && copy.read(check,n)==int(n) && memcmp(bytes,check,n)==0; delay(1);
        }
        copy.close();
        if(ok) moved.push_back("/midi/"+name);
        else migrationError_="legacy_midi_migration_failed";
    }
    dir.close(); refreshSpace();
    if(endWrite()) {
        // Delete flash originals only after SD sync and byte-for-byte verification.
        for(const auto &path:moved) {
            if(LittleFS.remove(path)) { ++migrated_; --legacy_; }
            else migrationError_="legacy_midi_cleanup_failed";
        }
        LittleFS.remove("/.upload.part");
    } else migrationError_=error();
    LittleFS.end();
}
