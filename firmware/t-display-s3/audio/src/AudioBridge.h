#pragma once

#include <Arduino.h>
#include <USB.h>
#include <USBAudioCard.h>
#include <WiFi.h>
#include <WiFiUdp.h>

#include "AudioProtocol.h"

struct AudioBridgeStats {
    bool serverDiscovered = false;
    bool usbMounted = false;
    bool usbStreaming = false;
    bool receivingAudio = false;
    bool buffering = true;
    uint16_t bufferMilliseconds = 0;
    uint32_t receivedPackets = 0;
    uint32_t missingPackets = 0;
    uint32_t bufferUnderruns = 0;
    uint32_t malformedPackets = 0;
    uint32_t usbDroppedBlocks = 0;
    uint8_t peakLevel = 0;
};

class AudioBridge {
public:
    AudioBridge();

    bool begin(const String &deviceId, const String &deviceName);
    bool hasServer() const;
    String serverBaseUrl() const;
    AudioBridgeStats getStats() const;

private:
    struct MonoFrame {
        int16_t sample;
    };

    static constexpr size_t RING_FRAME_COUNT = 32768;
    static constexpr uint32_t TARGET_BUFFER_FRAMES = 9600;
    static constexpr uint32_t MIN_BUFFER_FRAMES = 4800;
    static constexpr uint32_t SERVER_TIMEOUT_MS = 10000;
    static constexpr uint32_t AUDIO_TIMEOUT_MS = 750;
    static constexpr uint32_t LEVEL_TIMEOUT_MS = 500;
    static constexpr uint32_t PEAK_WINDOW_MS = 100;
    static constexpr uint64_t INVALID_TAG = UINT64_MAX;

    static void networkTaskEntry(void *argument);
    static void usbTaskEntry(void *argument);
    static void usbEventCallback(void *argument, esp_event_base_t eventBase, int32_t eventId, void *eventData);

    void networkTask();
    void usbTask();
    void serviceDiscovery();
    void serviceAudioStream();
    void serviceLevelStream();
    void replyToOffer(const IPAddress &remoteIp, uint16_t remotePort);
    void acceptAudioStreamBytes(const uint8_t *data, size_t byteCount);
    void resetAudioBufferLocked(uint32_t sessionId, uint32_t firstSampleIndex);
    void renderUsbBlock(MonoFrame *output, size_t frameCount);
    void setUsbStreaming(bool enabled);
    void setUsbMounted(bool mounted);
    void refreshUsbConnectionState();
    bool serverActiveLocked(uint32_t now) const;

    USBAudioCard _usbAudio;
    WiFiUDP _discoveryUdp;
    WiFiUDP _levelUdp;
    WiFiServer _audioServer{AudioProtocol::AUDIO_PORT};
    WiFiClient _audioClient;
    TaskHandle_t _networkTaskHandle = nullptr;
    TaskHandle_t _usbTaskHandle = nullptr;
    MonoFrame *_frames = nullptr;
    uint64_t *_frameTags = nullptr;

    mutable portMUX_TYPE _mux = portMUX_INITIALIZER_UNLOCKED;
    String _deviceId;
    String _deviceName;
    IPAddress _serverIp;
    uint16_t _serverHttpPort = 8765;
    uint32_t _lastOfferAt = 0;
    uint32_t _lastAudioAt = 0;
    uint32_t _lastLevelAt = 0;
    uint32_t _sessionId = 0;
    uint64_t _latestSampleIndex = 0;
    double _readPosition = 0.0;
    float _fadeGain = 0.0f;
    int16_t _concealSample = 0;
    float _concealGain = 0.0f;
    bool _buffering = true;
    bool _networkBound = false;
    bool _usbMounted = false;
    bool _usbStreaming = false;
    bool _usbSignalState = false;
    bool _usbSignalInitialized = false;
    uint32_t _usbSignalChangedAt = 0;
    uint32_t _receivedPackets = 0;
    uint32_t _missingFrames = 0;
    uint32_t _bufferUnderruns = 0;
    uint32_t _malformedPackets = 0;
    uint32_t _usbDroppedBlocks = 0;
    uint32_t _peakWindowStartedAt = 0;
    uint8_t _peakLevel = 0;
    uint8_t _pendingPcmByte = 0;
    bool _hasPendingPcmByte = false;
};
