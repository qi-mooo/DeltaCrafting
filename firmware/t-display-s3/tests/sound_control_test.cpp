#include "sound_policy.h"
#include "sound_state.h"
#include <assert.h>
#include <initializer_list>

int main()
{
    SoundPolicy::UsbHostGate usb;
    assert(!usb.update(false, 0)); // Charger/battery alone never enables PCM.
    assert(!usb.update(true, 100));
    assert(!usb.update(true, 219));
    assert(usb.update(true, 220));
    assert(!usb.update(false, 221)); // Unplug/suspend closes PCM without waiting.
    assert(!usb.update(true, 222));
    assert(!usb.update(true, 341));
    assert(usb.update(true, 342));
    assert(!usb.update(false, UINT32_MAX - 200));
    assert(!usb.update(true, UINT32_MAX - 99));
    assert(!usb.update(true, 19));
    assert(usb.update(true, 20));

    StaticJsonDocument<512> doc;
    SoundControl::State state;
    assert(!SoundControl::fresh(state, 0));
    for (const char *payload : {"{\"muted\":true,\"volumePercent\":0}",
        "{\"muted\":false,\"volumePercent\":100,\"deviceName\":\"speaker\"}"}) {
        assert(!deserializeJson(doc, payload));
        SoundControl::apply(doc.as<JsonVariantConst>(), state);
        assert(state.online);
        assert(state.muted == doc["muted"].as<bool>() && state.volume == doc["volumePercent"].as<int>());
    }
    state.checkedAt = UINT32_MAX - 99;
    assert(SoundControl::fresh(state, 9899));
    assert(!SoundControl::fresh(state, 9900));
    for (const char *payload : {"{}", "{\"error\":\"unavailable\"}", "{\"muted\":false}",
        "{\"muted\":\"false\",\"volumePercent\":50}", "{\"muted\":false,\"volumePercent\":\"50\"}",
        "{\"muted\":false,\"volumePercent\":-1}", "{\"muted\":false,\"volumePercent\":101}"}) {
        assert(!deserializeJson(doc, payload));
        SoundControl::apply(doc.as<JsonVariantConst>(), state);
        assert(!state.online && !SoundControl::fresh(state, state.checkedAt));
    }
}
