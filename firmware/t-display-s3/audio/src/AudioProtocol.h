#pragma once

#include <Arduino.h>

namespace AudioProtocol {

constexpr uint8_t VERSION = 3;
constexpr uint16_t DISCOVERY_PORT = 40100;
constexpr uint16_t AUDIO_PORT = 40101;
constexpr uint16_t LEVEL_PORT = 40102;
constexpr uint8_t LEVEL_MAGIC_0 = 'T';
constexpr uint8_t LEVEL_MAGIC_1 = 'L';
constexpr uint8_t LEVEL_PACKET_SIZE = 4;
constexpr uint32_t SAMPLE_RATE = 48000;
constexpr uint8_t CHANNELS = 1;
constexpr uint8_t BITS_PER_SAMPLE = 16;
constexpr uint16_t STREAM_CHUNK_FRAMES = 240;
constexpr uint16_t STREAM_CHUNK_BYTES = STREAM_CHUNK_FRAMES * CHANNELS * sizeof(int16_t);

} // namespace AudioProtocol
