#!/usr/bin/env python3
"""Configure a flashed DeltaCrafter S3 over USB without rebuilding or echoing secrets."""
import argparse
import getpass
import json
import time

import serial


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", required=True, help="COM5 or /dev/cu.usbmodem...")
    parser.add_argument("--wifi-ssid", required=True)
    parser.add_argument("--base-url", required=True, help="http://192.168.1.100:17890")
    args = parser.parse_args()
    payload = {
        "command": "configure",
        "wifiSsid": args.wifi_ssid,
        "wifiPassword": getpass.getpass("Wi-Fi password (hidden): "),
        "baseUrl": args.base_url.rstrip("/"),
        "apiKey": getpass.getpass("DeltaCrafter pairing key (hidden): ").strip(),
    }
    wire = json.dumps(payload, ensure_ascii=False).encode("utf-8") + b"\n"
    if len(wire) > 2048:
        raise SystemExit("Configuration is too long.")
    with serial.Serial(args.port, 115200, timeout=0.5, write_timeout=5) as device:
        time.sleep(2)
        device.reset_input_buffer()
        device.write(wire)
        device.flush()
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            try:
                response = json.loads(device.readline())
            except (ValueError, UnicodeDecodeError):
                continue
            if not isinstance(response, dict) or "ok" not in response:
                continue
            if response.get("ok") and response.get("restart"):
                print("Saved. The device is restarting and will connect automatically.")
                return
            raise SystemExit("Device rejected configuration: " + str(response.get("error", "unknown")))
    raise SystemExit("No acknowledgement. Check port/firmware and close other serial monitors.")


if __name__ == "__main__":
    try:
        main()
    except serial.SerialException as error:
        raise SystemExit(f"Serial connection failed: {error}") from None
