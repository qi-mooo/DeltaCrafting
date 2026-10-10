#!/usr/bin/env python3
"""Provision and control the DeltaHarp USB player (Python 3 + pyserial)."""
import argparse
import base64
import getpass
import hashlib
import json
import os
from pathlib import Path
import struct
import time
import urllib.error
import urllib.request
import zipfile


def serial_request(port, payload, timeout=10):
    import serial
    device = serial.Serial(port=None, baudrate=115200, timeout=0.3, write_timeout=3)
    device.dtr = True
    device.rts = False
    device.port = port
    with device:
        time.sleep(0.3)
        device.reset_input_buffer()
        device.write(json.dumps(payload, ensure_ascii=False).encode() + b"\n")
        device.flush()
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            try:
                reply = json.loads(device.readline())
            except (ValueError, UnicodeDecodeError):
                continue
            if isinstance(reply, dict) and "ok" in reply:
                if not reply["ok"]:
                    raise RuntimeError(reply.get("error", "device_rejected_command"))
                return reply
    raise RuntimeError("Serial response timed out; close other serial monitors.")


def save_config(path, value):
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w") as stream:
        if hasattr(os, "fchmod"):
            os.fchmod(stream.fileno(), 0o600)
        json.dump(value, stream, indent=2)
        stream.write("\n")


def api(config, path, body=None, timeout=8):
    request = urllib.request.Request(
        config["url"].rstrip("/") + "/api/v1/" + path,
        data=None if body is None else json.dumps(body).encode(),
        headers={"Authorization": "Bearer " + config["apiKey"], "Content-Type": "application/json"},
    )
    # Local device requests must not pass through a desktop proxy.
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    try:
        with opener.open(request, timeout=timeout) as response:
            return json.load(response)
    except urllib.error.HTTPError as error:
        raise RuntimeError(f"HTTP {error.code}: {error.read().decode()}") from None


def update_firmware(config, path):
    if zipfile.is_zipfile(path):
        with zipfile.ZipFile(path) as bundle:
            data = bundle.read("firmware.bin")
            manifest = json.loads(bundle.read("ota-manifest.json"))
        if manifest.get("board") != "esp32-s3-dongle-fn8" or manifest.get("layout") != "dual-8mb-v1":
            raise RuntimeError("Wrong firmware target or flash layout.")
        if manifest.get("sha256") != hashlib.sha256(data).hexdigest() or manifest.get("size") != len(data):
            raise RuntimeError("Firmware bundle checksum mismatch.")
    else:
        data = path.read_bytes()
    marker = b"DeltaHarp:esp32-s3-dongle-fn8:dual-8mb-v1:app"
    if len(data)<288 or data[0]!=0xe9 or data[12:14]!=b"\x09\x00" or marker not in data:
        raise RuntimeError("Not a DeltaHarp USB player application image.")
    session = api(config, "firmware/begin", {"board": "esp32-s3-dongle-fn8", "layout": "dual-8mb-v1",
                  "size": len(data), "sha256": hashlib.sha256(data).hexdigest()}, timeout=30)
    identity = {"updateId": session["updateId"]}
    committed = False
    try:
        chunk_size = session["chunkBytes"]
        for offset in range(0, len(data), chunk_size):
            chunk = data[offset:offset+chunk_size]
            payload = dict(identity, offset=offset, data=base64.b64encode(chunk).decode())
            try:
                result = api(config, "firmware/chunk", payload)
            except (OSError, RuntimeError):
                result = api(config, "firmware")
                if result.get("updateId") != identity["updateId"]:
                    raise RuntimeError("Update session lost.") from None
                if result["received"] == offset:
                    result = api(config, "firmware/chunk", payload)
                elif result["received"] != offset + len(chunk):
                    raise RuntimeError("Unexpected update offset.") from None
            if result["received"] != offset + len(chunk):
                raise RuntimeError("Chunk acknowledgement mismatch.")
            if offset // chunk_size % 24 == 0:
                print(f"Uploaded {result['received']} / {len(data)} bytes", flush=True)
        committed = True  # A lost response can still mean the boot slot was selected.
        try:
            result = api(config, "firmware/commit", identity, timeout=20)
        except OSError:
            result = {"ok": True, "restart": True, "message": "Commit response lost; checking reboot."}
        time.sleep(2)
        deadline = time.monotonic() + 35
        while time.monotonic() < deadline:
            try:
                status = api(config, "status", timeout=2)
                if status.get("update", {}).get("phase") == "idle":
                    running = status["update"]
                    if running.get("partition") == session.get("partition"):
                        raise RuntimeError("Device returned without switching the OTA partition.")
                    return {"ok": True, "firmware": status["firmware"], "partition": running["partition"], "restarted": True}
            except OSError:
                pass
            time.sleep(1)
        raise RuntimeError("Image committed, but reboot was not verified. Check serial info before retrying.")
    finally:
        if not committed:
            try:
                api(config, "firmware/abort", identity)
            except (OSError, RuntimeError):
                pass


def vlq(n):
    data = [n & 127]
    while n > 127:
        n >>= 7
        data.insert(0, (n & 127) | 128)
    return bytes(data)


def demo(path):
    # C major plus low/high/sharp combinations, 120 BPM, 480 ticks/quarter.
    track = bytearray(b"\x00\xff\x51\x03\x07\xa1\x20")
    for pitch in (60, 62, 64, 65, 67, 69, 71, 72, 48, 49, 73, 84):
        track += b"\x00\x90" + bytes((pitch, 100))
        track += vlq(360) + b"\x80" + bytes((pitch, 0))
        track += vlq(120) + b"\xff\x01\x00"
    track += b"\x00\xff\x2f\x00"
    data = b"MThd" + struct.pack(">IHHH", 6, 0, 1, 480)
    data += b"MTrk" + struct.pack(">I", len(track)) + track
    with open(path, "xb") as stream:
        stream.write(data)
    return {"ok": True, "file": str(path), "bytes": len(data), "notes": 12}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, default=Path(__file__).with_name("player.local.json"))
    parser.add_argument("--port", help="USB CDC port, e.g. /dev/cu.usbmodem... or COM5")
    parser.add_argument("--url", help="Override player URL, e.g. http://192.168.4.1")
    sub = parser.add_subparsers(dest="command", required=True)
    for name in ("info", "pair", "status", "pause", "resume", "stop", "refresh", "usb"):
        sub.add_parser(name)
    songs = sub.add_parser("songs")
    songs.add_argument("--offset", type=int, default=0)
    wifi = sub.add_parser("wifi")
    wifi.add_argument("ssid")
    screen = sub.add_parser("pair-display")
    screen.add_argument("--direct", action="store_true", help="Connect the display directly to the player AP")
    make = sub.add_parser("make-demo")
    make.add_argument("path", type=Path)
    sub.add_parser("firmware")
    update = sub.add_parser("update")
    update.add_argument("image", type=Path, help="USB player firmware.bin or DeltaHarp ZIP bundle")
    play = sub.add_parser("play")
    play.add_argument("file", help="Absolute path on the SD card")
    play.add_argument("--countdown", type=int, default=3)
    play.add_argument("--track", type=int, default=-1)
    play.add_argument("--channel", type=int, default=-1)
    play.add_argument("--dry-run", action="store_true", help="Run timing and mapping without pressing keys")
    settings = sub.add_parser("settings")
    for p in (play, settings):
        p.add_argument("--speed", type=int, choices=range(50, 201), metavar="50..200")
        p.add_argument("--transpose", type=int, choices=range(-24, 25), metavar="-24..24")
        p.add_argument("--loop", action=argparse.BooleanOptionalAction, default=None)
    args = parser.parse_args()
    if args.command == "make-demo":
        result = demo(args.path)
    elif args.command in ("info", "pair", "wifi"):
        if not args.port:
            parser.error("--port is required for this command")
        if args.command == "info":
            result = serial_request(args.port, {"command": "info"})
        elif args.command == "wifi":
            result = serial_request(args.port, {"command": "configure", "ssid": args.ssid,
                                                 "password": getpass.getpass("Wi-Fi password: ")})
        else:
            result = serial_request(args.port, {"command": "pairing"})
            status = serial_request(args.port, {"command": "info"})
            ip = status["ip"] if status["ip"] != "0.0.0.0" else status["apIp"]
            result["url"] = args.url or "http://" + ip
            save_config(args.config, result)
            result = {"ok": True, "config": str(args.config), "url": result["url"],
                      "apSsid": result["apSsid"], "message": "Credentials saved locally; not printed."}
    else:
        config = {} if args.port and args.command != "pair-display" else json.loads(args.config.read_text())
        if args.url:
            config["url"] = args.url
        if args.command in ("firmware", "update"):
            if args.port:
                parser.error("Firmware updates use HTTP; omit --port")
            result = api(config, "firmware") if args.command == "firmware" else update_firmware(config, args.image)
        elif args.command == "pair-display":
            if not args.port:
                parser.error("--port must identify the T-Display, not the player")
            target = serial_request(args.port, {"command": "info"})
            if not str(target.get("firmware", "")).startswith("axeuh-tools-"):
                raise RuntimeError("This serial port is not a DeltaCrafter T-Display.")
            url = "http://192.168.4.1" if args.direct else config["url"]
            result = serial_request(args.port, {"command": "harp-configure", "playerUrl": url,
                                                "playerKey": config["apiKey"]})
            if args.direct:
                time.sleep(4)
                result = serial_request(args.port, {"command": "wifi-configure", "wifiSsid": config["apSsid"],
                                                    "wifiPassword": config["apPassword"]})
        elif args.command == "status":
            result = serial_request(args.port, {"command": "info"}) if args.port else api(config, "status")
        elif args.command == "songs":
            result = serial_request(args.port, {"command": "songs", "offset": args.offset}) if args.port else api(config, f"songs?offset={args.offset}")
        else:
            body = {}
            if args.command in ("play", "settings"):
                for key in ("speed", "transpose", "loop"):
                    if getattr(args, key) is not None:
                        body[key] = getattr(args, key)
            if args.command == "play":
                body.update(file=args.file, countdown=args.countdown, track=args.track,
                            channel=args.channel, dryRun=args.dry_run)
            path = {"refresh": "library/refresh", "usb": "storage/usb"}.get(args.command, args.command)
            if args.port:
                body.update(command="control", action=args.command)
                result = serial_request(args.port, body)
            else:
                result = api(config, path, body)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError) as error:
        raise SystemExit(str(error)) from None
