#pragma once
#include "midi_file.h"
#include "harp_mapping.h"

namespace Harp {
// Rebuild the selected channel, including held notes and sustain, without HID.
// next is the first event after target; false means the cursor reached the end.
inline bool seekEvents(MidiFile &midi,Voice &voice,Event &next,uint64_t target,int track,int channel) {
    voice.clear(); midi.rewind();
    while(midi.next(next)) {
        if(next.us>target) return true;
        if(next.track==track && next.channel==channel) voice.event(next.type,next.a,next.b);
    }
    return false;
}
}
