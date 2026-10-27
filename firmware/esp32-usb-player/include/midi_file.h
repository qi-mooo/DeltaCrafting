#pragma once
#include <stdint.h>
#include <stddef.h>
#include <tml.h>
namespace Harp {
struct Reader {
    virtual ~Reader() = default;
    virtual uint32_t size() const = 0;
    virtual bool read(uint32_t offset, void *buffer, size_t length) = 0;
    virtual bool cancelled() const { return false; }
};
struct Event { uint64_t us = 0; uint8_t track = 0, channel = 0, type = 0, a = 0, b = 0; };
class MidiFile {
public:
    static constexpr unsigned MaxTracks = 32;
    static constexpr size_t MemoryLimit = 160 * 1024;
    ~MidiFile() { close(); }
    bool open(Reader &reader);
    void close();
    void rewind() { cursor = head; }
    bool next(Event &event);
    const char *error() const { return error_; }
    unsigned trackCount() const { return tracks; }
private:
    tml_message *head = nullptr, *cursor = nullptr;
    unsigned tracks = 0;
    const char *error_ = nullptr;
};
}
