#include "midi_file.h"
#include <stdlib.h>
#include <string.h>
#include <cstddef>
#ifdef ARDUINO
#include <Arduino.h>
#endif
namespace {
size_t used = 0;
const char *parseError = nullptr;
Harp::Reader *activeReader = nullptr;
unsigned eventCount = 0;
bool keepParsing() {
    if (++eventCount % 256 == 0) {
#ifdef ARDUINO
        delay(1);
#endif
        if (activeReader && activeReader->cancelled()) { parseError = "midi_cancelled"; return false; }
    }
    return true;
}
struct alignas(std::max_align_t) Allocation { size_t size; };
void *limitedAlloc(size_t n) {
    if (n > Harp::MidiFile::MemoryLimit - used) { parseError = "midi_memory_limit"; return nullptr; }
    auto *p = static_cast<Allocation *>(malloc(sizeof(Allocation) + n));
    if (!p) { parseError = "out_of_memory"; return nullptr; }
    p->size = n; used += n; return p + 1;
}
void limitedFree(void *p) {
    if (!p) return;
    auto *a = static_cast<Allocation *>(p) - 1; used -= a->size; free(a);
}
void *limitedRealloc(void *p, size_t n) {
    if (!p) return limitedAlloc(n);
    auto *a = static_cast<Allocation *>(p) - 1;
    size_t old = a->size;
    if (n > Harp::MidiFile::MemoryLimit - (used - old)) { parseError = "midi_memory_limit"; return nullptr; }
    auto *b = static_cast<Allocation *>(realloc(a, sizeof(Allocation) + n));
    if (!b) { parseError = "out_of_memory"; return nullptr; }
    b->size = n; used = used - old + n; return b + 1;
}
struct MidiStream { Harp::Reader &reader; uint32_t offset = 0; };
int readStream(void *context, void *buffer, unsigned size) {
    auto &s = *static_cast<MidiStream *>(context);
    if (size > s.reader.size() - s.offset || !s.reader.read(s.offset, buffer, size)) return 0;
    s.offset += size; return int(size);
}
}
#define TML_IMPLEMENTATION
#define TML_NO_STDIO
#define TML_MALLOC limitedAlloc
#define TML_REALLOC limitedRealloc
#define TML_FREE limitedFree
#define TML_CONTINUE() keepParsing()
#define TML_WARN(msg) do { if (!parseError) parseError = "invalid_midi"; } while (0)
#define TML_ERROR(msg) do { if (!parseError) parseError = "invalid_midi"; } while (0)
#include <tml.h>
namespace Harp {
void MidiFile::close() { if (head) tml_free(head); head = cursor = nullptr; }
bool MidiFile::open(Reader &reader) {
    close(); error_ = nullptr; parseError = nullptr;
    uint8_t h[14];
    if (reader.size() > 512 * 1024 || reader.size() < sizeof(h) || !reader.read(0, h, sizeof(h))
        || memcmp(h, "MThd\0\0\0\6", 8) || h[8] || h[9] > 1 || (h[12] & 128)
        || !(h[12] | h[13]) || h[10] || !h[11] || h[11] > MaxTracks || (!h[9] && h[11] != 1)) {
        error_ = "requires_smf_0_or_1_ppqn_max32tracks_512KiB"; return false;
    }
    tracks = h[11];
    uint32_t offset = 14;
    for (unsigned i = 0; i < tracks; ++i) {
        uint8_t chunk[8];
        if (offset > reader.size() || reader.size() - offset < 8 || !reader.read(offset, chunk, 8)
            || memcmp(chunk,"MTrk",4)) { error_ = "invalid_track_chunk"; return false; }
        uint32_t n = uint32_t(chunk[4]) << 24 | uint32_t(chunk[5]) << 16 | uint32_t(chunk[6]) << 8 | chunk[7];
        offset += 8;
        if (!n || n > reader.size() - offset) { error_ = "truncated_track"; return false; }
        offset += n;
    }
    MidiStream s{reader}; tml_stream stream{&s, readStream};
    activeReader = &reader; eventCount = 0;
    head = tml_load(&stream);
    activeReader = nullptr;
    if (parseError || !head) { error_ = parseError ? parseError : "no_midi_events"; close(); return false; }
    for (auto *m = head; m; m = m->next) {
        if (m->time > 3600000 || (m->type == TML_SET_TEMPO && !tml_get_tempo_value(m))) {
            error_ = "invalid_tempo_or_duration_over_1h"; close(); return false;
        }
    }
    cursor = head; return true;
}
bool MidiFile::next(Event &event) {
    if (!cursor) return false;
    event.us = uint64_t(cursor->time) * 1000;
    event.track = cursor->track; event.channel = cursor->channel; event.type = cursor->type;
    event.a = cursor->key; event.b = cursor->velocity;
    cursor = cursor->next; return true;
}
}
