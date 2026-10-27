#pragma once
#include <LittleFS.h>

// The previously unused 3.375 MB filesystem partition is dedicated to UI cache.
// NVS remains untouched: its 20 KB also holds Wi-Fi/pairing/OTA settings.
class UiResumeStore {
public:
    class Stream {
    public:
        Stream(fs::File file, bool writing) : file_(file), writing_(writing) {}
        explicit operator bool() const { return bool(file_); }
        size_t size() const { return file_.size(); }
        bool available() { return position_ < used_ || file_.available(); }
        int read()
        {
            if (position_ == used_) { used_ = file_.read(buffer_, sizeof(buffer_)); position_ = 0; }
            return position_ < used_ ? buffer_[position_++] : -1;
        }
        size_t write(const uint8_t *byte, size_t)
        {
            if (used_ == sizeof(buffer_) && !flush()) return 0;
            buffer_[used_++] = *byte;
            return 1;
        }
        bool finish() { return !writing_ || flush(); }
        void close() { file_.close(); }
    private:
        bool flush()
        {
            bool ok = file_.write(buffer_, used_) == used_;
            used_ = 0;
            return ok;
        }
        fs::File file_;
        bool writing_;
        uint8_t buffer_[256];
        size_t position_ = 0, used_ = 0;
    };
    bool begin() { return LittleFS.begin(true, "/delta-resume", 2, "spiffs"); }
    Stream create() { return {LittleFS.open(Temporary, "w"), true}; }
    Stream open() { return {LittleFS.open(Snapshot, "r"), false}; }
    bool commit(bool ok)
    {
        if (!ok) { LittleFS.remove(Temporary); return false; }
        return LittleFS.rename(Temporary, Snapshot);
    }
    bool clear()
    {
        LittleFS.remove(Temporary);
        return !LittleFS.exists(Snapshot) || LittleFS.remove(Snapshot);
    }
private:
    static constexpr const char *Temporary = "/ui-resume.tmp";
    static constexpr const char *Snapshot = "/ui-resume.bin";
};
