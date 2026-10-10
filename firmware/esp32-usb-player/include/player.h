#pragma once
#include "storage.h"
#include "midi_file.h"
#include "harp_mapping.h"
#include <USBHIDKeyboard.h>
#include <USBHIDMouse.h>
#include <atomic>

enum class PlayerCommand : uint8_t { Library, Play, Pause, Resume, Stop, Export, Settings, Format, Seek };
struct PlayerRequest {
    PlayerCommand command = PlayerCommand::Stop;
    char path[192] = {};
    int track = -1, channel = -1, transpose = 0, speed = 100, countdown = 3;
    bool loop = false, dryRun = false;
    uint32_t startMs=0, positionMs=0;
};
struct PlayerStatus {
    String state = "stopped", file, error;
    uint32_t elapsedMs = 0, durationMs = 0, countdownMs = 0, notes = 0;
    int track = -1, channel = -1, baseOctave = 4, speed = 100, transpose = 0;
    bool loop = false, busy = false, dryRun = false;
    uint32_t emittedNotes = 0;
};
class Player {
public:
    explicit Player(Storage &s) : storage(s) { reader.cancelFlag = &cancel; reader.storage=&s; }
    bool begin();
    bool submit(const PlayerRequest &request);
    bool beginMaintenance();
    void endMaintenance() { maintenance=false; }
    PlayerStatus status();
    std::vector<Song> library(size_t offset, size_t &total);
    std::atomic_bool usbConnected{false};
    std::atomic_bool cancel{false};
private:
    std::atomic_bool maintenance{false}, parked{false};
    struct FileReader : Harp::Reader {
        FsFile file;
        Storage *storage = nullptr;
        std::atomic_bool *cancelFlag = nullptr;
        bool cancelled() const override { return (cancelFlag && cancelFlag->load()) || !storage->readValid(); }
        uint32_t size() const override { return file.fileSize(); }
        bool read(uint32_t off, void *buffer, size_t n) override {
            return !cancelled() && file.seekSet(off) && file.read(buffer,n) == int(n) && !cancelled();
        }
    } reader;
    struct Candidate { uint32_t count = 0, sum = 0; uint8_t low = 127, high = 0; } candidates[Harp::MidiFile::MaxTracks][16];
    uint32_t histogram[128] = {};
    Storage &storage;
    Harp::MidiFile midi;
    Harp::Voice voice;
    Harp::Event next;
    Harp::Key output, pending;
    USBHIDKeyboard keyboard;
    USBHIDMouse mouse;
    QueueHandle_t queue = nullptr;
    SemaphoreHandle_t mutex = nullptr;
    PlayerStatus shared, current;
    std::vector<Song> songs;
    bool hasNext = false, keyPending = false;
    uint64_t musicUs = 0, trimUs = 0, totalUs = 0, startAt = 0, lastClock = 0, keyAt = 0;
    uint32_t loopStartMs=0;
    void worker();
    bool load(const PlayerRequest &request);
    bool rewind();
    bool seek(uint32_t milliseconds);
    void runCommand(const PlayerRequest &request);
    void publish();
    void release();
    void silence();
    void stop(const String &error = "");
    void sound(uint64_t now, bool retrigger = false);
    bool selected(const Harp::Event &e) const { return e.track == current.track && e.channel == current.channel; }
};
