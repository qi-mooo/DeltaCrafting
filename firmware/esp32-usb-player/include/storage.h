#pragma once
#include <Arduino.h>
#include <SdFat.h>
#include <USBMSC.h>
#include <driver/sdmmc_host.h>
#include <sdmmc_cmd.h>
#include <atomic>
#include <vector>
#include "read_window.h"

struct CardPins {
    bool mmc = false;
    int clk = -1, cmd = -1, d0 = -1, cs = -1;
    bool valid() const;
};
struct Song { String path, name; uint32_t bytes = 0; };
enum class CardOwner { Missing, Host, Ejected, Player };
class Storage {
public:
    Storage();
    bool begin(const CardPins &pins);
    bool beginRead();
    bool endRead();
    bool readValid();
    bool exportUsb();
    bool suspendUsbForUpdate();
    void restoreUsbAfterUpdate();
    bool formatExfat();
    bool list(std::vector<Song> &songs);
    FsVolume volume;
    CardOwner owner() const { return owner_.load(); }
    const char *ownerName() const;
    uint32_t sectors() const { return blocks ? blocks->sectorCount() : 0; }
    const String &error() const { return error_; }
    String identity() const;
private:
    // Never expose the real writable block device to the song reader. Even an
    // accidental filesystem sync cannot modify a disk mounted by the computer.
    struct ReadView : FsBlockDeviceInterface {
        Storage *storage = nullptr;
        Harp::ReadWindow window;
        bool isBusy() override { return false; }
        bool readSector(uint32_t s, uint8_t *b) override { return readSectors(s,b,1); }
        bool readSectors(uint32_t s, uint8_t *b, size_t n) override;
        bool writeSector(uint32_t, const uint8_t *) override { return false; }
        bool writeSectors(uint32_t, const uint8_t *, size_t) override { return false; }
        uint32_t sectorCount() override;
        bool syncDevice() override { return true; }
    } readView;
    struct MmcBlock : FsBlockDeviceInterface {
        sdmmc_card_t card{};
        bool isBusy() override { return false; }
        bool readSector(uint32_t s, uint8_t *b) override { return readSectors(s,b,1); }
        bool readSectors(uint32_t s, uint8_t *b, size_t n) override { return sdmmc_read_sectors(&card,b,s,n) == ESP_OK; }
        bool writeSector(uint32_t s, const uint8_t *b) override { return writeSectors(s,b,1); }
        bool writeSectors(uint32_t s, const uint8_t *b, size_t n) override { return sdmmc_write_sectors(&card,b,s,n) == ESP_OK; }
        uint32_t sectorCount() override { return card.csd.capacity; }
        bool syncDevice() override { return sdmmc_get_status(&card) == ESP_OK; }
    } mmc;
    SdFs spi;
    FsBlockDevice *blocks = nullptr;
    USBMSC msc;
    SemaphoreHandle_t mutex = nullptr;
    std::atomic<CardOwner> owner_{CardOwner::Missing};
    std::atomic<uint32_t> pendingIo{0};
    std::atomic<uint32_t> pendingWrites{0};
    uint32_t writeEpoch=0, lastWrite=0;
    uint32_t lastIo=0;
    bool updateSuspended=false;
    String error_, cardId;
    static Storage *instance;
    int32_t transfer(uint32_t lba, uint32_t offset, uint8_t *buffer, uint32_t size, bool write);
    bool walk(const String &path, unsigned depth, std::vector<Song> &songs);
};
