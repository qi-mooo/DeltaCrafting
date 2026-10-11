#pragma once
#include "storage.h"
#include "upload_rules.h"

class UploadLibrary {
public:
    explicit UploadLibrary(Storage &s):storage(s) {}
    void begin();
    bool ready() const { return storage.sectors()!=0; }
    static bool isUpload(const String &path) { return path.startsWith("/@web/"); }
    static constexpr const char *Temp="/@web/.upload.part";
    bool beginWrite();
    bool endWrite() { return storage.endWrite(); }
    bool list(std::vector<Song> &songs);
    FsFile open(const String &path,oflag_t flags=O_RDONLY);
    bool exists(const String &path) { return storage.volume.exists(path.c_str()); }
    bool remove(const String &path) { return storage.volume.remove(path.c_str()); }
    bool rename(const String &from,const String &to) { return storage.volume.rename(from.c_str(),to.c_str()); }
    bool refreshSpace() { return storage.refreshSpace(); }
    uint64_t totalBytes() const { return storage.totalBytes(); }
    uint64_t usedBytes() const { return storage.usedBytes(); }
    bool spaceKnown() const { return storage.spaceKnown(); }
    const String &error() const { return storage.error(); }
    const String &migrationError() const { return migrationError_; }
    unsigned migratedSongs() const { return migrated_; }
    unsigned legacySongs() const { return legacy_; }
private:
    Storage &storage;
    String migrationError_;
    unsigned migrated_=0,legacy_=0;
};
