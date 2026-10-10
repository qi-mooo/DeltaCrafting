# DeltaHarp USB MIDI Player

已完成的硬件实测与尚未验证项见 [本次验证记录](VERIFIED.md)。
日常使用：复制 MIDI 到 `DELTAHARP` → 电脑弹出磁盘 → 屏幕刷新曲库 → 选择并播放。
USB 板已支持 HTTP 固件更新；屏幕固件在原有 T-Display 项目中新增口琴播放器菜单。

ESP32-S3 Dongle reads MIDI from its onboard SD card and outputs USB keyboard and
mouse reports using the HarpAutoPlayer note mapping. The computer does not parse
or schedule the MIDI. The existing DeltaCrafter T-Display-S3 firmware controls it
over Wi-Fi from Tools > Harp Player.

## Hardware

Verified against `ESP32-S3-Dongle.pdf`, board v1.0g, ESP32-S3FN8, 8 MB flash,
no PSRAM. Connect the native USB plug to the target computer.

| SD signal | GPIO |
| --- | --- |
| CLK | 36 |
| CMD / SPI MOSI | 35 |
| D0 / SPI MISO | 37 |
| D1 | 38 |
| D2 | 33 |
| D3 / SPI CS | 34 |

Default: SDMMC 1-bit using GPIO 36/35/37. Do not apply these pins to unrelated
S3 boards, especially versions with octal PSRAM. The SD library supports exFAT
and FAT32, UTF-8 names, subdirectories up to three levels, and 500 songs maximum.

## Copy and play

1. Plug in the Dongle. Its SD card appears as a USB removable disk alongside
   keyboard, mouse and serial interfaces. It starts in USB reader mode.
2. Copy `.mid`, `.midi` or `.kar` files to the card. **Eject the whole disk in the
   operating system**, leaving the Dongle plugged in.
3. On the display open Tools > Harp Player, refresh the library, select a song,
   and play. GPIO 14 cycles rows and GPIO 0 confirms, matching existing menus.
4. The default three-second countdown gives time to focus the game's instrument.
   Pause releases all keys and mouse buttons. Resume restores the held note.
5. To copy more music, choose USB reader on the display or send `usb`. Playback
   stops, local files close, and the disk becomes visible on the computer again.

Only one owner accesses the filesystem at a time. A play/refresh request while
the computer owns the disk returns `eject_sd_on_computer_first`. Unmounting only
a partition may not send a USB media-eject command; on macOS use `diskutil eject
/dev/diskN` after confirming that disk is the Dongle. Reboot also returns to reader
mode. Eject before firmware updates or Wi-Fi provisioning, which restart USB.

## Pairing and control

Run these from this directory with Python 3.9+ and `pyserial` installed:

```sh
python3 -m pip install pyserial
python3 control.py --port /dev/cu.usbmodemXXXX info
python3 control.py --port /dev/cu.usbmodemXXXX pair
```

`pair` writes `player.local.json` with owner-only permissions. It contains the
API key, AP name and AP password, and is gitignored. The player creates a private
2.4 GHz AP named `DeltaHarp-<device suffix>` at `192.168.4.1`. Its credentials
are generated on first boot and kept in NVS. They are never included in firmware.

Flash the updated T-Display firmware and use its own USB serial port:

```sh
python3 control.py --port /dev/cu.DISPLAY pair-display --direct
```

This pairs the screen and joins it directly to the Dongle's AP; no router or
desktop DeltaCrafter API is required for the player. Direct mode changes the
screen's Wi-Fi network. Existing desktop monitoring still needs its original
network and desktop API. To keep both available, put both boards on the same
2.4 GHz LAN instead:

```sh
python3 control.py --port /dev/cu.PLAYER wifi YourNetwork
python3 control.py --port /dev/cu.PLAYER pair
python3 control.py --port /dev/cu.DISPLAY pair-display
```

Configure the screen's Wi-Fi with its existing setup tool or the new
`{"command":"wifi-configure","wifiSsid":"...","wifiPassword":"..."}` command.
The Dongle Wi-Fi password is entered hidden at the CLI. On a LAN, reserve its
IP in the router or supply a stable URL with `--url http://delta-harp.local`.

CLI commands use Wi-Fi by default. Adding `--port` uses USB serial instead,
which is also useful for a computer without a working Wi-Fi adapter:

```sh
python3 control.py status
python3 control.py refresh
python3 control.py songs
python3 control.py play /song.mid --speed 100 --countdown 3
python3 control.py pause
python3 control.py resume
python3 control.py stop
python3 control.py settings --speed 125 --loop
python3 control.py usb
python3 control.py --port /dev/cu.PLAYER play /song.mid --dry-run --countdown 0
python3 control.py make-demo /path/to/SD/DeltaHarp-Test.mid
```

`--dry-run` executes parsing, scheduling and mapping without pressing keys;
status exposes `emittedNotes`. Accepted commands run asynchronously: poll status
for `busy`, `state` and `error`. A normal play request always restores HID output.
Speed, transpose and loop persist across songs until reboot; dry-run does not.

## HTTP API

All routes require `Authorization: Bearer <apiKey>`. This is a local HTTP API;
use a trusted LAN or the WPA2-protected player AP.

| Method | Path | Body / result |
| --- | --- | --- |
| GET | `/api/v1/status` | State, error, timing, selected track/channel, SD owner |
| GET | `/api/v1/songs?offset=0` | 32 songs per page; total and absolute SD paths |
| POST | `/api/v1/library/refresh` | Claim ejected SD and scan songs |
| POST | `/api/v1/play` | `{"file":"/song.mid","countdown":3}` |
| POST | `/api/v1/pause` | Release HID, retain position |
| POST | `/api/v1/resume` | Restore the current note and continue |
| POST | `/api/v1/stop` | Release HID and cancel queued playback |
| POST | `/api/v1/settings` | Optional `speed`, `transpose`, `loop` |
| POST | `/api/v1/storage/usb` | Stop and export SD to the computer |


Play options: `speed` 50..200 percent, `transpose` -24..24 semitones, `loop`
boolean, `countdown` 0..30 seconds, `track` -1 for automatic or 0..31,
`channel` -1 for automatic or 0..15, and `dryRun` boolean. HTTP 202 means queued,
not completed. Invalid bodies return 400, missing authentication 401, unavailable
USB/host-owned storage 409, and full command queues 503.

Serial equivalents are newline-delimited JSON. Local USB possession authorizes
control; serial does not require a key:

```json
{"command":"info"}
{"command":"songs","offset":0}
{"command":"control","action":"refresh"}
{"command":"control","action":"play","file":"/song.mid","countdown":3}
{"command":"control","action":"pause"}
{"command":"control","action":"resume"}
{"command":"control","action":"stop"}
{"command":"control","action":"usb"}
```

Formatting is deliberately a separate local command. After OS eject, obtain
`cardId` from `info` and send the exact identity:

```json
{"command":"format-exfat","cardId":"<actual cardId>","confirm":"ERASE_SD_CARD"}
```

This erases the card. Poll status for completion and return it to USB reader
mode. Alternatively format the identified removable disk with the OS exFAT tool.

## Firmware update over HTTP

From `delta-harp-v2`, the USB player also supports system firmware updates over
the authenticated API. Eject the SD disk in the OS first, then run:

```sh
python3 control.py firmware
python3 control.py update .pio/build/esp32-usb-player/DeltaHarp-esp32s3-usb.zip
# Or upload just the player application image:
python3 control.py update .pio/build/esp32-usb-player/firmware.bin
```

The CLI uploads 4096-byte chunks, verifies the whole-file SHA-256, commits, and
checks that the device returns on the other application partition. No USB cable
replug or download mode is needed. Existing v1 players need one USB installation
of v2 to gain the OTA API.

| Method | Path | JSON body |
| --- | --- | --- |
| GET | `/api/v1/firmware` | Version, partition, progress, error, size limit |
| POST | `/api/v1/firmware/begin` | `board`, `layout`, `size`, `sha256` |
| POST | `/api/v1/firmware/chunk` | `updateId`, `offset`, `data` (base64, ≤4096 decoded bytes) |
| POST | `/api/v1/firmware/commit` | `updateId` |
| POST | `/api/v1/firmware/abort` | `updateId` |

Begin uses `board="esp32-s3-dongle-fn8"` and `layout="dual-8mb-v1"` and returns
an `updateId`. Offsets must exactly match `received`; query progress to resolve a
lost response before retrying. After 30 seconds without an accepted session
request, the unfinished update is aborted and playback controls become available.

Begin stops playback, releases HID, and blocks playback/storage/configuration
changes until commit or abort. Data is written only to the inactive app slot.
Commit checks size, SHA-256, the embedded player/board/layout marker and ESP-IDF's
image checksum/chip validation before changing the boot partition. Wrong-board,
truncated or corrupt images never become the selected boot image. An interrupted
upload leaves the previous application selected; abort does not erase it.

OTA updates the application, preserving NVS pairing/Wi-Fi configuration, bootloader,
partition table and SD card. Reboot returns to USB reader mode. This build does
not enable automatic rollback after a valid image boots but later crashes; a
bad application can require USB recovery. The board marker prevents accidental
cross-flashing, and is not a cryptographic firmware signature. Only a paired
controller on the trusted LAN/AP should be given the API key.

## MIDI behavior and limits

Uses the vendored TinyMidiLoader parser with bounded allocation and malformed
input checks. Supports SMF 0/1 with PPQN timing and tempo changes, up to 32 tracks,
512 KiB file size, 160 KiB parser allocations and one hour of music. Available
heap may impose a lower practical limit. SMPTE, format 2 and RIFF/RMI wrappers
are rejected. Large arrangements should be exported as a single melody track.

Automatic selection chooses one melodic track/channel, excludes MIDI channel 10,
then chooses the octave covering most notes. Chords reduce to the highest active
pitch; sustain is honored. Track recommendation is a heuristic and differs from
the Windows program. The firmware does not implement its multi-track arrangement
or vocal-contour preprocessing.

| Note class | HID key | Mouse modifier |
| --- | --- | --- |
| C D E F G A B | Z X C V B N M | None at base octave |
| Sharp / flat equivalent | Same natural key | Middle button |
| One octave below | Same key | Left button |
| One octave above | Same key | Right button |
| Top C / C-sharp | Comma | Right, plus middle for sharp |

The selected base covers 38 semitones. Out-of-range pitches produce no key.
Modifiers precede key-down by 12 ms; notes shorter than that preparation window
can be skipped. Timing is millisecond-resolution and may jitter under USB load.
USB disconnect/suspend pauses playback. This reproduces the mapping, not every
behavior of the original Windows executable. Game acceptance and audible timing
still require testing on the actual game computer.

## Build and recovery

```sh
pio run -d firmware/esp32-usb-player
python3 firmware/esp32-usb-player/tests/run_native.py
python3 firmware/esp32-usb-player/package.py
TDISPLAY_S3_DIR=/path/to/T-Display-S3 pio run -d firmware/t-display-s3/audio
TDISPLAY_S3_DIR=/path/to/T-Display-S3 pio run -d firmware/t-display-s3
python3 firmware/t-display-s3/tests/run_native.py
```

Run build commands from the repository root. USB bundle:
`firmware/esp32-usb-player/.pio/build/esp32-usb-player/DeltaHarp-esp32s3-usb.zip`.
Display bundle: `firmware/t-display-s3/.pio/build/deltacrafter-monitor/DeltaCrafter-esp32s3.zip`.
These are different firmware targets; never flash the screen image onto the Dongle.

The USB bundle includes a checksum-verified flash script, source, ELF and exact
offsets. Install `esptool==4.5.1` and `pyserial==3.5`, put the Dongle into download
mode (hold BOOT, press RESET, release BOOT), then run `python flash.py --port ...`.
Press RESET after flashing. For an already-running player, opening its CDC port at
1200 baud enters the ROM downloader; the serial port name may change. With
esptool 5, `--after watchdog-reset` starts this FN8 board reliably after writing.

Initial full-flash backup from the connected board is retained locally under
`backups/68ee8f6d4a44-original.bin`; it is excluded from source and release bundles.
No firmware operation automatically formats an SD card.
