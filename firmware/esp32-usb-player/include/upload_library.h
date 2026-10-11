#pragma once
#include "storage.h"
#include <LittleFS.h>
#include "upload_rules.h"

class UploadLibrary {
public:
    void begin();
    bool ready() const { return ready_; }
    static bool isUpload(const String &path) { return path.startsWith("/@web/"); }
    static String diskPath(const String &path) { return "/midi/"+path.substring(6); }
    bool list(std::vector<Song> &songs);
    fs::File open(const String &path);
    size_t totalBytes() { return ready_?LittleFS.totalBytes():0; }
    size_t usedBytes() { return ready_?LittleFS.usedBytes():0; }
private:
    bool ready_=false;
};
