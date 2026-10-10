#include "player.h"
#include <esp_timer.h>
#include <algorithm>
#include "playback_seek.h"

bool Player::begin() {
    mutex = xSemaphoreCreateMutex(); queue = xQueueCreate(4, sizeof(PlayerRequest));
    keyboard.begin(); mouse.begin();
    return mutex && queue && xTaskCreatePinnedToCore([](void *p) { static_cast<Player *>(p)->worker(); },
        "harp-playback", 12288, this, 3, nullptr, 1) == pdPASS;
}
bool Player::submit(const PlayerRequest &request) {
    if (maintenance.load()) return false;
    // Stop is out of band so a long file scan is cancellable immediately.
    if (request.command == PlayerCommand::Stop) { cancel.store(true); return true; }
    return queue && xQueueSend(queue, &request, 0) == pdTRUE;
}
bool Player::beginMaintenance() {
    maintenance=true; cancel=true;
    uint32_t started=millis();
    while (!parked.load() && millis()-started<5000) delay(1);
    if (parked.load()) return true;
    maintenance=false; return false;
}
PlayerStatus Player::status() {
    if (!mutex) return shared;
    xSemaphoreTake(mutex, portMAX_DELAY); auto copy = shared; xSemaphoreGive(mutex); return copy;
}
std::vector<Song> Player::library(size_t offset, size_t &total) {
    total = 0;
    if (!mutex) return {};
    xSemaphoreTake(mutex, portMAX_DELAY);
    total = songs.size(); offset = std::min(offset, total);
    std::vector<Song> page(songs.begin() + offset, songs.begin() + std::min(offset + 32, total));
    xSemaphoreGive(mutex); return page;
}
void Player::publish() {
    current.elapsedMs = musicUs / 1000; current.durationMs = totalUs / 1000;
    current.countdownMs = startAt > uint64_t(esp_timer_get_time()) && current.state == "countdown"
        ? (startAt - esp_timer_get_time()) / 1000 : 0;
    xSemaphoreTake(mutex, portMAX_DELAY); shared = current; xSemaphoreGive(mutex);
}
void Player::release() {
    if(!current.dryRun && (output.usage || output.mouse || pending.usage || pending.mouse)) {
        keyboard.releaseAll(); mouse.release(MOUSE_ALL);
    }
    output = {}; pending = {}; keyPending = false;
}
void Player::silence() { release(); voice.clear(); }
void Player::stop(const String &error) {
    silence(); reader.file.close(); midi.close(); hasNext = false;
    current.state = error.isEmpty() ? "stopped" : "error"; current.error = error; current.busy = false;
}
bool Player::rewind() {
    return seek(loopStartMs);
}
bool Player::seek(uint32_t milliseconds) {
    uint64_t target=uint64_t(milliseconds)*1000;
    if(target>totalUs) { current.error="seek_out_of_range"; return false; }
    release();
    hasNext=Harp::seekEvents(midi,voice,next,target+trimUs,current.track,current.channel);
    musicUs=target; lastClock=esp_timer_get_time();
    if(midi.error()) { stop(midi.error()); return false; }
    if(current.state=="playing") sound(lastClock,true);
    return true;
}
bool Player::load(const PlayerRequest &request) {
    stop(); current.file = request.path; current.state = "loading"; current.busy = true;
    current.dryRun = request.dryRun; current.emittedNotes = 0; publish();
    bool loaded=false; String failure;
    for(unsigned attempt=0;attempt<25 && !cancel.load();++attempt) {
        if(storage.beginRead()) {
            bool opened=reader.file.open(&storage.volume,request.path,O_RDONLY) && !reader.file.isDirectory()
                && reader.file.fileSize()<=512*1024;
            loaded=opened && midi.open(reader);
            failure=opened ? String(midi.error() ? midi.error() : "") : "midi_open_failed_or_larger_than_512KiB";
            reader.file.close();
            if(!storage.endRead()) { loaded=false; midi.close(); failure=storage.error(); }
            if(loaded) break;
        } else failure=storage.error();
        if(failure!="sd_busy_retry" && failure!="sd_changed_retry") break;
        delay(250);
    }
    if(cancel.load()) { stop(); return false; }
    if(!loaded) { stop(failure); return false; }
    memset(candidates, 0, sizeof(candidates));
    for (auto &track : candidates) for (auto &c : track) c.low = 127;
    Harp::Event e; totalUs = 0; unsigned processed = 0;
    while (midi.next(e)) {
        totalUs = e.us;
        if (e.type == 0x90 && e.b && e.channel != 9) {
            auto &c = candidates[e.track][e.channel]; ++c.count; c.sum += e.a;
            c.low = std::min(c.low, e.a); c.high = std::max(c.high, e.a);
        }
        if (!(++processed % 256)) { delay(1); if (cancel.load()) { stop(); return false; } }
    }
    if (midi.error()) { stop(midi.error()); return false; }
    double best = -1e20; current.track = current.channel = -1;
    for (unsigned t = 0; t < midi.trackCount(); ++t) for (int ch = 0; ch < 16; ++ch) {
        auto &c = candidates[t][ch];
        if (!c.count || ch == 9 || (request.track >= 0 && request.track != int(t))
            || (request.channel >= 0 && request.channel != ch)) continue;
        double average = double(c.sum) / c.count;
        double score = std::min(c.count, uint32_t(400)) + (average >= 55 && average <= 85 ? 80 : 0)
            - std::max(0, int(c.high) - int(c.low) - 37) * 4;
        if (score > best) { best = score; current.track = t; current.channel = ch; }
    }
    if (current.track < 0) { stop("no_melodic_track_channel"); return false; }
    memset(histogram, 0, sizeof(histogram)); trimUs = UINT64_MAX;
    midi.rewind();
    processed = 0;
    while (midi.next(e)) {
        if (selected(e) && e.type == 0x90 && e.b) { ++histogram[e.a]; trimUs = std::min(trimUs, e.us); }
        if (!(++processed % 256)) { delay(1); if (cancel.load()) { stop(); return false; } }
    }
    if (midi.error()) { stop(midi.error()); return false; }
    current.transpose = request.transpose; current.speed = request.speed; current.loop = request.loop;
    current.baseOctave = Harp::autoOctave(histogram, request.transpose);
    current.notes = candidates[current.track][current.channel].count;
    totalUs = totalUs > trimUs ? totalUs - trimUs : 0;
    if(request.startMs>=totalUs/1000 && request.startMs) { stop("start_out_of_range"); return false; }
    loopStartMs=request.startMs;
    if (!seek(request.startMs)) { stop(current.error); return false; }
    if (!usbConnected.load()) { stop("usb_host_not_connected"); return false; }
    current.busy = false; current.state = "countdown";
    startAt = esp_timer_get_time() + uint64_t(request.countdown) * 1000000;
    return true;
}
void Player::sound(uint64_t now, bool retrigger) {
    int pitch = voice.highest();
    Harp::Key desired = pitch < 0 ? Harp::Key{} : Harp::mapPitch(pitch + current.transpose, current.baseOctave);
    if (desired != pending || retrigger) {
        if(!current.dryRun) { keyboard.releaseAll(); mouse.release(MOUSE_ALL); }
        output.usage = 0; keyPending = false; output.mouse = 0;
        pending = desired;
        if (desired.usage) {
            if (desired.mouse && !current.dryRun) mouse.press(desired.mouse);
            output.mouse = desired.mouse;
            keyAt = now + 12000; keyPending = true;
        }
    }
    if (keyPending && now >= keyAt && pending.usage) {
        if (!current.dryRun) keyboard.pressRaw(pending.usage);
        ++current.emittedNotes; output.usage = pending.usage; keyPending = false;
    }
}
void Player::runCommand(const PlayerRequest &r) {
    current.error = "";
    switch (r.command) {
    case PlayerCommand::Play: load(r); break;
    case PlayerCommand::Pause:
        if (current.state == "playing" || current.state == "countdown") { release(); current.state = "paused"; }
        break;
    case PlayerCommand::Resume:
        if (current.state == "paused" && usbConnected.load()) {
            current.state = "playing"; lastClock = esp_timer_get_time(); sound(lastClock, true);
        } else if (!usbConnected.load()) current.error = "usb_host_not_connected";
        break;
    case PlayerCommand::Stop: stop(); break;
    case PlayerCommand::Seek:
        if(current.state=="playing" || current.state=="paused" || current.state=="countdown") seek(r.positionMs);
        else current.error="no_song_loaded";
        break;
    case PlayerCommand::Library: {
        if (current.state == "playing" || current.state == "countdown" || current.state == "paused") { current.error = "stop_before_refreshing_library"; break; }
        current.busy = true; publish(); std::vector<Song> fetched;
        bool scanned=false;
        for(unsigned attempt=0;attempt<25 && !cancel.load();++attempt) {
            scanned=storage.list(fetched);
            if(scanned) break;
            current.error=storage.error();
            if(current.error!="sd_busy_retry" && current.error!="sd_changed_retry") break;
            delay(250);
        }
        if(scanned && !cancel.load()) {
            current.error="";
            xSemaphoreTake(mutex, portMAX_DELAY); songs = std::move(fetched); xSemaphoreGive(mutex);
        }
        current.busy = false; break;
    }
    case PlayerCommand::Export:
        stop(); if (!storage.exportUsb()) current.error = storage.error();
        break;
    case PlayerCommand::Settings:
        current.speed = r.speed; current.transpose = r.transpose; current.loop = r.loop;
        current.baseOctave = Harp::autoOctave(histogram, current.transpose);
        release(); if (current.state == "playing") sound(esp_timer_get_time(), true);
        break;
    case PlayerCommand::Format:
        stop(); if (!storage.formatExfat()) current.error = storage.error();
        xSemaphoreTake(mutex, portMAX_DELAY); songs.clear(); xSemaphoreGive(mutex); break;
    }
}
void Player::worker() {
    uint64_t lastPublish = 0;
    for (;;) {
        if (maintenance.load()) {
            xQueueReset(queue); stop(); current.state="updating"; publish(); parked=true;
            while (maintenance.load()) vTaskDelay(pdMS_TO_TICKS(5));
            parked=false; cancel=false; stop(); publish();
        }
        if (cancel.exchange(false)) {
            xQueueReset(queue); stop(); publish();
        }
        PlayerRequest request;
        if (xQueueReceive(queue, &request, 0) == pdTRUE) { runCommand(request); publish(); }
        uint64_t now = esp_timer_get_time();
        if (!usbConnected.load() && (current.state == "playing" || current.state == "countdown")) {
            release(); current.state = "paused"; current.error = "usb_disconnected";
        }
        if (current.state == "countdown" && now >= startAt) { current.state = "playing"; lastClock = now; }
        if (current.state == "playing") {
            musicUs += (now - lastClock) * current.speed / 100; lastClock = now;
            // A constant 12 ms HID preparation delay is used for every onset.
            // Coalesce same-time chord events before choosing the highest note.
            bool onsets[128] = {}; unsigned processed = 0;
            while (hasNext && next.us <= musicUs + trimUs && processed++ < 512) {
                if (selected(next)) {
                    if (next.type == 0x90 && next.b) onsets[next.a] = true;
                    voice.event(next.type, next.a, next.b);
                }
                hasNext = midi.next(next);
            }
            if (midi.error()) stop(midi.error());
            else if (!hasNext && musicUs >= totalUs + 20000) {
                if (current.loop) { if (!rewind()) stop(); lastClock = now; }
                else stop();
            } else { int highest=voice.highest(); sound(now, highest>=0 && onsets[highest]); }
        }
        if (now - lastPublish > 100000) { publish(); lastPublish = now; }
        vTaskDelay(pdMS_TO_TICKS(1));
    }
}
