#include "AudioBridge.h"

#include <WiFi.h>
#include <esp_heap_caps.h>
#include <tusb.h>

namespace {

constexpr size_t STREAM_READ_BYTES = 1024;
AudioBridge *activeBridge = nullptr;

int parseIntegerField(const String &json, const char *field, int fallback)
{
    String key = "\"";
    key += field;
    key += "\":";
    int position = json.indexOf(key);
    if (position < 0) {
        return fallback;
    }

    position += key.length();
    while (position < json.length() && isspace(static_cast<unsigned char>(json[position]))) {
        ++position;
    }

    int value = 0;
    bool foundDigit = false;
    while (position < json.length() && isdigit(static_cast<unsigned char>(json[position]))) {
        foundDigit = true;
        value = value * 10 + json[position] - '0';
        ++position;
    }
    return foundDigit ? value : fallback;
}

uint8_t peakToPercent(uint16_t peak)
{
    if (peak == 0) {
        return 0;
    }
    return static_cast<uint8_t>(constrain(
        (static_cast<uint32_t>(peak) * 100U) / 32767U, 0U, 100U));
}

} // namespace

AudioBridge::AudioBridge()
    : _usbAudio(
          AudioProtocol::SAMPLE_RATE,
          UAC_BPS_16,
          UAC_SPK_NONE,
          UAC_MIC_MONO)
{
}

bool AudioBridge::begin(const String &deviceId, const String &deviceName)
{
    activeBridge = this;
    _deviceId = deviceId;
    _deviceName = deviceName;

    _frames = static_cast<MonoFrame *>(heap_caps_malloc(
        RING_FRAME_COUNT * sizeof(MonoFrame), MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));
    _frameTags = static_cast<uint64_t *>(heap_caps_malloc(
        RING_FRAME_COUNT * sizeof(uint64_t), MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));

    if (_frames == nullptr || _frameTags == nullptr) {
        if (_frames != nullptr) {
            heap_caps_free(_frames);
        }
        if (_frameTags != nullptr) {
            heap_caps_free(_frameTags);
        }
        _frames = static_cast<MonoFrame *>(heap_caps_malloc(
            RING_FRAME_COUNT * sizeof(MonoFrame), MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT));
        _frameTags = static_cast<uint64_t *>(heap_caps_malloc(
            RING_FRAME_COUNT * sizeof(uint64_t), MALLOC_CAP_INTERNAL | MALLOC_CAP_8BIT));
    }

    if (_frames == nullptr || _frameTags == nullptr) {
        return false;
    }

    memset(_frames, 0, RING_FRAME_COUNT * sizeof(MonoFrame));
    for (size_t index = 0; index < RING_FRAME_COUNT; ++index) {
        _frameTags[index] = INVALID_TAG;
    }

    _usbAudio.onEvent(usbEventCallback);
    if (!_usbAudio.begin()) {
        return false;
    }

    USB.productName("T-Display S3 WiFi Microphone");
    USB.manufacturerName("LILYGO");
    USB.usbPower(250);
    USB.usbAttributes(0);
    USB.onEvent(usbEventCallback);
    if (!USB.begin()) {
        return false;
    }

    BaseType_t networkCreated = xTaskCreatePinnedToCore(
        networkTaskEntry,
        "audio-network",
        6144,
        this,
        4,
        &_networkTaskHandle,
        0);
    BaseType_t usbCreated = xTaskCreatePinnedToCore(
        usbTaskEntry,
        "audio-usb",
        4096,
        this,
        5,
        &_usbTaskHandle,
        1);

    return networkCreated == pdPASS && usbCreated == pdPASS;
}

bool AudioBridge::serverActiveLocked(uint32_t now) const
{
    return _lastOfferAt != 0 && now - _lastOfferAt <= SERVER_TIMEOUT_MS;
}

bool AudioBridge::hasServer() const
{
    portENTER_CRITICAL(&_mux);
    const bool active = serverActiveLocked(millis());
    portEXIT_CRITICAL(&_mux);
    return active;
}

String AudioBridge::serverBaseUrl() const
{
    IPAddress serverIp;
    uint16_t serverPort;
    bool active;

    portENTER_CRITICAL(&_mux);
    active = serverActiveLocked(millis());
    serverIp = _serverIp;
    serverPort = _serverHttpPort;
    portEXIT_CRITICAL(&_mux);

    if (!active) {
        return String();
    }

    String result = "http://";
    result += serverIp.toString();
    result += ':';
    result += serverPort;
    return result;
}

AudioBridgeStats AudioBridge::getStats() const
{
    AudioBridgeStats result;
    const uint32_t now = millis();

    portENTER_CRITICAL(&_mux);
    result.serverDiscovered = serverActiveLocked(now);
    result.usbMounted = _usbMounted;
    result.usbStreaming = _usbStreaming;
    result.receivingAudio = _lastAudioAt != 0 && now - _lastAudioAt <= AUDIO_TIMEOUT_MS;
    const bool receivingLevel = _lastLevelAt != 0 && now - _lastLevelAt <= LEVEL_TIMEOUT_MS;
    result.buffering = _buffering;
    const uint64_t readIndex = static_cast<uint64_t>(_readPosition);
    const uint64_t bufferedFrames = _latestSampleIndex > readIndex ? _latestSampleIndex - readIndex : 0;
    result.bufferMilliseconds = static_cast<uint16_t>(min<uint64_t>(
        bufferedFrames * 1000ULL / AudioProtocol::SAMPLE_RATE, 999ULL));
    result.receivedPackets = _receivedPackets;
    result.missingPackets = _missingFrames / AudioProtocol::STREAM_CHUNK_FRAMES;
    result.bufferUnderruns = _bufferUnderruns;
    result.malformedPackets = _malformedPackets;
    result.usbDroppedBlocks = _usbDroppedBlocks;
    result.peakLevel = result.receivingAudio || receivingLevel ? _peakLevel : 0;
    portEXIT_CRITICAL(&_mux);
    return result;
}

void AudioBridge::networkTaskEntry(void *argument)
{
    static_cast<AudioBridge *>(argument)->networkTask();
}

void AudioBridge::usbTaskEntry(void *argument)
{
    static_cast<AudioBridge *>(argument)->usbTask();
}

void AudioBridge::usbEventCallback(void *argument, esp_event_base_t eventBase, int32_t eventId, void *eventData)
{
    (void)argument;
    auto *bridge = activeBridge;
    if (bridge == nullptr) {
        return;
    }

    if (eventBase == ARDUINO_USB_EVENTS) {
        if (eventId == ARDUINO_USB_STOPPED_EVENT || eventId == ARDUINO_USB_SUSPEND_EVENT) {
            bridge->setUsbMounted(false);
            bridge->setUsbStreaming(false);
        }
        return;
    }

    if (eventBase == ARDUINO_USB_AUDIO_CARD_EVENTS
        && eventId == ARDUINO_USB_AUDIO_CARD_INTERFACE_ENABLE_EVENT) {
        const auto *data = static_cast<const arduino_usb_audio_card_event_data_t *>(eventData);
        if (data != nullptr && data->interface_enable.interface == UAC_INTERFACE_MIC) {
            bridge->setUsbStreaming(data->interface_enable.enable);
        }
    }
}

void AudioBridge::setUsbMounted(bool mounted)
{
    portENTER_CRITICAL(&_mux);
    _usbMounted = mounted;
    portEXIT_CRITICAL(&_mux);
}

void AudioBridge::setUsbStreaming(bool enabled)
{
    portENTER_CRITICAL(&_mux);
    _usbStreaming = enabled;
    if (!enabled) {
        _buffering = true;
        _fadeGain = 0.0f;
    }
    portEXIT_CRITICAL(&_mux);
}

void AudioBridge::refreshUsbConnectionState()
{
    const bool signal = tud_mounted() && !tud_suspended();
    const bool mounted = _usbHost.update(signal, millis());
    setUsbMounted(mounted);
    if (!signal) setUsbStreaming(false);
}

void AudioBridge::networkTask()
{
    for (;;) {
        refreshUsbConnectionState();
        if (WiFi.status() != WL_CONNECTED) {
            if (_networkBound) {
                _audioClient.stop();
                _audioServer.end();
                _pcmListening = false;
                _discoveryUdp.stop();
                _levelUdp.stop();
                _networkBound = false;
            }
            vTaskDelay(pdMS_TO_TICKS(200));
            continue;
        }

        if (!_networkBound) {
            const bool discoveryReady = _discoveryUdp.begin(AudioProtocol::DISCOVERY_PORT) == 1;
            const bool levelReady = _levelUdp.begin(AudioProtocol::LEVEL_PORT) == 1;
            _networkBound = discoveryReady && levelReady;
            if (!_networkBound) {
                _discoveryUdp.stop();
                _levelUdp.stop();
                vTaskDelay(pdMS_TO_TICKS(1000));
                continue;
            }
        }

        serviceDiscovery();
        serviceLevelStream();
        serviceAudioStream();
        vTaskDelay(pdMS_TO_TICKS(1));
    }
}

void AudioBridge::serviceDiscovery()
{
    int packetSize;
    while ((packetSize = _discoveryUdp.parsePacket()) > 0) {
        String payload;
        payload.reserve(packetSize + 1);
        while (_discoveryUdp.available()) {
            payload += static_cast<char>(_discoveryUdp.read());
        }

        if (payload.indexOf("tdisplay-audio") < 0
            || payload.indexOf("\"offer\"") < 0
            || parseIntegerField(payload, "version", -1) != AudioProtocol::VERSION) {
            continue;
        }

        const IPAddress remoteIp = _discoveryUdp.remoteIP();
        const uint16_t remotePort = _discoveryUdp.remotePort();
        const uint16_t httpPort = static_cast<uint16_t>(constrain(
            parseIntegerField(payload, "httpPort", 8765), 1, 65535));

        portENTER_CRITICAL(&_mux);
        if (serverActiveLocked(millis()) && _serverIp != remoteIp) {
            portEXIT_CRITICAL(&_mux);
            continue;
        }
        _serverIp = remoteIp;
        _serverHttpPort = httpPort;
        _lastOfferAt = millis();
        portEXIT_CRITICAL(&_mux);

        replyToOffer(remoteIp, remotePort);
    }
}

void AudioBridge::replyToOffer(const IPAddress &remoteIp, uint16_t remotePort)
{
    bool usbMounted;
    bool usbStreaming;
    portENTER_CRITICAL(&_mux);
    usbMounted = _usbMounted;
    usbStreaming = _usbStreaming;
    portEXIT_CRITICAL(&_mux);

    String reply;
    reply.reserve(280);
    reply += F("{\"protocol\":\"tdisplay-audio\",\"version\":3,\"type\":\"ready\",\"deviceId\":\"");
    reply += _deviceId;
    reply += F("\",\"deviceName\":\"");
    reply += _deviceName;
    reply += F("\",\"audioPort\":");
    reply += AudioProtocol::AUDIO_PORT;
    reply += F(",\"levelPort\":");
    reply += AudioProtocol::LEVEL_PORT;
    reply += F(",\"transport\":\"tcp\",\"sampleRate\":48000,\"channels\":1,\"bitsPerSample\":16,\"usbMounted\":");
    reply += usbMounted ? F("true") : F("false");
    reply += F(",\"usbStreaming\":");
    reply += usbStreaming ? F("true") : F("false");
    reply += '}';

    _discoveryUdp.beginPacket(remoteIp, remotePort);
    _discoveryUdp.write(reinterpret_cast<const uint8_t *>(reply.c_str()), reply.length());
    _discoveryUdp.endPacket();
}

void AudioBridge::serviceLevelStream()
{
    int packetSize;
    while ((packetSize = _levelUdp.parsePacket()) > 0) {
        uint8_t packet[AudioProtocol::LEVEL_PACKET_SIZE] = {};
        const int bytesRead = _levelUdp.read(packet, min<int>(packetSize, sizeof(packet)));
        while (_levelUdp.available()) {
            _levelUdp.read();
        }

        IPAddress serverIp;
        bool serverActive;
        portENTER_CRITICAL(&_mux);
        serverIp = _serverIp;
        serverActive = serverActiveLocked(millis());
        portEXIT_CRITICAL(&_mux);

        if (packetSize != AudioProtocol::LEVEL_PACKET_SIZE
            || bytesRead != AudioProtocol::LEVEL_PACKET_SIZE
            || !serverActive
            || _levelUdp.remoteIP() != serverIp
            || packet[0] != AudioProtocol::LEVEL_MAGIC_0
            || packet[1] != AudioProtocol::LEVEL_MAGIC_1
            || packet[2] != AudioProtocol::VERSION) {
            continue;
        }

        portENTER_CRITICAL(&_mux);
        _peakLevel = constrain(packet[3], 0, 100);
        _lastLevelAt = millis();
        portEXIT_CRITICAL(&_mux);
    }
}

void AudioBridge::serviceAudioStream()
{
    bool serverActive, usbMounted;
    IPAddress serverIp;
    portENTER_CRITICAL(&_mux);
    serverActive = serverActiveLocked(millis());
    serverIp = _serverIp;
    usbMounted = _usbMounted;
    portEXIT_CRITICAL(&_mux);

    if (!usbMounted) {
        _audioClient.stop();
        if (_pcmListening) {
            _audioServer.end();
            _pcmListening = false;
            _hasPendingPcmByte = false;
            portENTER_CRITICAL(&_mux);
            _lastAudioAt = 0;
            resetAudioBufferLocked(_sessionId + 1, 0);
            portEXIT_CRITICAL(&_mux);
        }
        return;
    }
    if (!_pcmListening) {
        _audioServer.begin();
        _audioServer.setNoDelay(true);
        _pcmListening = true;
    }

    if (_audioClient && (!_audioClient.connected() || !serverActive || _audioClient.remoteIP() != serverIp)) {
        _audioClient.stop();
        portENTER_CRITICAL(&_mux);
        _buffering = true;
        _fadeGain = 0.0f;
        portEXIT_CRITICAL(&_mux);
    }

    if (!_audioClient) {
        WiFiClient candidate = _audioServer.accept();
        if (!candidate) {
            return;
        }
        if (!serverActive || candidate.remoteIP() != serverIp) {
            candidate.stop();
            return;
        }
        candidate.setNoDelay(true);
        _audioClient = candidate;
        _hasPendingPcmByte = false;
        portENTER_CRITICAL(&_mux);
        resetAudioBufferLocked(_sessionId + 1, 0);
        portEXIT_CRITICAL(&_mux);
    }

    static uint8_t streamBuffer[STREAM_READ_BYTES];
    while (_audioClient.available() > 0) {
        const int received = _audioClient.read(streamBuffer, sizeof(streamBuffer));
        if (received <= 0) {
            break;
        }

        size_t offset = 0;
        if (_hasPendingPcmByte) {
            const uint8_t sampleBytes[2] = {_pendingPcmByte, streamBuffer[0]};
            acceptAudioStreamBytes(sampleBytes, sizeof(sampleBytes));
            _hasPendingPcmByte = false;
            offset = 1;
        }

        const size_t remaining = static_cast<size_t>(received) - offset;
        const size_t evenBytes = remaining & ~static_cast<size_t>(1);
        if (evenBytes > 0) {
            acceptAudioStreamBytes(streamBuffer + offset, evenBytes);
            offset += evenBytes;
        }
        if (offset < static_cast<size_t>(received)) {
            _pendingPcmByte = streamBuffer[offset];
            _hasPendingPcmByte = true;
        }
    }
}

void AudioBridge::resetAudioBufferLocked(uint32_t sessionId, uint32_t firstSampleIndex)
{
    _sessionId = sessionId;
    _latestSampleIndex = firstSampleIndex;
    _readPosition = firstSampleIndex;
    _buffering = true;
    _fadeGain = 0.0f;
    _concealSample = 0;
    _concealGain = 0.0f;
    _missingFrames = 0;
    _bufferUnderruns = 0;
    _peakWindowStartedAt = 0;
    _peakLevel = 0;
    for (size_t index = 0; index < RING_FRAME_COUNT; ++index) {
        _frameTags[index] = INVALID_TAG;
    }
}

void AudioBridge::acceptAudioStreamBytes(const uint8_t *data, size_t byteCount)
{
    if (byteCount == 0 || (byteCount & 1U) != 0) {
        return;
    }

    const size_t sampleCount = byteCount / sizeof(int16_t);
    uint16_t peak = 0;
    for (size_t index = 0; index < sampleCount; ++index) {
        const uint16_t raw = static_cast<uint16_t>(data[index * 2])
            | (static_cast<uint16_t>(data[index * 2 + 1]) << 8);
        const int16_t sample = static_cast<int16_t>(raw);
        peak = max<uint16_t>(peak, static_cast<uint16_t>(abs(static_cast<int32_t>(sample))));
    }

    portENTER_CRITICAL(&_mux);
    for (size_t index = 0; index < sampleCount; ++index) {
        const uint16_t raw = static_cast<uint16_t>(data[index * 2])
            | (static_cast<uint16_t>(data[index * 2 + 1]) << 8);
        const uint64_t sampleIndex = _latestSampleIndex++;
        const size_t ringIndex = sampleIndex % RING_FRAME_COUNT;
        _frames[ringIndex].sample = static_cast<int16_t>(raw);
        _frameTags[ringIndex] = sampleIndex;
    }

    const uint64_t readIndex = static_cast<uint64_t>(_readPosition);
    if (_latestSampleIndex - readIndex >= RING_FRAME_COUNT - AudioProtocol::STREAM_CHUNK_FRAMES) {
        _readPosition = static_cast<double>(_latestSampleIndex - TARGET_BUFFER_FRAMES);
        _buffering = true;
        _fadeGain = 0.0f;
    }

    const uint32_t now = millis();
    const uint8_t level = peakToPercent(peak);
    if (_peakWindowStartedAt == 0 || now - _peakWindowStartedAt >= PEAK_WINDOW_MS) {
        _peakWindowStartedAt = now;
        _peakLevel = level;
    } else {
        _peakLevel = max(_peakLevel, level);
    }
    _lastAudioAt = now;
    ++_receivedPackets;
    portEXIT_CRITICAL(&_mux);
}

void AudioBridge::renderUsbBlock(MonoFrame *output, size_t frameCount)
{
    portENTER_CRITICAL(&_mux);
    const uint64_t readIndex = static_cast<uint64_t>(_readPosition);
    uint64_t bufferedFrames = _latestSampleIndex > readIndex ? _latestSampleIndex - readIndex : 0;

    if (_buffering && bufferedFrames >= TARGET_BUFFER_FRAMES) {
        _buffering = false;
    } else if (!_buffering && bufferedFrames < MIN_BUFFER_FRAMES) {
        _buffering = true;
        ++_bufferUnderruns;
    }

    if (_buffering) {
        for (size_t index = 0; index < frameCount; ++index) {
            output[index] = {0};
        }
        _fadeGain = max(0.0f, _fadeGain - static_cast<float>(frameCount) / 48.0f);
        portEXIT_CRITICAL(&_mux);
        return;
    }

    const double error = static_cast<double>(bufferedFrames) - TARGET_BUFFER_FRAMES;
    const double ratioCorrection = constrain(
        error / static_cast<double>(TARGET_BUFFER_FRAMES) * 0.0015,
        -0.0015,
        0.0015);
    const double inputStep = 1.0 + ratioCorrection;

    for (size_t outputIndex = 0; outputIndex < frameCount; ++outputIndex) {
        const uint64_t inputIndex = static_cast<uint64_t>(_readPosition);
        const double fraction = _readPosition - static_cast<double>(inputIndex);
        const size_t ring0 = inputIndex % RING_FRAME_COUNT;
        const size_t ring1 = (inputIndex + 1) % RING_FRAME_COUNT;
        const bool valid0 = _frameTags[ring0] == inputIndex;
        const bool valid1 = _frameTags[ring1] == inputIndex + 1;

        MonoFrame frame{0};
        if (valid0 && valid1) {
            frame.sample = static_cast<int16_t>(
                _frames[ring0].sample + (_frames[ring1].sample - _frames[ring0].sample) * fraction);
            _concealSample = frame.sample;
            _concealGain = 1.0f;
        } else if (valid0) {
            frame = _frames[ring0];
            _concealSample = frame.sample;
            _concealGain = 1.0f;
        } else {
            ++_missingFrames;
            frame.sample = static_cast<int16_t>(_concealSample * _concealGain);
            _concealGain *= 0.98f;
            if (_concealGain < 0.001f) {
                _concealGain = 0.0f;
            }
        }

        _fadeGain = min(1.0f, _fadeGain + 1.0f / 240.0f);
        output[outputIndex].sample = static_cast<int16_t>(frame.sample * _fadeGain);

        _readPosition += inputStep;
        const uint64_t nextInputIndex = static_cast<uint64_t>(_readPosition);
        if (nextInputIndex > inputIndex) {
            _frameTags[ring0] = INVALID_TAG;
        }
    }
    portEXIT_CRITICAL(&_mux);
}

void AudioBridge::usbTask()
{
    MonoFrame output[48];
    TickType_t lastWake = xTaskGetTickCount();

    for (;;) {
        vTaskDelayUntil(&lastWake, pdMS_TO_TICKS(1));

        bool streaming;
        portENTER_CRITICAL(&_mux);
        streaming = _usbMounted && _usbStreaming;
        portEXIT_CRITICAL(&_mux);
        if (!streaming) {
            vTaskDelay(pdMS_TO_TICKS(10));
            lastWake = xTaskGetTickCount();
            continue;
        }

        renderUsbBlock(output, sizeof(output) / sizeof(output[0]));
        const uint16_t bytes = sizeof(output);
        if (_usbAudio.write(output, bytes) != bytes) {
            portENTER_CRITICAL(&_mux);
            ++_usbDroppedBlocks;
            portEXIT_CRITICAL(&_mux);
        }
    }
}
