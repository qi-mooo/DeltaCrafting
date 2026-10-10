#include "storage.h"
#include <algorithm>

Storage *Storage::instance = nullptr;
bool CardPins::valid() const {
    const int values[] = {clk,cmd,d0,cs};
    for (unsigned i = 0; i < (mmc ? 3u : 4u); ++i) {
        int p = values[i];
        // Verified FN8 Dongle uses 35..37 for SD; octal-PSRAM S3 boards differ.
        if (p < 0 || p > 48 || p == 19 || p == 20 || (p >= 22 && p <= 32)
            || (p == 46 && i != 2)) return false;
        for (unsigned j = 0; j < i; ++j) if (p == values[j]) return false;
    }
    return true;
}
Storage::Storage() { instance = this; }
const char *Storage::ownerName() const {
    switch (owner()) {
    case CardOwner::Host: return "usb";
    case CardOwner::Ejected: return "ejected";
    case CardOwner::Player: return "player";
    default: return "missing";
    }
}
String Storage::identity() const { return cardId; }
bool Storage::begin(const CardPins &pins) {
    mutex = xSemaphoreCreateMutex();
    msc.vendorID("Delta"); msc.productID("Harp SD Card"); msc.productRevision("1.0");
    msc.onRead([](uint32_t lba, uint32_t off, void *buf, uint32_t n) {
        return instance->transfer(lba, off, static_cast<uint8_t *>(buf), n, false);
    });
    msc.onWrite([](uint32_t lba, uint32_t off, uint8_t *buf, uint32_t n) {
        return instance->transfer(lba, off, buf, n, true);
    });
    msc.onStartStop([](uint8_t, bool start, bool eject) {
        if (!eject) return true;
        xSemaphoreTake(instance->mutex, portMAX_DELAY);
        bool ok = true;
        if (!start && instance->owner() == CardOwner::Host) {
            ok = instance->blocks->syncDevice();
            if (ok) {
                instance->owner_ = CardOwner::Ejected;
                instance->msc.mediaPresent(false);
            }
        } else if (start && instance->owner() != CardOwner::Host) ok = false;
        xSemaphoreGive(instance->mutex);
        return ok;
    });
    if (!mutex) { error_ = "mutex_failed"; return false; }
    if (!pins.valid()) error_ = "sd_pins_not_configured";
    else if (pins.mmc) {
        sdmmc_host_t host = SDMMC_HOST_DEFAULT();
        host.flags = SDMMC_HOST_FLAG_1BIT; host.max_freq_khz = SDMMC_FREQ_DEFAULT;
        sdmmc_slot_config_t slot = SDMMC_SLOT_CONFIG_DEFAULT();
        slot.width = 1; slot.clk = gpio_num_t(pins.clk); slot.cmd = gpio_num_t(pins.cmd); slot.d0 = gpio_num_t(pins.d0);
        slot.flags |= SDMMC_SLOT_FLAG_INTERNAL_PULLUP;
        esp_err_t result = sdmmc_host_init();
        if (result == ESP_OK) result = sdmmc_host_init_slot(host.slot, &slot);
        if (result == ESP_OK) result = sdmmc_card_init(&host, &mmc.card);
        if (result == ESP_OK && mmc.card.csd.sector_size == 512) {
            blocks = &mmc;
            cardId = String(mmc.card.cid.mfg_id, HEX) + "-" + String(mmc.card.cid.serial, HEX);
        } else error_ = String("sdmmc_init:") + esp_err_to_name(result);
    } else {
        SPI.begin(pins.clk, pins.d0, pins.cmd, pins.cs);
        if (spi.cardBegin(SdSpiConfig(pins.cs, SHARED_SPI, SD_SCK_MHZ(10), &SPI))) {
            blocks = spi.card(); cid_t cid{};
            if (spi.card()->readCID(&cid)) {
                const uint8_t *raw = reinterpret_cast<const uint8_t *>(&cid);
                for (size_t i = 0; i < sizeof(cid); ++i) { char h[3]; snprintf(h,sizeof(h),"%02x",raw[i]); cardId += h; }
            }
        } else error_ = "sd_spi_init_failed";
    }
    // Register MSC even without media so USB CDC diagnostics remain accessible.
    msc.begin(blocks ? sectors() : 1, 512);
    owner_ = blocks ? CardOwner::Host : CardOwner::Missing;
    lastIo=millis();
    msc.mediaPresent(blocks != nullptr);
    return blocks != nullptr;
}
int32_t Storage::transfer(uint32_t lba, uint32_t offset, uint8_t *buffer, uint32_t size, bool write) {
    if (!mutex) return -1;
    ++pendingIo;
    xSemaphoreTake(mutex, portMAX_DELAY);
    uint64_t pos = uint64_t(lba) * 512 + offset;
    bool ok = owner() == CardOwner::Host && blocks && pos + size <= uint64_t(sectors()) * 512;
    uint32_t done = 0;
    alignas(4) uint8_t sector[512];
    while (ok && done < size) {
        uint32_t index = pos / 512, within = pos % 512;
        uint32_t n = std::min(uint32_t(512 - within), size - done);
        if (!write || within || n != 512) ok = blocks->readSector(index, sector);
        if (!ok) break;
        if (write) { memcpy(sector + within, buffer + done, n); ok = blocks->writeSector(index, sector); }
        else memcpy(buffer + done, sector + within, n);
        done += n; pos += n;
    }
    if (ok && write) ok = blocks->syncDevice();
    lastIo=millis(); --pendingIo;
    xSemaphoreGive(mutex);
    return ok ? int32_t(size) : -1;
}
bool Storage::suspendUsbForUpdate() {
    // Called only after the player worker has parked. Never mount or modify the
    // host filesystem. This drains device I/O, not unsubmitted host write caches.
    if(!mutex || xSemaphoreTake(mutex,pdMS_TO_TICKS(100))!=pdTRUE) { error_="sd_io_busy"; return false; }
    bool ok=true;
    if(owner()==CardOwner::Host) {
        if(pendingIo.load() || millis()-lastIo<2000) { error_="sd_io_busy"; ok=false; }
        else if(!blocks->syncDevice()) { error_="sd_sync_failed"; ok=false; }
        else { msc.mediaPresent(false); owner_=CardOwner::Ejected; updateSuspended=true; error_=""; }
    }
    xSemaphoreGive(mutex); return ok;
}
void Storage::restoreUsbAfterUpdate() {
    if(!mutex) return;
    xSemaphoreTake(mutex,portMAX_DELAY);
    if(updateSuspended) { owner_=CardOwner::Host; msc.mediaPresent(true); updateSuspended=false; }
    xSemaphoreGive(mutex);
}
bool Storage::claim() {
    if (owner() == CardOwner::Player) return true;
    xSemaphoreTake(mutex, portMAX_DELAY);
    bool ok = owner() == CardOwner::Ejected;
    if (!ok) error_ = owner() == CardOwner::Host ? "eject_sd_on_computer_first" : "sd_not_available";
    else {
        ok = volume.begin(blocks) || volume.begin(blocks, true, 0);
        if (ok) { owner_ = CardOwner::Player; error_ = ""; }
        else error_ = "filesystem_mount_failed";
    }
    xSemaphoreGive(mutex);
    return ok;
}
bool Storage::exportUsb() {
    xSemaphoreTake(mutex, portMAX_DELAY);
    bool ok = blocks != nullptr;
    if (ok) {
        volume.end(); ok = blocks->syncDevice();
        if (ok) { owner_ = CardOwner::Host; msc.mediaPresent(true); error_ = ""; }
    }
    xSemaphoreGive(mutex);
    return ok;
}
bool Storage::formatExfat() {
    xSemaphoreTake(mutex, portMAX_DELAY);
    bool ok = blocks && (owner() == CardOwner::Ejected || owner() == CardOwner::Player);
    if (ok) {
        volume.end(); owner_ = CardOwner::Ejected;
        alignas(4) uint8_t buffer[512]; ExFatFormatter formatter;
        ok = formatter.format(blocks, buffer) && blocks->syncDevice() && volume.begin(blocks);
        if (ok) { owner_ = CardOwner::Player; error_ = ""; }
        else error_ = "exfat_format_failed";
    } else error_ = "eject_sd_on_computer_first";
    xSemaphoreGive(mutex);
    return ok;
}
bool Storage::walk(const String &path, unsigned depth, std::vector<Song> &songs) {
    FsFile dir, entry;
    if (!dir.open(&volume, path.c_str(), O_RDONLY)) return false;
    char name[192];
    while (entry.openNext(&dir, O_RDONLY)) {
        if (!entry.getName(name, sizeof(name))) {
            error_ = "filename_too_long_or_unreadable"; return false;
        }
        String leaf(name), full = path == "/" ? path + leaf : path + "/" + leaf;
        bool directory = entry.isDirectory(); uint64_t bytes = entry.fileSize();
        entry.close();
        if (leaf.startsWith(".")) continue;
        if (directory && depth < 3) { if (!walk(full, depth + 1, songs)) return false; }
        else {
            String lower = leaf; lower.toLowerCase();
            if (!directory && (lower.endsWith(".mid") || lower.endsWith(".midi") || lower.endsWith(".kar"))) {
                if (songs.size() >= 500 || full.length() > 191) { error_ = "library_limit_500_or_path_191"; return false; }
                songs.push_back({full, leaf, uint32_t(bytes)});
            }
        }
        delay(1);
    }
    return true;
}
bool Storage::list(std::vector<Song> &songs) {
    songs.clear();
    if (!claim()) return false;
    bool ok = walk("/", 0, songs);
    std::sort(songs.begin(), songs.end(), [](const Song &a, const Song &b) { return a.path < b.path; });
    if (!ok && error_.isEmpty()) error_ = "directory_read_failed";
    return ok;
}
