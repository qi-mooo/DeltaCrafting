#pragma once
#include "midi_file.h"
#include "harp_mapping.h"
#include <algorithm>
#include <vector>

namespace Harp {
// Original MIDI coordinates: seeking, the progress bar and saved start time
// keep their meaning. Only the playback clock jumps across a confirmed rest.
class SilenceMap {
public:
    static constexpr uint32_t MinThresholdMs=2000, MaxThresholdMs=60000;
    static constexpr uint32_t DefaultThresholdMs=3000, KeepMs=500;
    void clear() { gaps.clear(); voice.clear(); heard=false; silentAt=0; }
    void observe(const Event &event,int track,int channel) {
        if(event.track!=track || event.channel!=channel) return;
        bool sounding=voice.highest()>=0;
        voice.event(event.type,event.a,event.b);
        bool nextSounding=voice.highest()>=0;
        uint32_t at=event.us/1000;
        if(sounding && !nextSounding) silentAt=at;
        if(!sounding && nextSounding) {
            // Leading silence is already trimmed by the player. Do not infer
            // a rest from note-on spacing: held notes and pedal are still sound.
            if(heard && at>=silentAt && at-silentAt>MinThresholdMs) gaps.push_back({silentAt,at});
            heard=true;
        }
    }
    uint64_t target(uint64_t positionUs,bool enabled,uint32_t thresholdMs) const {
        if(!enabled) return positionUs;
        auto gap=std::upper_bound(gaps.begin(),gaps.end(),positionUs,
            [](uint64_t at,const Gap &g) { return at<uint64_t(g.end)*1000; });
        if(gap==gaps.end() || gap->end-gap->start<=thresholdMs
            || positionUs<uint64_t(gap->start+KeepMs)*1000) return positionUs;
        return uint64_t(gap->end)*1000;
    }
private:
    // Songs are bounded to one hour and gaps to >2 seconds: at most 1,799
    // entries (under 16 KiB of vector capacity), independent of event density.
    struct Gap { uint32_t start,end; };
    std::vector<Gap> gaps;
    Voice voice;
    uint32_t silentAt=0;
    bool heard=false;
};
}
