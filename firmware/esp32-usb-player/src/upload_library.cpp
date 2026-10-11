#include "upload_library.h"
#include <esp_partition.h>

void UploadLibrary::begin() {
    ready_=LittleFS.begin(false);
    if(!ready_) {
        // Only initialize a blank, previously unused data partition. A damaged
        // existing library must never be silently formatted on reboot.
        auto partition=esp_partition_find_first(ESP_PARTITION_TYPE_DATA,ESP_PARTITION_SUBTYPE_DATA_SPIFFS,"spiffs");
        bool blank=partition!=nullptr; uint8_t data[512];
        for(size_t offset=0;blank && offset<partition->size;offset+=sizeof(data)) {
            blank=esp_partition_read(partition,offset,data,sizeof(data))==ESP_OK;
            for(auto b:data) if(b!=0xff) blank=false;
            if(offset%4096==0) delay(1);
        }
        if(blank && LittleFS.format()) ready_=LittleFS.begin(false);
    }
    if(ready_) {
        LittleFS.remove("/.upload.part");
        if(!LittleFS.exists("/midi")) ready_=LittleFS.mkdir("/midi");
    }
}
bool UploadLibrary::list(std::vector<Song> &songs) {
    if(!ready_) return true;
    auto directory=LittleFS.open("/midi"); if(!directory || !directory.isDirectory()) return false;
    for(auto file=directory.openNextFile();file;file=directory.openNextFile()) {
        String name=file.name();
        if(!file.isDirectory() && Harp::uploadNameValid(name.c_str()))
            songs.push_back({"/@web/"+name,name,uint32_t(file.size())});
    }
    return true;
}
fs::File UploadLibrary::open(const String &path) {
    if(!ready_ || !isUpload(path) || !Harp::uploadNameValid(path.substring(6).c_str())) return {};
    return LittleFS.open(diskPath(path),FILE_READ);
}
